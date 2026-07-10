namespace Web10.Radio.API

open System
open System.Buffers
open System.Security.Claims
open System.Text.Encodings.Web
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.ServerSentEvents
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Funogram.Telegram.Types
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authorization
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Microsoft.Extensions.Primitives
open Npgsql
open Web10.Radio.Database
open Web10.Radio.Database.Repositories
open Web10.Radio.Telegram

[<RequireQualifiedAccess>]
module AdminAuthentication =
    [<Literal>]
    let SchemeName = "Web10AdminBearer"

    [<Literal>]
    let PolicyName = "Web10Admin"

    let fixedTimeEqualsUtf8 (expected: string) (actual: string) =
        let hash value =
            value
            |> fun candidate -> if isNull candidate then String.Empty else candidate
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData

        CryptographicOperations.FixedTimeEquals(hash expected, hash actual)

type AdminBearerAuthenticationHandler
    (
        options: IOptionsMonitor<AuthenticationSchemeOptions>,
        loggerFactory: ILoggerFactory,
        encoder: UrlEncoder,
        adminOptions: AdminOptions,
        contextAccessor: IHttpContextAccessor
    ) =
    inherit AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)

    override this.HandleAuthenticateAsync() =
        let mutable values = StringValues()

        let result =
            if not (this.Request.Headers.TryGetValue("Authorization", &values)) || values.Count <> 1 then
                AuthenticateResult.NoResult()
            else
                let authorization = values[0]
                let prefix = "Bearer "

                if isNull authorization
                   || authorization.Contains(',', StringComparison.Ordinal)
                   || not (authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) then
                    AuthenticateResult.Fail("A single bearer admin token is required.")
                else
                    let suppliedToken = authorization.Substring(prefix.Length)

                    if String.IsNullOrEmpty suppliedToken || not (AdminAuthentication.fixedTimeEqualsUtf8 adminOptions.Token suppliedToken) then
                        AuthenticateResult.Fail("The bearer admin token is invalid.")
                    else
                        let identity = ClaimsIdentity([| Claim(ClaimTypes.NameIdentifier, "admin") |], this.Scheme.Name)
                        let principal = ClaimsPrincipal(identity)
                        AuthenticateResult.Success(AuthenticationTicket(principal, this.Scheme.Name))

        Task.FromResult(result)

    override this.HandleChallengeAsync(properties) =
        task {
            let context = contextAccessor.HttpContext
            context.Response.Headers.WWWAuthenticate <- "Bearer"

            do!
                ApiProblems.write
                    context
                    StatusCodes.Status401Unauthorized
                    "admin.auth.required"
                    "Admin authentication required"
                    "A valid bearer admin token is required."
        }
        :> Task

[<RequireQualifiedAccess>]
module StreamNodeAuthentication =
    [<Literal>]
    let SchemeName = "Web10StreamNodeBearer"

    [<Literal>]
    let PolicyName = "Web10StreamNode"

type StreamNodeBearerAuthenticationHandler
    (
        options: IOptionsMonitor<AuthenticationSchemeOptions>,
        loggerFactory: ILoggerFactory,
        encoder: UrlEncoder,
        streamOptions: StreamOptions,
        contextAccessor: IHttpContextAccessor
    ) =
    inherit AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)

    override this.HandleAuthenticateAsync() =
        let mutable values = StringValues()

        let result =
            if not (this.Request.Headers.TryGetValue("Authorization", &values)) || values.Count <> 1 then
                AuthenticateResult.NoResult()
            else
                let authorization = values[0]
                let prefix = "Bearer "

                if isNull authorization
                   || authorization.Contains(',', StringComparison.Ordinal)
                   || not (authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) then
                    AuthenticateResult.Fail("A single bearer stream-node token is required.")
                else
                    let suppliedToken = authorization.Substring(prefix.Length)

                    if String.IsNullOrEmpty suppliedToken
                       || not (AdminAuthentication.fixedTimeEqualsUtf8 streamOptions.CallbackToken suppliedToken) then
                        AuthenticateResult.Fail("The bearer stream-node token is invalid.")
                    else
                        let identity = ClaimsIdentity([| Claim(ClaimTypes.NameIdentifier, "stream-node") |], this.Scheme.Name)
                        let principal = ClaimsPrincipal(identity)
                        AuthenticateResult.Success(AuthenticationTicket(principal, this.Scheme.Name))

        Task.FromResult(result)

    override this.HandleChallengeAsync(properties) =
        task {
            let context = contextAccessor.HttpContext
            context.Response.Headers.WWWAuthenticate <- "Bearer"

            do!
                ApiProblems.write
                    context
                    StatusCodes.Status401Unauthorized
                    "stream-node.auth.required"
                    "Stream-node authentication required"
                    "A valid bearer stream-node callback token is required."
        }
        :> Task

[<RequireQualifiedAccess>]
module ApiRouteLog =
    let private completedMessage =
        LoggerMessage.Define<string, int, string, string, double>(
            LogLevel.Information,
            EventId(3000, "ApiRouteCompleted"),
            "API route {Route} completed with {Status} traceId {TraceId} correlationId {CorrelationId} in {ElapsedMs} ms"
        )
    let private failedMessage =
        LoggerMessage.Define<string, int, string, string>(
            LogLevel.Error,
            EventId(3001, "ApiRouteFailed"),
            "API route {Route} failed with wire status {Status} traceId {TraceId} correlationId {CorrelationId}"
        )


    let private correlationId (context: HttpContext) =
        let mutable values = StringValues()

        if context.Request.Headers.TryGetValue("X-Correlation-Id", &values) then
            let rendered = values.ToString()

            if String.IsNullOrWhiteSpace rendered then String.Empty else rendered
        else
            String.Empty

    let completed (logger: ILogger) (route: string) (status: int) (context: HttpContext) (elapsedMs: double) =
        completedMessage.Invoke(logger, route, status, ApiTrace.traceId context, correlationId context, elapsedMs, null)

    let failed (logger: ILogger) (route: string) (status: int) (context: HttpContext) (error: exn) =
        failedMessage.Invoke(logger, route, status, ApiTrace.traceId context, correlationId context, error)

type ApiRouteHandler = HttpContext -> Task<int>

type IPlayerEventsDelay =
    abstract member WaitForNextSnapshotAsync: CancellationToken -> Task

type PlayerEventsDelay() =
    interface IPlayerEventsDelay with
        member _.WaitForNextSnapshotAsync(cancellationToken) =
            Task.Delay(TimeSpan.FromSeconds(5.0), cancellationToken)

type private PlayerEventsEnumerator(dataSource: NpgsqlDataSource, clock: IClock, delay: IPlayerEventsDelay, cancellationToken: CancellationToken) =
    let mutable eventIndex = 0
    let mutable currentSnapshot: PlayerStateDto option = None
    let mutable current = Unchecked.defaultof<SseItem<JsonElement>>

    let loadSnapshot () =
        task {
            let! snapshotResult = PlayerStateReadModel.loadSnapshot dataSource clock cancellationToken

            match snapshotResult with
            | Error _ -> return false
            | Ok snapshot ->
                currentSnapshot <- Some snapshot
                return true
        }

    let moveNextCore () =
        task {
            try
                if cancellationToken.IsCancellationRequested then
                    return false
                elif eventIndex = 0 then
                    let! loaded = loadSnapshot ()

                    if loaded then
                        let snapshot = currentSnapshot.Value
                        current <- SseItem<JsonElement>(ApiJson.toElement snapshot, "player.state")
                        eventIndex <- 1
                        return true
                    else
                        return false
                elif eventIndex = 1 then
                    do! delay.WaitForNextSnapshotAsync(cancellationToken)
                    let! loaded = loadSnapshot ()

                    if loaded then
                        let snapshot = currentSnapshot.Value
                        current <- SseItem<JsonElement>(ApiJson.toElement snapshot.Queue, "player.queue")
                        eventIndex <- 2
                        return true
                    else
                        return false
                else
                    let snapshot = currentSnapshot.Value

                    match eventIndex with
                    | 2 ->
                        current <- SseItem<JsonElement>(ApiJson.toElement snapshot.SuperChat, "player.say")
                        eventIndex <- 3
                    | 3 ->
                        current <- SseItem<JsonElement>(ApiJson.toElement snapshot.DonationGoal, "player.donation")
                        eventIndex <- 4
                    | _ ->
                        current <- SseItem<JsonElement>(ApiJson.toElement snapshot.Stream, "player.health")
                        eventIndex <- 1

                    return true
            with
            | :? OperationCanceledException -> return false
        }

    interface IAsyncEnumerator<SseItem<JsonElement>> with
        member _.Current = current

        member _.MoveNextAsync() : ValueTask<bool> =
            ValueTask<bool>(moveNextCore ())

        member _.DisposeAsync() : ValueTask =
            ValueTask()

type private PlayerEvents(dataSource: NpgsqlDataSource, clock: IClock, delay: IPlayerEventsDelay, requestAborted: CancellationToken) =
    interface IAsyncEnumerable<SseItem<JsonElement>> with
        member _.GetAsyncEnumerator(enumeratorCancellationToken: CancellationToken) =
            let cancellationToken =
                if enumeratorCancellationToken.CanBeCanceled then
                    enumeratorCancellationToken
                else
                    requestAborted

            PlayerEventsEnumerator(dataSource, clock, delay, cancellationToken) :> IAsyncEnumerator<SseItem<JsonElement>>

[<RequireQualifiedAccess>]
module ApiEndpoints =
    let addApiServices (adminOptions: AdminOptions) (streamOptions: StreamOptions) (services: IServiceCollection) : IServiceCollection =
        services.AddSingleton<AdminOptions>(adminOptions) |> ignore
        services.AddSingleton<StreamOptions>(streamOptions) |> ignore
        services.AddSingleton<IPlayerEventsDelay, PlayerEventsDelay>() |> ignore
        services.AddHttpContextAccessor() |> ignore

        services
            .AddAuthentication(AdminAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, AdminBearerAuthenticationHandler>(AdminAuthentication.SchemeName, ignore)
            .AddScheme<AuthenticationSchemeOptions, StreamNodeBearerAuthenticationHandler>(StreamNodeAuthentication.SchemeName, ignore)
        |> ignore

        services.AddAuthorization(fun authorizationOptions ->
            authorizationOptions.AddPolicy(
                AdminAuthentication.PolicyName,
                fun policy ->
                    policy.AddAuthenticationSchemes(AdminAuthentication.SchemeName) |> ignore
                    policy.RequireAuthenticatedUser() |> ignore
            )

            authorizationOptions.AddPolicy(
                StreamNodeAuthentication.PolicyName,
                fun policy ->
                    policy.AddAuthenticationSchemes(StreamNodeAuthentication.SchemeName) |> ignore
                    policy.RequireAuthenticatedUser() |> ignore
            ))
        |> ignore

        services

    let execute (logger: ILogger) (route: string) (handler: ApiRouteHandler) (context: HttpContext) : Task =
        task {
            let stopwatch = Stopwatch.StartNew()
            let mutable statusCode = StatusCodes.Status500InternalServerError

            try
                let! handledStatusCode = handler context
                statusCode <- handledStatusCode
            with
            | :? OperationCanceledException when context.RequestAborted.IsCancellationRequested ->
                statusCode <- if context.Response.HasStarted then context.Response.StatusCode else 499

                if not context.Response.HasStarted then
                    context.Response.StatusCode <- statusCode
            | error ->
                statusCode <- if context.Response.HasStarted then context.Response.StatusCode else StatusCodes.Status500InternalServerError
                ApiRouteLog.failed logger route statusCode context error

                if not context.Response.HasStarted then
                    do!
                        ApiProblems.write
                            context
                            StatusCodes.Status500InternalServerError
                            "api.unhandled"
                            "API route failed"
                            "API route failed."

            stopwatch.Stop()
            ApiRouteLog.completed logger route statusCode context stopwatch.Elapsed.TotalMilliseconds
        }
        :> Task

    let private map (routes: IEndpointRouteBuilder) (logger: ILogger) (method: string) (route: string) (logRoute: string) (handler: ApiRouteHandler) =
        routes.MapMethods(route, [| method |], RequestDelegate(fun context -> execute logger logRoute handler context)) |> ignore

    let private repositoryReadFailed (context: HttpContext) =
        ApiProblems.write
            context
            StatusCodes.Status500InternalServerError
            "state.read_failed"
            "State read failed"
            "State could not be read."

    let private writeOk context value =
        ApiJson.write context StatusCodes.Status200OK ApiJson.JsonContentType value

    let private streamUnavailable context =
        ApiProblems.write
            context
            StatusCodes.Status503ServiceUnavailable
            "stream.unavailable"
            "Stream unavailable"
            "Stream is offline"

    let private adminContractUnpinned context =
        ApiProblems.write
            context
            StatusCodes.Status501NotImplemented
            "admin.contract_unpinned"
            "Admin contract not pinned"
            "This admin route is listed in SPEC but its request/response body is not pinned yet."

    let private playerState (context: HttpContext) =
        task {
            let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let clock = context.RequestServices.GetRequiredService<IClock>()
            let! result = PlayerStateReadModel.loadSnapshot dataSource clock context.RequestAborted

            match result with
            | Ok state ->
                do! writeOk context state
                return StatusCodes.Status200OK
            | Error _ ->
                do! repositoryReadFailed context
                return StatusCodes.Status500InternalServerError
        }

    let private playerEvents (context: HttpContext) =
        task {
            let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let clock = context.RequestServices.GetRequiredService<IClock>()
            let delay = context.RequestServices.GetRequiredService<IPlayerEventsDelay>()
            let events = PlayerEvents(dataSource, clock, delay, context.RequestAborted) :> IAsyncEnumerable<SseItem<JsonElement>>
            let result = TypedResults.ServerSentEvents<JsonElement>(events) :> IResult
            do! result.ExecuteAsync(context)
            return StatusCodes.Status200OK
        }

    let private playerStream (context: HttpContext) =
        task {
            let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let clock = context.RequestServices.GetRequiredService<IClock>()
            let! healthResult = PlayerStateReadModel.loadStreamHealth dataSource clock context.RequestAborted

            match healthResult with
            | Error _ ->
                do! repositoryReadFailed context
                return StatusCodes.Status500InternalServerError
            | Ok health when health.Status <> "live" && health.Status <> "degraded" ->
                do! streamUnavailable context
                return StatusCodes.Status503ServiceUnavailable
            | Ok _ ->
                let! fileResult = PlayerStateReadModel.loadStreamFile dataSource clock context.RequestAborted

                match fileResult with
                | Error _ ->
                    do! repositoryReadFailed context
                    return StatusCodes.Status500InternalServerError
                | Ok None ->
                    do! streamUnavailable context
                    return StatusCodes.Status503ServiceUnavailable
                | Ok (Some file) ->
                    let openedStream =
                        try
                            Ok(File.OpenRead(file.CachePath))
                        with
                        | :? FileNotFoundException
                        | :? DirectoryNotFoundException
                        | :? UnauthorizedAccessException
                        | :? IOException -> Error()

                    match openedStream with
                    | Error () ->
                        do! streamUnavailable context
                        return StatusCodes.Status503ServiceUnavailable
                    | Ok stream ->
                        use stream = stream
                        let result = Results.Stream(stream, contentType = file.ContentType, enableRangeProcessing = true)
                        do! result.ExecuteAsync(context)
                        return context.Response.StatusCode
        }

    let private playerSong (context: HttpContext) =
        task {
            let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let clock = context.RequestServices.GetRequiredService<IClock>()
            let! result = PlayerStateReadModel.loadCurrentSong dataSource clock context.RequestAborted

            match result with
            | Ok song ->
                do! writeOk context song
                return StatusCodes.Status200OK
            | Error _ ->
                do! repositoryReadFailed context
                return StatusCodes.Status500InternalServerError
        }

    let private playerHealth (context: HttpContext) =
        task {
            let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let clock = context.RequestServices.GetRequiredService<IClock>()
            let! result = PlayerStateReadModel.loadStreamHealth dataSource clock context.RequestAborted

            match result with
            | Ok health ->
                do! writeOk context { health with TraceId = ApiTrace.traceId context }
                return StatusCodes.Status200OK
            | Error _ ->
                do! repositoryReadFailed context
                return StatusCodes.Status500InternalServerError
        }

    [<Literal>]
    let TelegramWebhookMaxBodyBytes = 1_048_576

    type private TelegramMappedEvent =
        { EventType: DomainEventType
          PayloadJson: string }

    type private TelegramMappedUpdate =
        | Ignored
        | DurableEvent of TelegramMappedEvent
        | PreCheckout of TelegramPreCheckoutInput

    type private TelegramUserContext =
        { TelegramUserId: int64
          DisplayName: string option
          LanguageCode: string option }

    type private CallbackMessageContext =
        { ChatId: int64
          MessageId: int64 }

    let private webhookSecretHeader (context: HttpContext) =
        let mutable values = StringValues()

        if context.Request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", &values) && values.Count = 1 then
            Some values[0]
        else
            None

    let private tryCommandArgument (expectedCommand: string) (text: string) =
        if String.IsNullOrWhiteSpace text then
            None
        else
            let separatorIndex = text.IndexOfAny([| ' '; '\t'; '\r'; '\n' |])

            let commandToken, argument =
                if separatorIndex < 0 then
                    text, String.Empty
                else
                    text.Substring(0, separatorIndex), text.Substring(separatorIndex + 1).Trim()

            let commandWithoutBot =
                let botSeparatorIndex = commandToken.IndexOf('@')

                if botSeparatorIndex > 0
                   && botSeparatorIndex < commandToken.Length - 1
                   && commandToken.IndexOf('@', botSeparatorIndex + 1) < 0 then
                    commandToken.Substring(0, botSeparatorIndex)
                else
                    commandToken

            if String.Equals(commandWithoutBot, expectedCommand, StringComparison.OrdinalIgnoreCase) then Some argument else None

    let private telegramUserContext (user: User) =
        let displayName =
            match user.Username with
            | Some username when not (String.IsNullOrWhiteSpace username) -> Some username
            | _ ->
                [ Some user.FirstName; user.LastName ]
                |> List.choose id
                |> String.concat " "
                |> fun value -> if String.IsNullOrWhiteSpace value then None else Some value

        { TelegramUserId = user.Id
          DisplayName = displayName
          LanguageCode = user.LanguageCode }

    let private telegramActor (message: Message) =
        message.From |> Option.map telegramUserContext

    let private callbackMessageContext (callbackQuery: CallbackQuery) =
        callbackQuery.Message
        |> Option.map (function
            | MaybeInaccessibleMessage.Message message ->
                { ChatId = message.Chat.Id
                  MessageId = message.MessageId }
            | MaybeInaccessibleMessage.InaccessibleMessage message ->
                { ChatId = message.Chat.Id
                  MessageId = message.MessageId })

    let private isPositiveInt32 (value: int64) =
        value > 0L && value <= int64 Int32.MaxValue

    let private mapTelegramUpdate (update: Update) : Result<TelegramMappedUpdate, string> =
        match update.PreCheckoutQuery with
        | Some preCheckoutQuery when String.IsNullOrWhiteSpace preCheckoutQuery.Id ->
            Error "Telegram pre-checkout query id is required."
        | Some preCheckoutQuery when String.IsNullOrWhiteSpace preCheckoutQuery.InvoicePayload ->
            Error "Telegram pre-checkout invoice payload is required."
        | Some preCheckoutQuery when not (isPositiveInt32 preCheckoutQuery.TotalAmount) ->
            Error "Telegram pre-checkout amount must be a positive supported integer."
        | Some preCheckoutQuery ->
            let user = telegramUserContext preCheckoutQuery.From

            Ok(
                PreCheckout
                    { TelegramUpdateId = update.UpdateId
                      QueryId = preCheckoutQuery.Id
                      TelegramUserId = user.TelegramUserId
                      LanguageCode = user.LanguageCode
                      Currency = preCheckoutQuery.Currency
                      TotalAmount = int preCheckoutQuery.TotalAmount
                      InvoicePayload = preCheckoutQuery.InvoicePayload }
            )
        | None ->
            match update.Message |> Option.bind (fun message -> message.SuccessfulPayment |> Option.map (fun payment -> message, payment)) with
            | Some(message, payment) when String.IsNullOrWhiteSpace payment.InvoicePayload ->
                Error "Telegram payment invoice payload is required."
            | Some(message, payment) when String.IsNullOrWhiteSpace payment.TelegramPaymentChargeId ->
                Error "Telegram payment charge id is required."
            | Some(message, payment) when not (isPositiveInt32 payment.TotalAmount) ->
                Error "Telegram payment amount must be a positive supported integer."
            | Some(message, payment) ->
                let actor = telegramActor message

                let payload =
                    JsonSerializer.Serialize(
                        {| paymentId = payment.InvoicePayload
                           telegramPaymentChargeId = payment.TelegramPaymentChargeId
                           amountStars = int payment.TotalAmount
                           currency = payment.Currency
                           telegramUpdateId = update.UpdateId
                           telegramMessageId = message.MessageId
                           chatId = message.Chat.Id
                           telegramUserId = actor |> Option.map (fun value -> value.TelegramUserId)
                           displayName = actor |> Option.bind (fun value -> value.DisplayName)
                           languageCode = actor |> Option.bind (fun value -> value.LanguageCode) |},
                        ApiJson.options
                    )

                Ok(DurableEvent { EventType = DomainEventType.DonationPaid; PayloadJson = payload })
            | None ->
                match update.CallbackQuery with
                | Some callbackQuery when String.IsNullOrWhiteSpace callbackQuery.Id ->
                    Error "Telegram callback query id is required."
                | Some callbackQuery ->
                    let actor = telegramUserContext callbackQuery.From
                    let message = callbackMessageContext callbackQuery
                    let chatId = message |> Option.map (fun value -> value.ChatId)
                    let isPrivateChat = chatId = Some actor.TelegramUserId

                    let payload =
                        JsonSerializer.Serialize(
                            {| telegramUpdateId = update.UpdateId
                               telegramMessageId = message |> Option.map (fun value -> value.MessageId)
                               chatId = chatId
                               telegramUserId = actor.TelegramUserId
                               displayName = actor.DisplayName
                               languageCode = actor.LanguageCode
                               callbackQueryId = callbackQuery.Id
                               rawCallbackData = callbackQuery.Data
                               isPrivateChat = isPrivateChat |},
                            ApiJson.options
                        )

                    Ok(DurableEvent { EventType = DomainEventType.TelegramCallbackReceived; PayloadJson = payload })
                | None ->
                    match update.Message with
                    | Some message ->
                        match message.Text, telegramActor message with
                        | Some text, Some actor ->
                            let chatId = message.Chat.Id
                            let isPrivateChat = chatId = actor.TelegramUserId

                            let commandEvent eventType command argument =
                                let payload =
                                    JsonSerializer.Serialize(
                                        {| telegramUpdateId = update.UpdateId
                                           telegramMessageId = message.MessageId
                                           chatId = chatId
                                           telegramUserId = actor.TelegramUserId
                                           displayName = actor.DisplayName
                                           languageCode = actor.LanguageCode
                                           command = command
                                           argument = argument
                                           isPrivateChat = isPrivateChat |},
                                        ApiJson.options
                                    )

                                DurableEvent { EventType = eventType; PayloadJson = payload }

                            match
                                tryCommandArgument "/request" text,
                                tryCommandArgument "/say" text,
                                tryCommandArgument "/start" text,
                                tryCommandArgument "/help" text,
                                tryCommandArgument "/song" text,
                                tryCommandArgument "/terms" text,
                                tryCommandArgument "/paysupport" text
                            with
                            | Some query, _, _, _, _, _, _ ->
                                let payload =
                                    JsonSerializer.Serialize(
                                        {| telegramUpdateId = update.UpdateId
                                           telegramMessageId = message.MessageId
                                           chatId = chatId
                                           telegramUserId = actor.TelegramUserId
                                           displayName = actor.DisplayName
                                           languageCode = actor.LanguageCode
                                           command = "/request"
                                           argument = query
                                           query = query
                                           isPrivateChat = isPrivateChat |},
                                        ApiJson.options
                                    )

                                Ok(DurableEvent { EventType = DomainEventType.TrackRequested; PayloadJson = payload })
                            | _, Some submittedText, _, _, _, _, _ ->
                                let payload =
                                    JsonSerializer.Serialize(
                                        {| telegramUpdateId = update.UpdateId
                                           telegramMessageId = message.MessageId
                                           chatId = chatId
                                           telegramUserId = actor.TelegramUserId
                                           displayName = actor.DisplayName
                                           languageCode = actor.LanguageCode
                                           command = "/say"
                                           argument = submittedText
                                           text = submittedText
                                           isPrivateChat = isPrivateChat |},
                                        ApiJson.options
                                    )

                                Ok(DurableEvent { EventType = DomainEventType.SayMessageSubmitted; PayloadJson = payload })
                            | _, _, Some argument, _, _, _, _ ->
                                commandEvent DomainEventType.TelegramCommandReceived "/start" argument |> Ok
                            | _, _, _, Some argument, _, _, _ ->
                                commandEvent DomainEventType.TelegramCommandReceived "/help" argument |> Ok
                            | _, _, _, _, Some argument, _, _ ->
                                commandEvent DomainEventType.TelegramCommandReceived "/song" argument |> Ok
                            | _, _, _, _, _, Some argument, _ ->
                                commandEvent DomainEventType.TelegramCommandReceived "/terms" argument |> Ok
                            | _, _, _, _, _, _, Some argument ->
                                commandEvent DomainEventType.TelegramCommandReceived "/paysupport" argument |> Ok
                            | _ -> Ok Ignored
                        | _ -> Ok Ignored
                    | None -> Ok Ignored

    let private readBoundedBody (maximumBytes: int) (context: HttpContext) (buffer: byte array) =
        task {
            let mutable total = 0
            let mutable complete = false

            while not complete && total <= maximumBytes do
                let remaining = maximumBytes + 1 - total
                let! bytesRead = context.Request.Body.ReadAsync(buffer.AsMemory(total, remaining), context.RequestAborted)

                if bytesRead = 0 then
                    complete <- true
                else
                    total <- total + bytesRead

            return if total > maximumBytes then None else Some total
        }

    let private telegramWebhook (context: HttpContext) =
        task {
            let telegramOptions = context.RequestServices.GetRequiredService<TelegramOptions>()
            let state = context.RequestServices.GetRequiredService<ITelegramAdapterState>()

            match webhookSecretHeader context with
            | None ->
                state.RecordError("telegram.webhook.secret_invalid")
                do!
                    ApiProblems.write
                        context
                        StatusCodes.Status401Unauthorized
                        "telegram.webhook.secret_invalid"
                        "Telegram webhook unauthorized"
                        "Exactly one Telegram webhook secret token header is required."

                return StatusCodes.Status401Unauthorized
            | Some suppliedSecret when not (AdminAuthentication.fixedTimeEqualsUtf8 telegramOptions.WebhookSecret suppliedSecret) ->
                state.RecordError("telegram.webhook.secret_invalid")
                do!
                    ApiProblems.write
                        context
                        StatusCodes.Status401Unauthorized
                        "telegram.webhook.secret_invalid"
                        "Telegram webhook unauthorized"
                        "Telegram webhook secret token is invalid."

                return StatusCodes.Status401Unauthorized
            | Some _ ->
                if context.Request.ContentLength.HasValue
                   && context.Request.ContentLength.Value > int64 TelegramWebhookMaxBodyBytes then
                    state.RecordError("request.too_large")

                    do!
                        ApiProblems.write
                            context
                            StatusCodes.Status413PayloadTooLarge
                            "request.too_large"
                            "Request body too large"
                            "Telegram webhook body exceeds the maximum allowed size."

                    return StatusCodes.Status413PayloadTooLarge
                else
                    let buffer = ArrayPool<byte>.Shared.Rent(TelegramWebhookMaxBodyBytes + 1)
                    use _bufferLease =
                        { new IDisposable with
                            member _.Dispose() = ArrayPool<byte>.Shared.Return(buffer, clearArray = true) }


                    try
                        let! bodyLength = readBoundedBody TelegramWebhookMaxBodyBytes context buffer

                        match bodyLength with
                        | None ->
                            state.RecordError("request.too_large")

                            do!
                                ApiProblems.write
                                    context
                                    StatusCodes.Status413PayloadTooLarge
                                    "request.too_large"
                                    "Request body too large"
                                    "Telegram webhook body exceeds the maximum allowed size."

                            return StatusCodes.Status413PayloadTooLarge
                        | Some length ->
                            match TelegramUpdateJson.tryParse buffer length with
                            | Error message ->
                                state.RecordError("request.invalid")
                                do! ApiProblems.write context StatusCodes.Status400BadRequest "request.invalid" "Invalid request" message
                                return StatusCodes.Status400BadRequest
                            | Ok update ->
                                match mapTelegramUpdate update with
                                | Error message ->
                                    state.RecordError("request.invalid")
                                    do! ApiProblems.write context StatusCodes.Status400BadRequest "request.invalid" "Invalid request" message
                                    return StatusCodes.Status400BadRequest
                                | Ok mappedUpdate ->
                                    match mappedUpdate with
                                    | Ignored ->
                                        state.RecordUpdate(update.UpdateId)
                                        context.Response.StatusCode <- StatusCodes.Status204NoContent
                                        return StatusCodes.Status204NoContent
                                    | PreCheckout preCheckout ->
                                        let workflow = context.RequestServices.GetRequiredService<ITelegramPreCheckoutWorkflow>()
                                        let! result = workflow.HandleAsync preCheckout context.RequestAborted

                                        match result with
                                        | Ok () ->
                                            state.RecordUpdate(update.UpdateId)
                                            context.Response.StatusCode <- StatusCodes.Status204NoContent
                                            return StatusCodes.Status204NoContent
                                        | Error error ->
                                            state.RecordError("telegram.pre_checkout_unavailable")

                                            do!
                                                ApiProblems.write
                                                    context
                                                    StatusCodes.Status503ServiceUnavailable
                                                    "telegram.pre_checkout_unavailable"
                                                    "Telegram pre-checkout unavailable"
                                                    (BackgroundWorkerError.toMessage error)

                                            return StatusCodes.Status503ServiceUnavailable
                                    | DurableEvent event ->
                                        let ingestor = context.RequestServices.GetRequiredService<ITelegramUpdateEventIngestor>()

                                        let! ingestResult =
                                            ingestor.TryIngestAsync
                                                update.UpdateId
                                                event.EventType
                                                "Web10.Radio.Telegram"
                                                event.PayloadJson
                                                context.RequestAborted

                                        match ingestResult with
                                        | Ok _ ->
                                            state.RecordUpdate(update.UpdateId)
                                            context.Response.StatusCode <- StatusCodes.Status204NoContent
                                            return StatusCodes.Status204NoContent
                                        | Error error ->
                                            state.RecordError("telegram.webhook.ingest_failed")

                                            do!
                                                ApiProblems.write
                                                    context
                                                    StatusCodes.Status500InternalServerError
                                                    "telegram.webhook.ingest_failed"
                                                    "Telegram webhook failed"
                                                    (BackgroundWorkerError.toMessage error)

                                            return StatusCodes.Status500InternalServerError
                    with error ->
                        state.RecordError("telegram.webhook.unhandled")
                        return raise error
        }

    let private telegramHealth (context: HttpContext) =
        task {
            let state = context.RequestServices.GetRequiredService<ITelegramAdapterState>()
            let snapshot = state.Snapshot()
            let lastUpdateId =
                match snapshot.LastUpdateId with
                | Some value -> Nullable<int64>(value)
                | None -> Nullable<int64>()

            let dto: TelegramHealthDto =
                { IsConfigured = snapshot.IsConfigured
                  ChannelIdOrUsername = snapshot.ChannelIdOrUsername
                  LastUpdateId = lastUpdateId
                  LastError = snapshot.LastError |> Option.defaultValue null }

            do! writeOk context dto
            return StatusCodes.Status200OK
        }

    [<Literal>]
    let PlaybackCallbackMaxBodyBytes = 4096

    let private parsePlaybackCallback requireOutcome (buffer: byte array) length =
        try
            use document = JsonDocument.Parse(ReadOnlyMemory<byte>(buffer, 0, length))
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error "Playback callback body must be a JSON object."
            else
                let mutable ownerElement = Unchecked.defaultof<JsonElement>
                let mutable attemptElement = Unchecked.defaultof<JsonElement>
                let mutable statusElement = Unchecked.defaultof<JsonElement>
                let mutable reasonElement = Unchecked.defaultof<JsonElement>
                let mutable claimOwner = Guid.Empty
                let mutable claimAttempt = 0

                if not (root.TryGetProperty("claimOwner", &ownerElement))
                   || ownerElement.ValueKind <> JsonValueKind.String
                   || not (Guid.TryParse(ownerElement.GetString(), &claimOwner))
                   || claimOwner = Guid.Empty then
                    Error "claimOwner must be a non-empty UUID."
                elif not (root.TryGetProperty("claimAttempt", &attemptElement))
                     || attemptElement.ValueKind <> JsonValueKind.Number
                     || not (attemptElement.TryGetInt32(&claimAttempt))
                     || claimAttempt <= 0 then
                    Error "claimAttempt must be a positive integer."
                elif not requireOutcome then
                    Ok(claimOwner, claimAttempt, None)
                elif not (root.TryGetProperty("status", &statusElement))
                     || statusElement.ValueKind <> JsonValueKind.String then
                    Error "status must be played or failed."
                else
                    match statusElement.GetString() with
                    | "played" ->
                        Ok(claimOwner, claimAttempt, Some Web10.Radio.API.PlaybackCompletion.Succeeded)
                    | "failed" ->
                        if root.TryGetProperty("failureReason", &reasonElement)
                           && reasonElement.ValueKind = JsonValueKind.String
                           && not (String.IsNullOrWhiteSpace(reasonElement.GetString())) then
                            Ok(
                                claimOwner,
                                claimAttempt,
                                Some(Web10.Radio.API.PlaybackCompletion.Failed(reasonElement.GetString().Trim()))
                            )
                        else
                            Error "failureReason is required when status is failed."
                    | _ -> Error "status must be played or failed."
        with :? JsonException ->
            Error "Playback callback body must be valid JSON."

    let private parseQueueItemId (context: HttpContext) =
        let mutable queueItemId = Guid.Empty
        let value = context.Request.RouteValues["queueItemId"]

        if isNull value || not (Guid.TryParse(string value, &queueItemId)) || queueItemId = Guid.Empty then
            Error "queueItemId must be a non-empty UUID."
        else
            Ok queueItemId

    let private playbackCallback requireOutcome (context: HttpContext) =
        task {
            if context.Request.ContentLength.HasValue
               && context.Request.ContentLength.Value > int64 PlaybackCallbackMaxBodyBytes then
                do!
                    ApiProblems.write
                        context
                        StatusCodes.Status413PayloadTooLarge
                        "request.too_large"
                        "Request body too large"
                        "Playback callback body exceeds the maximum allowed size."

                return StatusCodes.Status413PayloadTooLarge
            else
                let buffer = ArrayPool<byte>.Shared.Rent(PlaybackCallbackMaxBodyBytes + 1)
                use _bufferLease =
                    { new IDisposable with
                        member _.Dispose() = ArrayPool<byte>.Shared.Return(buffer, clearArray = true) }

                let! bodyLength = readBoundedBody PlaybackCallbackMaxBodyBytes context buffer

                match parseQueueItemId context, bodyLength with
                | Error message, _ ->
                    do!
                        ApiProblems.write
                            context
                            StatusCodes.Status400BadRequest
                            "request.invalid"
                            "Invalid request"
                            message

                    return StatusCodes.Status400BadRequest
                | _, None ->
                    do!
                        ApiProblems.write
                            context
                            StatusCodes.Status413PayloadTooLarge
                            "request.too_large"
                            "Request body too large"
                            "Playback callback body exceeds the maximum allowed size."

                    return StatusCodes.Status413PayloadTooLarge
                | Ok queueItemId, Some length ->
                    match parsePlaybackCallback requireOutcome buffer length with
                    | Error message ->
                        do! ApiProblems.write context StatusCodes.Status400BadRequest "request.invalid" "Invalid request" message
                        return StatusCodes.Status400BadRequest
                    | Ok(claimOwner, claimAttempt, outcome) ->
                        let reporter = context.RequestServices.GetRequiredService<IPlaybackCompletionReporter>()

                        let! result =
                            match outcome with
                            | None -> reporter.RenewLeaseAsync queueItemId claimOwner claimAttempt context.RequestAborted
                            | Some completion ->
                                reporter.ReportAsync queueItemId claimOwner claimAttempt completion context.RequestAborted

                        match result with
                        | Ok true ->
                            context.Response.StatusCode <- StatusCodes.Status204NoContent
                            return StatusCodes.Status204NoContent
                        | Ok false ->
                            do!
                                ApiProblems.write
                                    context
                                    StatusCodes.Status409Conflict
                                    "playback.claim_stale"
                                    "Playback claim is stale"
                                    "The playback claim owner or attempt is no longer active."

                            return StatusCodes.Status409Conflict
                        | Error error ->
                            do!
                                ApiProblems.write
                                    context
                                    StatusCodes.Status500InternalServerError
                                    "playback.callback_failed"
                                    "Playback callback failed"
                                    (BackgroundWorkerError.toMessage error)

                            return StatusCodes.Status500InternalServerError
        }

    let private playbackLease context = playbackCallback false context
    let private playbackCompletion context = playbackCallback true context

    let private adminSocialLinks (context: HttpContext) =
        task {
            let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let! result = PlayerStateReadModel.loadSocialLinks dataSource context.RequestAborted

            match result with
            | Ok socials ->
                do! writeOk context socials
                return StatusCodes.Status200OK
            | Error _ ->
                do! repositoryReadFailed context
                return StatusCodes.Status500InternalServerError
        }

    let private adminDonationGoal (context: HttpContext) =
        task {
            let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let! result = PlayerStateReadModel.loadDonationGoal dataSource context.RequestAborted

            match result with
            | Ok donationGoal ->
                do! writeOk context donationGoal
                return StatusCodes.Status200OK
            | Error _ ->
                do! repositoryReadFailed context
                return StatusCodes.Status500InternalServerError
        }

    [<Literal>]
    let private SayModerationMaxBodyBytes = 2 * 1024

    let private sayStatusText = function
        | SayMessageModerationFilter.Pending -> "pending"
        | SayMessageModerationFilter.Approved -> "approved"
        | SayMessageModerationFilter.Rejected -> "rejected"

    let private sayTargetText = function
        | SayMessageModerationTarget.Approved -> "Approved"
        | SayMessageModerationTarget.Rejected -> "Rejected"

    let private trySayModerationFilter (context: HttpContext) =
        let mutable values = StringValues()

        if not (context.Request.Query.TryGetValue("status", &values)) || values.Count <> 1 then
            None
        else
            match values[0] with
            | "pending" -> Some SayMessageModerationFilter.Pending
            | "approved" -> Some SayMessageModerationFilter.Approved
            | "rejected" -> Some SayMessageModerationFilter.Rejected
            | _ -> None

    let private toAdminSayMessageDto (message: SayMessageForModeration) : AdminSayMessageDto =
        { Id = message.Id.ToString("D")
          TelegramUserId =
              match message.TelegramUserId with
              | Some telegramUserId -> Nullable<int64>(telegramUserId)
              | None -> Nullable<int64>()
          DisplayName = message.DisplayName
          Text = message.Text
          AmountStars = message.AmountStars
          Color = message.Color |> Option.defaultValue null
          Status = sayStatusText message.Status
          SubmittedAtUtc = ApiTime.toIsoUtc message.SubmittedAtUtc
          PaidAtUtc = message.PaidAtUtc |> Option.map ApiTime.toIsoUtc |> Option.defaultValue null
          ModeratedAtUtc = message.ModeratedAtUtc |> Option.map ApiTime.toIsoUtc |> Option.defaultValue null
          ModerationReason = message.ModerationReason |> Option.defaultValue null }

    let private sayStatusInvalid context =
        ApiProblems.write
            context
            StatusCodes.Status400BadRequest
            "say.status.invalid"
            "Invalid say message status"
            "Query parameter status must be exactly one of pending, approved, or rejected."

    let private sayRequestInvalid context =
        ApiProblems.write
            context
            StatusCodes.Status400BadRequest
            "say.request.invalid"
            "Invalid say message request"
            "The say message identifier or request body is invalid."

    let private trySayMessageId (context: HttpContext) =
        let value = context.Request.RouteValues["messageId"]
        let mutable messageId = Guid.Empty

        if isNull value || not (Guid.TryParse(string value, &messageId)) || messageId = Guid.Empty then
            None
        else
            Some messageId

    let private tryReadSayModerationBody (context: HttpContext) =
        task {
            if context.Request.ContentLength.HasValue
               && context.Request.ContentLength.Value > int64 SayModerationMaxBodyBytes then
                return None
            else
                let buffer = ArrayPool<byte>.Shared.Rent(SayModerationMaxBodyBytes + 1)

                use _bufferLease =
                    { new IDisposable with
                        member _.Dispose() = ArrayPool<byte>.Shared.Return(buffer, clearArray = true) }

                let! length = readBoundedBody SayModerationMaxBodyBytes context buffer

                match length with
                | None -> return None
                | Some bodyLength ->
                    try
                        use document = JsonDocument.Parse(buffer.AsMemory(0, bodyLength))
                        return Some(document.RootElement.Clone())
                    with
                    | :? JsonException -> return None
        }

    let private isExactEmptyJsonObject (root: JsonElement) =
        root.ValueKind = JsonValueKind.Object
        && (root.EnumerateObject() |> Seq.isEmpty)

    let private tryRejectReason (root: JsonElement) =
        if root.ValueKind <> JsonValueKind.Object then
            None
        else
            let properties = root.EnumerateObject() |> Seq.toList

            match properties with
            | [ property ] when property.Name = "reason" && property.Value.ValueKind = JsonValueKind.String ->
                let reason = property.Value.GetString()

                if isNull reason then
                    None
                else
                    let trimmedReason = reason.Trim()

                    if trimmedReason.Length >= 1 && trimmedReason.Length <= 500 then
                        Some trimmedReason
                    else
                        None
            | _ -> None

    let private adminSayMessages (context: HttpContext) =
        task {
            match trySayModerationFilter context with
            | None ->
                do! sayStatusInvalid context
                return StatusCodes.Status400BadRequest
            | Some filter ->
                let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let! result = SayMessageRepository.listForModeration dataSource filter context.RequestAborted

                match result with
                | Ok messages ->
                    do! writeOk context (messages |> List.map toAdminSayMessageDto)
                    return StatusCodes.Status200OK
                | Error _ ->
                    do! repositoryReadFailed context
                    return StatusCodes.Status500InternalServerError
        }

    let private moderateSayMessage
        (context: HttpContext)
        (messageId: Guid)
        (target: SayMessageModerationTarget)
        (reason: string option) =
        task {
            let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let idGenerator = context.RequestServices.GetRequiredService<IIdGenerator>()
            let clock = context.RequestServices.GetRequiredService<IClock>()
            let moderatedAtUtc = clock.UtcNow

            let! result =
                DatabaseSession.withTransactionResult
                    dataSource
                    (fun connection transaction cancellationToken ->
                        task {
                            let! moderation =
                                SayMessageRepository.moderateInTransaction
                                    connection
                                    transaction
                                    messageId
                                    target
                                    reason
                                    moderatedAtUtc
                                    cancellationToken

                            match moderation with
                            | Error repositoryError -> return Error repositoryError
                            | Ok SayMessageModerationOutcome.Applied ->
                                let payload =
                                    JsonSerializer.Serialize(
                                        {| sayMessageId = messageId.ToString("D")
                                           status = sayTargetText target
                                           moderationReason =
                                               match target with
                                               | SayMessageModerationTarget.Approved -> null
                                               | SayMessageModerationTarget.Rejected -> reason |> Option.defaultValue null |},
                                        ApiJson.options
                                    )

                                match
                                    DomainEventEnvelope.create
                                        idGenerator
                                        clock
                                        DomainEventType.SayMessageModerated
                                        "Web10.Radio.API.Admin"
                                        None
                                        None
                                        payload
                                with
                                | Error domainError ->
                                    return Error(DatabaseError("ApiEndpoints.moderateSayMessage", DomainEventError.toMessage domainError))
                                | Ok envelope ->
                                    let! appended =
                                        OutboxEventRepository.appendInTransaction
                                            connection
                                            transaction
                                            (OutboxMapping.toOutboxEvent envelope)
                                            cancellationToken

                                    match appended with
                                    | Ok () -> return Ok SayMessageModerationOutcome.Applied
                                    | Error repositoryError -> return Error repositoryError
                            | Ok outcome -> return Ok outcome
                        })
                    context.RequestAborted

            match result with
            | Error _ ->
                do! repositoryReadFailed context
                return StatusCodes.Status500InternalServerError
            | Ok SayMessageModerationOutcome.Applied
            | Ok SayMessageModerationOutcome.AlreadyApplied ->
                context.Response.StatusCode <- StatusCodes.Status204NoContent
                return StatusCodes.Status204NoContent
            | Ok SayMessageModerationOutcome.NotFound ->
                do!
                    ApiProblems.write
                        context
                        StatusCodes.Status404NotFound
                        "say.not_found"
                        "Say message not found"
                        "The say message does not exist."

                return StatusCodes.Status404NotFound
            | Ok SayMessageModerationOutcome.Conflict ->
                do!
                    ApiProblems.write
                        context
                        StatusCodes.Status409Conflict
                        "say.state_conflict"
                        "Say message state conflict"
                        "The say message cannot be moderated in its current state."

                return StatusCodes.Status409Conflict
        }

    let private approveSayMessage (context: HttpContext) =
        task {
            match trySayMessageId context with
            | None ->
                do! sayRequestInvalid context
                return StatusCodes.Status400BadRequest
            | Some messageId ->
                let! body = tryReadSayModerationBody context

                match body with
                | Some root when isExactEmptyJsonObject root ->
                    return! moderateSayMessage context messageId SayMessageModerationTarget.Approved None
                | _ ->
                    do! sayRequestInvalid context
                    return StatusCodes.Status400BadRequest
        }

    let private rejectSayMessage (context: HttpContext) =
        task {
            match trySayMessageId context with
            | None ->
                do! sayRequestInvalid context
                return StatusCodes.Status400BadRequest
            | Some messageId ->
                let! body = tryReadSayModerationBody context

                match body |> Option.bind tryRejectReason with
                | Some reason ->
                    return! moderateSayMessage context messageId SayMessageModerationTarget.Rejected (Some reason)
                | None ->
                    do! sayRequestInvalid context
                    return StatusCodes.Status400BadRequest
        }

    let private adminPlaceholder (context: HttpContext) =
        task {
            do! adminContractUnpinned context
            return StatusCodes.Status501NotImplemented
        }

    let mapApiV0Endpoints (app: WebApplication) : unit =
        let logger = app.Logger
        map app logger "GET" "/api/v0/player/state" "/api/v0/player/state" playerState
        map app logger "GET" "/api/v0/player/events" "/api/v0/player/events" playerEvents
        map app logger "GET" "/api/v0/player/stream" "/api/v0/player/stream" playerStream
        map app logger "GET" "/api/v0/player/song" "/api/v0/player/song" playerSong
        map app logger "GET" "/api/v0/player/health" "/api/v0/player/health" playerHealth

        map app logger "POST" "/api/v0/telegram/webhook" "/api/v0/telegram/webhook" telegramWebhook
        map app logger "GET" "/api/v0/telegram/health" "/api/v0/telegram/health" telegramHealth

        let streamNode = app.MapGroup("/api/v0/stream-node")
        streamNode.RequireAuthorization(StreamNodeAuthentication.PolicyName) |> ignore
        map streamNode logger "POST" "/playback/{queueItemId}/lease" "/api/v0/stream-node/playback/{queueItemId}/lease" playbackLease
        map streamNode logger "POST" "/playback/{queueItemId}/completion" "/api/v0/stream-node/playback/{queueItemId}/completion" playbackCompletion

        let admin = app.MapGroup("/api/v0/admin")
        admin.RequireAuthorization(AdminAuthentication.PolicyName) |> ignore
        map admin logger "GET" "/social-links" "/api/v0/admin/social-links" adminSocialLinks
        map admin logger "PUT" "/social-links" "/api/v0/admin/social-links" adminPlaceholder
        map admin logger "GET" "/donation-goal" "/api/v0/admin/donation-goal" adminDonationGoal
        map admin logger "PUT" "/donation-goal" "/api/v0/admin/donation-goal" adminPlaceholder
        map admin logger "GET" "/playlists" "/api/v0/admin/playlists" adminPlaceholder
        map admin logger "POST" "/playlists" "/api/v0/admin/playlists" adminPlaceholder
        map admin logger "GET" "/playlists/{playlistId}/items" "/api/v0/admin/playlists/{playlistId}/items" adminPlaceholder
        map admin logger "POST" "/playlists/{playlistId}/items" "/api/v0/admin/playlists/{playlistId}/items" adminPlaceholder
        map admin logger "PUT" "/playlists/{playlistId}/items" "/api/v0/admin/playlists/{playlistId}/items" adminPlaceholder
        map admin logger "GET" "/say-messages" "/api/v0/admin/say-messages" adminSayMessages
        map admin logger "POST" "/say-messages/{messageId}/approve" "/api/v0/admin/say-messages/{messageId}/approve" approveSayMessage
        map admin logger "POST" "/say-messages/{messageId}/reject" "/api/v0/admin/say-messages/{messageId}/reject" rejectSayMessage
        map admin logger "GET" "/storage" "/api/v0/admin/storage" adminPlaceholder
        map admin logger "PUT" "/storage" "/api/v0/admin/storage" adminPlaceholder
        map admin logger "POST" "/library/scan" "/api/v0/admin/library/scan" adminPlaceholder
        map admin logger "GET" "/stream-node/status" "/api/v0/admin/stream-node/status" adminPlaceholder
        map admin logger "POST" "/stream-node/restart" "/api/v0/admin/stream-node/restart" adminPlaceholder
