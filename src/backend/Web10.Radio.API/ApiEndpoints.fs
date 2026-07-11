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
open Microsoft.Extensions.Hosting
open Npgsql
open Web10.Radio.Database
open Web10.Radio.Database.Repositories
open Web10.Radio.Telegram

[<RequireQualifiedAccess>]
module ApiSecurity =
    let fixedTimeEqualsUtf8 (expected: string) (actual: string) =
        let hash value =
            value
            |> fun candidate -> if isNull candidate then String.Empty else candidate
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData

        CryptographicOperations.FixedTimeEquals(hash expected, hash actual)
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
                       || not (ApiSecurity.fixedTimeEqualsUtf8 streamOptions.CallbackToken suppliedToken) then
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

/// Result of routing a single parsed Telegram update through the shared ingestion path.
/// Both the webhook endpoint and the long-polling worker consume this: the webhook maps
/// it to an HTTP status, the worker maps it to logging + offset-advance decisions.
type TelegramUpdateProcessingOutcome =
    | TelegramUpdateAccepted
    | TelegramUpdateRejected of message: string
    | TelegramPreCheckoutUnavailable of error: BackgroundWorkerError
    | TelegramIngestFailed of error: BackgroundWorkerError

[<RequireQualifiedAccess>]
module ApiEndpoints =
    let addApiServices
        (adminOptions: AdminOptions)
        (developmentFixturesEnabled: bool)
        (streamOptions: StreamOptions)
        (services: IServiceCollection)
        : IServiceCollection =
        services
        |> AdminIdentityComposition.addAdminIdentityServices adminOptions developmentFixturesEnabled
        |> ignore
        services.AddHttpContextAccessor() |> ignore

        services.AddSingleton<StreamOptions>(streamOptions) |> ignore
        services.AddSingleton<IPlayerEventsDelay, PlayerEventsDelay>() |> ignore

        services
            .AddAuthentication(AdminSessionAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, AdminSessionAuthenticationHandler>(AdminSessionAuthentication.SchemeName, ignore)
            .AddScheme<AuthenticationSchemeOptions, StreamNodeBearerAuthenticationHandler>(StreamNodeAuthentication.SchemeName, ignore)
        |> ignore

        services.AddAuthorization(fun authorizationOptions ->
            authorizationOptions.AddPolicy(
                AdminSessionAuthentication.PolicyName,
                fun policy ->
                    policy.AddAuthenticationSchemes(AdminSessionAuthentication.SchemeName) |> ignore
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

    type private BoundedJsonBody =
        | BodyTooLarge
        | BodyInvalid
        | BodyParsed of JsonElement

    let private readJsonBody maximumBytes (context: HttpContext) =
        task {
            if context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > int64 maximumBytes then
                return BodyTooLarge
            else
                let buffer = ArrayPool<byte>.Shared.Rent(maximumBytes + 1)

                use _bufferLease =
                    { new IDisposable with
                        member _.Dispose() = ArrayPool<byte>.Shared.Return(buffer, clearArray = true) }

                let! length = readBoundedBody maximumBytes context buffer

                match length with
                | None -> return BodyTooLarge
                | Some bodyLength ->
                    try
                        use document = JsonDocument.Parse(buffer.AsMemory(0, bodyLength))
                        return BodyParsed(document.RootElement.Clone())
                    with :? JsonException ->
                        return BodyInvalid
        }

    let private hasExactProperties (expected: Set<string>) (root: JsonElement) =
        root.ValueKind = JsonValueKind.Object
        && (root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) = expected

    let private tryProperty (name: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty(name, &value) then Some value else None

    let private tryString (name: string) (root: JsonElement) =
        match tryProperty name root with
        | Some value when value.ValueKind = JsonValueKind.String ->
            let text = value.GetString()
            if isNull text then None else Some text
        | _ -> None

    let private tryNullableString (name: string) (root: JsonElement) =
        match tryProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Null -> Some None
        | Some value when value.ValueKind = JsonValueKind.String ->
            let text = value.GetString()
            if isNull text then None else Some(Some text)
        | _ -> None

    let private tryPositiveGuid (value: string) =
        let mutable parsed = Guid.Empty
        if Guid.TryParse(value, &parsed) && parsed <> Guid.Empty then Some parsed else None

    let private writeRequestTooLarge context =
        ApiProblems.write context StatusCodes.Status413PayloadTooLarge "request.too_large" "Request body too large" "Request body exceeds the maximum allowed size."

    let private writeRepositoryFailure context =
        ApiProblems.write context StatusCodes.Status500InternalServerError "repository.write_failed" "Repository write failed" "The requested change could not be persisted."

    let private writeDomainProblem context status code title message =
        ApiProblems.write context status code title message

    let private parseGuidRoute routeValue (context: HttpContext) =
        match context.Request.RouteValues[routeValue] with
        | null -> None
        | value -> tryPositiveGuid (string value)

    let private toScanStatusDto (job: LibraryScanJobStatusRecord) : LibraryScanStatusDto =
        { ScanJobId = job.Id.ToString("D")
          Status = job.Status.ToLowerInvariant()
          DiscoveredCount = job.DiscoveredCount
          RequestedAtUtc = ApiTime.toIsoUtc job.RequestedAtUtc
          StartedAtUtc = job.StartedAtUtc |> Option.map ApiTime.toIsoUtc |> Option.defaultValue null
          FinishedAtUtc = job.FinishedAtUtc |> Option.map ApiTime.toIsoUtc |> Option.defaultValue null
          FailureReason = job.FailureReason |> Option.defaultValue null }

    let private toTrackDto (track: AdminTrack) : AdminTrackDto =
        { Id = track.Id.ToString("D")
          Title = track.Title
          Artist = track.Artist
          Album = track.Album
          DurationMs = max 0 track.DurationMs
          HasCachedFile = track.HasCachedFile }

    let private toPlaylistDto (playlist: PlaylistSummary) : PlaylistSummaryDto =
        { Id = playlist.Id.ToString("D")
          Name = playlist.Name
          Description = playlist.Description |> Option.defaultValue null
          IsActive = playlist.IsActive
          ItemCount = max 0 playlist.ItemCount }

    let private toPlaylistItemDto (item: PlaylistItem) : PlaylistItemDto =
        { Id = item.Id.ToString("D")
          TrackId = item.TrackId.ToString("D")
          Title = item.Title
          Artist = item.Artist
          Position = max 0 item.Position }

    let private toControlDto (state: StreamNodeControlState) : StreamNodeControlDto =
        { DesiredState = match state.DesiredState with | Running -> "running" | Stopped -> "stopped"
          RestartGeneration = max 0 state.RestartGeneration }

    /// Classify a parsed Telegram update and route it through the shared ingestion path
    /// (synchronous pre-checkout workflow, or durable-event inbox deduped by (updateId, eventType)).
    /// Reused verbatim by the webhook endpoint and the long-polling worker so payment/command
    /// handling never forks. Records adapter-state progress/errors as a side effect.
    let processTelegramUpdate (services: IServiceProvider) (update: Update) (cancellationToken: CancellationToken) : Task<TelegramUpdateProcessingOutcome> =
        task {
            use attempt = FlowTelemetry.start FlowTelemetry.TelegramUpdate
            FlowTelemetry.addTag "telegram.update_id" (box update.UpdateId) attempt

            let finish outcome =
                FlowTelemetry.finish outcome [] attempt |> ignore

            try
                let state = services.GetRequiredService<ITelegramAdapterState>()

                match mapTelegramUpdate update with
                | Error message ->
                    state.RecordError("request.invalid")
                    finish "rejected"
                    return TelegramUpdateRejected message
                | Ok Ignored ->
                    FlowTelemetry.addTag "event.type" (box "ignored") attempt
                    state.RecordUpdate(update.UpdateId)
                    finish "ignored"
                    return TelegramUpdateAccepted
                | Ok(PreCheckout preCheckout) ->
                    FlowTelemetry.addTag "event.type" (box "pre_checkout") attempt
                    let workflow = services.GetRequiredService<ITelegramPreCheckoutWorkflow>()
                    let! result = workflow.HandleAsync preCheckout cancellationToken

                    match result with
                    | Ok() ->
                        state.RecordUpdate(update.UpdateId)
                        finish "accepted"
                        return TelegramUpdateAccepted
                    | Error error ->
                        state.RecordError("telegram.pre_checkout_unavailable")
                        finish "error"
                        return TelegramPreCheckoutUnavailable error
                | Ok(DurableEvent event) ->
                    FlowTelemetry.addTag "event.type" (box (DomainEventType.toString event.EventType)) attempt
                    let ingestor = services.GetRequiredService<ITelegramUpdateEventIngestor>()

                    let! ingestResult =
                        ingestor.TryIngestAsync update.UpdateId event.EventType "Web10.Radio.Telegram" event.PayloadJson cancellationToken

                    match ingestResult with
                    | Ok true ->
                        state.RecordUpdate(update.UpdateId)
                        finish "accepted"
                        return TelegramUpdateAccepted
                    | Ok false ->
                        state.RecordUpdate(update.UpdateId)
                        finish "duplicate"
                        return TelegramUpdateAccepted
                    | Error error ->
                        state.RecordError("telegram.webhook.ingest_failed")
                        finish "error"
                        return TelegramIngestFailed error
            with error ->
                FlowTelemetry.finishError "error" [] error attempt |> ignore
                return raise error
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
            | Some suppliedSecret when not (ApiSecurity.fixedTimeEqualsUtf8 telegramOptions.WebhookSecret suppliedSecret) ->
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
                                let! outcome = processTelegramUpdate context.RequestServices update context.RequestAborted

                                match outcome with
                                | TelegramUpdateAccepted ->
                                    context.Response.StatusCode <- StatusCodes.Status204NoContent
                                    return StatusCodes.Status204NoContent
                                | TelegramUpdateRejected message ->
                                    do! ApiProblems.write context StatusCodes.Status400BadRequest "request.invalid" "Invalid request" message
                                    return StatusCodes.Status400BadRequest
                                | TelegramPreCheckoutUnavailable error ->
                                    do!
                                        ApiProblems.write
                                            context
                                            StatusCodes.Status503ServiceUnavailable
                                            "telegram.pre_checkout_unavailable"
                                            "Telegram pre-checkout unavailable"
                                            (BackgroundWorkerError.toMessage error)

                                    return StatusCodes.Status503ServiceUnavailable
                                | TelegramIngestFailed error ->
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
            let instrument, kindValue =
                if requireOutcome then
                    FlowTelemetry.StreamNodePlaybackCompletion, "completion"
                else
                    FlowTelemetry.StreamNodePlaybackLease, "lease"

            use attempt = FlowTelemetry.start instrument
            let metricTags = [ FlowTelemetry.kind kindValue ]
            FlowTelemetry.addTag "kind" (box kindValue) attempt

            let finish outcome =
                FlowTelemetry.finish outcome metricTags attempt |> ignore

            try
                if context.Request.ContentLength.HasValue
                   && context.Request.ContentLength.Value > int64 PlaybackCallbackMaxBodyBytes then
                    do!
                        ApiProblems.write
                            context
                            StatusCodes.Status413PayloadTooLarge
                            "request.too_large"
                            "Request body too large"
                            "Playback callback body exceeds the maximum allowed size."

                    finish "invalid"
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

                        finish "invalid"
                        return StatusCodes.Status400BadRequest
                    | _, None ->
                        do!
                            ApiProblems.write
                                context
                                StatusCodes.Status413PayloadTooLarge
                                "request.too_large"
                                "Request body too large"
                                "Playback callback body exceeds the maximum allowed size."

                        finish "invalid"
                        return StatusCodes.Status413PayloadTooLarge
                    | Ok queueItemId, Some length ->
                        match parsePlaybackCallback requireOutcome buffer length with
                        | Error message ->
                            do! ApiProblems.write context StatusCodes.Status400BadRequest "request.invalid" "Invalid request" message
                            finish "invalid"
                            return StatusCodes.Status400BadRequest
                        | Ok(claimOwner, claimAttempt, outcome) ->
                            FlowTelemetry.addTag "queue.item_id" (box (queueItemId.ToString("D"))) attempt
                            FlowTelemetry.addTag "claim.owner" (box (claimOwner.ToString("D"))) attempt
                            FlowTelemetry.addTag "claim.attempt" (box claimAttempt) attempt

                            let reporter = context.RequestServices.GetRequiredService<IPlaybackCompletionReporter>()

                            let! result =
                                match outcome with
                                | None -> reporter.RenewLeaseAsync queueItemId claimOwner claimAttempt context.RequestAborted
                                | Some completion ->
                                    reporter.ReportAsync queueItemId claimOwner claimAttempt completion context.RequestAborted

                            match result with
                            | Ok true ->
                                context.Response.StatusCode <- StatusCodes.Status204NoContent
                                finish "accepted"
                                return StatusCodes.Status204NoContent
                            | Ok false ->
                                do!
                                    ApiProblems.write
                                        context
                                        StatusCodes.Status409Conflict
                                        "playback.claim_stale"
                                        "Playback claim is stale"
                                        "The playback claim owner or attempt is no longer active."

                                finish "stale"
                                return StatusCodes.Status409Conflict
                            | Error error ->
                                do!
                                    ApiProblems.write
                                        context
                                        StatusCodes.Status500InternalServerError
                                        "playback.callback_failed"
                                        "Playback callback failed"
                                        (BackgroundWorkerError.toMessage error)

                                finish "error"
                                return StatusCodes.Status500InternalServerError
            with error ->
                FlowTelemetry.finishError "error" metricTags error attempt |> ignore
                return raise error
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
    let private adminAuthInvalid context =
        writeDomainProblem context StatusCodes.Status400BadRequest "admin.auth.request_invalid" "Invalid admin authentication request" "The login request body is invalid."

    let private adminCredentialsInvalid context =
        let problem: ProblemDetailsDto =
            { Type = "https://web10.radio/problems/admin-auth-invalid-credentials"
              Title = "Invalid admin credentials"
              Status = StatusCodes.Status401Unauthorized
              TraceId = String.Empty
              Code = "admin.auth.invalid_credentials"
              Message = "The supplied username or password is invalid." }
        ApiJson.write context StatusCodes.Status401Unauthorized ApiJson.ProblemContentType problem

    let private sessionToken (context: HttpContext) =
        match context.Request.Cookies.TryGetValue(AdminSessionAuthentication.CookieName) with
        | true, value when not (String.IsNullOrWhiteSpace value) -> Some value
        | _ -> None

    let private activeAdminSession (context: HttpContext) =
        task {
            match sessionToken context with
            | None -> return Ok None
            | Some token ->
                let identity = context.RequestServices.GetRequiredService<AdminIdentityService>()
                return! identity.TryGetActiveSessionAsync token context.RequestAborted
        }

    let private adminLogin (context: HttpContext) =
        task {
            match! readJsonBody 4096 context with
            | BodyTooLarge ->
                do! writeRequestTooLarge context
                return StatusCodes.Status413PayloadTooLarge
            | BodyInvalid ->
                do! adminAuthInvalid context
                return StatusCodes.Status400BadRequest
            | BodyParsed root when not (hasExactProperties (Set.ofList [ "username"; "password" ]) root) ->
                do! adminAuthInvalid context
                return StatusCodes.Status400BadRequest
            | BodyParsed root ->
                match tryString "username" root, tryString "password" root with
                | Some username, Some password when username.Trim().Length >= 1 && username.Trim().Length <= 64 && password.Length >= 12 && password.Length <= 256 ->
                    let identity = context.RequestServices.GetRequiredService<AdminIdentityService>()
                    let! result = identity.LoginAsync username password context.RequestAborted
                    match result with
                    | Error _ ->
                        do! writeRepositoryFailure context
                        return StatusCodes.Status500InternalServerError
                    | Ok AdminLoginOutcome.InvalidCredentials ->
                        do! adminCredentialsInvalid context
                        return StatusCodes.Status401Unauthorized
                    | Ok(AdminLoginOutcome.Authenticated login) ->
                        let maxAge = int AdminSessionAuthentication.SessionLifetime.TotalSeconds
                        context.Response.Headers["Set-Cookie"] <- StringValues(sprintf "%s=%s; Max-Age=%d; Path=/api/v0/admin; HttpOnly; SameSite=Strict" AdminSessionAuthentication.CookieName login.SessionToken maxAge)
                        do! writeOk context { Username = login.Username; CsrfToken = login.CsrfToken; DevelopmentFixturesEnabled = login.DevelopmentFixturesEnabled }
                        return StatusCodes.Status200OK
                | _ ->
                    do! adminAuthInvalid context
                    return StatusCodes.Status400BadRequest
        }

    let private adminSession (context: HttpContext) =
        task {
            match! activeAdminSession context with
            | Error _ ->
                do! writeRepositoryFailure context
                return StatusCodes.Status500InternalServerError
            | Ok None ->
                do! writeDomainProblem context StatusCodes.Status401Unauthorized "admin.auth.required" "Admin authentication required" "An active admin session is required."
                return StatusCodes.Status401Unauthorized
            | Ok(Some active) ->
                do! writeOk context { Username = active.Username; CsrfToken = active.Session.CsrfToken; DevelopmentFixturesEnabled = active.DevelopmentFixturesEnabled }
                return StatusCodes.Status200OK
        }

    let private csrfInvalid context =
        writeDomainProblem context StatusCodes.Status403Forbidden "admin.auth.csrf_invalid" "Invalid CSRF token" "A valid X-CSRF-Token header is required."

    let private csrfProtected (handler: ApiRouteHandler) (context: HttpContext) =
        task {
            match! activeAdminSession context with
            | Error _ ->
                do! writeRepositoryFailure context
                return StatusCodes.Status500InternalServerError
            | Ok None ->
                do! writeDomainProblem context StatusCodes.Status401Unauthorized "admin.auth.required" "Admin authentication required" "An active admin session is required."
                return StatusCodes.Status401Unauthorized
            | Ok(Some active) ->
                let mutable values = StringValues()
                let identity = context.RequestServices.GetRequiredService<AdminIdentityService>()
                let valid = context.Request.Headers.TryGetValue("X-CSRF-Token", &values) && values.Count = 1 && not (String.IsNullOrWhiteSpace values[0]) && identity.CsrfMatches active.Session values[0]
                if valid then
                    return! handler context
                else
                    do! csrfInvalid context
                    return StatusCodes.Status403Forbidden
        }

    let private adminLogout (context: HttpContext) =
        task {
            match! readJsonBody 4096 context with
            | BodyParsed root when isExactEmptyJsonObject root ->
                match! activeAdminSession context with
                | Error _ ->
                    do! writeRepositoryFailure context
                    return StatusCodes.Status500InternalServerError
                | Ok None ->
                    do! writeDomainProblem context StatusCodes.Status401Unauthorized "admin.auth.required" "Admin authentication required" "An active admin session is required."
                    return StatusCodes.Status401Unauthorized
                | Ok(Some active) ->
                    let identity = context.RequestServices.GetRequiredService<AdminIdentityService>()
                    let! revoked = identity.RevokeSessionAsync active.Session.Id context.RequestAborted
                    match revoked with
                    | Error _ ->
                        do! writeRepositoryFailure context
                        return StatusCodes.Status500InternalServerError
                    | Ok _ ->
                        context.Response.Headers["Set-Cookie"] <- StringValues(sprintf "%s=; Max-Age=0; Path=/api/v0/admin; HttpOnly; SameSite=Strict" AdminSessionAuthentication.CookieName)
                        context.Response.StatusCode <- StatusCodes.Status204NoContent
                        return StatusCodes.Status204NoContent
            | BodyTooLarge ->
                do! writeRequestTooLarge context
                return StatusCodes.Status413PayloadTooLarge
            | _ ->
                do! adminAuthInvalid context
                return StatusCodes.Status400BadRequest
        }

    let private publishEvent (context: HttpContext) (eventType: DomainEventType) (producer: string) (payload: string) : Task<bool> =
        task {
            let idGenerator = context.RequestServices.GetRequiredService<IIdGenerator>()
            let clock = context.RequestServices.GetRequiredService<IClock>()
            match DomainEventEnvelope.create idGenerator clock eventType producer None None payload with
            | Error _ -> return false
            | Ok envelope ->
                let publisher = context.RequestServices.GetRequiredService<IDomainEventPublisher>()
                let! result = publisher.PublishDurableAsync envelope context.RequestAborted
                return Result.isOk result
        }
    let private libraryScan (context: HttpContext) =
        task {
            match! readJsonBody (16 * 1024) context with
            | BodyTooLarge ->
                do! writeRequestTooLarge context
                return StatusCodes.Status413PayloadTooLarge
            | BodyInvalid ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "library.scan.request_invalid" "Invalid library scan request" "The scan request body is invalid."
                return StatusCodes.Status400BadRequest
            | BodyParsed root ->
                let backendId =
                    if isExactEmptyJsonObject root then Some None
                    elif hasExactProperties (Set.ofList [ "storageBackendId" ]) root then tryString "storageBackendId" root |> Option.bind tryPositiveGuid |> Option.map Some
                    else None
                match backendId with
                | None ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "library.scan.request_invalid" "Invalid library scan request" "The scan request body is invalid."
                    return StatusCodes.Status400BadRequest
                | Some storageBackendId ->
                    let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                    let clock = context.RequestServices.GetRequiredService<IClock>()
                    let ids = context.RequestServices.GetRequiredService<IIdGenerator>()
                    let! result = LibraryScanRepository.createOrGetActiveJob source (ids.NewId()) storageBackendId clock.UtcNow context.RequestAborted
                    match result with
                    | Error _ ->
                        do! writeRepositoryFailure context
                        return StatusCodes.Status500InternalServerError
                    | Ok StorageBackendNotFound ->
                        do! writeDomainProblem context StatusCodes.Status404NotFound "storage.backend_not_found" "Storage backend not found" "The requested storage backend is unavailable."
                        return StatusCodes.Status404NotFound
                    | Ok(Existing job) ->
                        do! ApiJson.write context StatusCodes.Status202Accepted ApiJson.JsonContentType { ScanJobId = job.Id.ToString("D") }
                        return StatusCodes.Status202Accepted
                    | Ok(Created job) ->
                        let payload = JsonSerializer.Serialize({| libraryScanJobId = job.Id.ToString("D") |}, ApiJson.options)
                        let! published = publishEvent context DomainEventType.LibraryScanRequested "Web10.Radio.API.Admin" payload
                        if published then
                            do! ApiJson.write context StatusCodes.Status202Accepted ApiJson.JsonContentType { ScanJobId = job.Id.ToString("D") }
                            return StatusCodes.Status202Accepted
                        else
                            do! writeRepositoryFailure context
                            return StatusCodes.Status500InternalServerError
        }

    let private libraryScanStatus (context: HttpContext) =
        task {
            match parseGuidRoute "scanJobId" context with
            | None ->
                do! writeDomainProblem context StatusCodes.Status404NotFound "library.scan_not_found" "Library scan not found" "The scan job does not exist."
                return StatusCodes.Status404NotFound
            | Some jobId ->
                let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let! result = LibraryScanRepository.getJobStatus source jobId context.RequestAborted
                match result with
                | Error _ ->
                    do! repositoryReadFailed context
                    return StatusCodes.Status500InternalServerError
                | Ok None ->
                    do! writeDomainProblem context StatusCodes.Status404NotFound "library.scan_not_found" "Library scan not found" "The scan job does not exist."
                    return StatusCodes.Status404NotFound
                | Ok(Some job) ->
                    do! writeOk context (toScanStatusDto job)
                    return StatusCodes.Status200OK
        }

    let private adminTracks (context: HttpContext) =
        task {
            let query = if context.Request.Query.ContainsKey("query") then context.Request.Query["query"].ToString() else String.Empty
            let limitText = if context.Request.Query.ContainsKey("limit") then context.Request.Query["limit"].ToString() else "100"
            let mutable limit = 0
            if query.Length > 200 || not (Int32.TryParse(limitText, &limit)) || limit < 1 || limit > 100 then
                do! writeDomainProblem context StatusCodes.Status400BadRequest "track.request_invalid" "Invalid track request" "The track query or limit is invalid."
                return StatusCodes.Status400BadRequest
            else
                let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let! result = AdminContentRepository.listActiveTracks source query limit context.RequestAborted
                match result with
                | Error _ ->
                    do! repositoryReadFailed context
                    return StatusCodes.Status500InternalServerError
                | Ok tracks ->
                    do! writeOk context (tracks |> List.map toTrackDto)
                    return StatusCodes.Status200OK
        }

    let private adminQueueTrack (context: HttpContext) =
        task {
            match! readJsonBody (16 * 1024) context with
            | BodyTooLarge ->
                do! writeRequestTooLarge context
                return StatusCodes.Status413PayloadTooLarge
            | BodyParsed root when hasExactProperties (Set.ofList [ "trackId" ]) root ->
                match tryString "trackId" root |> Option.bind tryPositiveGuid with
                | None ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "playback.request_invalid" "Invalid playback request" "The track identifier is invalid."
                    return StatusCodes.Status400BadRequest
                | Some trackId ->
                    let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                    let clock = context.RequestServices.GetRequiredService<IClock>()
                    let ids = context.RequestServices.GetRequiredService<IIdGenerator>()
                    let! result = AdminContentRepository.enqueueAdminTrack source { Id = ids.NewId(); TrackId = trackId; RequestedAtUtc = clock.UtcNow } context.RequestAborted
                    match result with
                    | Error _ ->
                        do! writeRepositoryFailure context
                        return StatusCodes.Status500InternalServerError
                    | Ok AdminContentMutation.NotFound ->
                        do! writeDomainProblem context StatusCodes.Status404NotFound "playback.not_found" "Track not found" "The requested track is unavailable."
                        return StatusCodes.Status404NotFound
                    | Ok AdminContentMutation.Conflict ->
                        do! writeDomainProblem context StatusCodes.Status409Conflict "playback.conflict" "Playback conflict" "The track could not be queued."
                        return StatusCodes.Status409Conflict
                    | Ok(AdminContentMutation.Applied item) ->
                        do! ApiJson.write context StatusCodes.Status202Accepted ApiJson.JsonContentType { QueueItemId = item.Id.ToString("D") }
                        return StatusCodes.Status202Accepted
            | _ ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "playback.request_invalid" "Invalid playback request" "The playback request body is invalid."
                return StatusCodes.Status400BadRequest
        }
    let private streamHeartbeat (context: HttpContext) =
        task {
            use attempt = FlowTelemetry.start FlowTelemetry.StreamNodeHeartbeat

            let finish outcome metricTags =
                FlowTelemetry.finish outcome metricTags attempt |> ignore

            try
                match! readJsonBody PlaybackCallbackMaxBodyBytes context with
                | BodyTooLarge ->
                    do! writeRequestTooLarge context
                    finish "invalid" []
                    return StatusCodes.Status413PayloadTooLarge
                | BodyParsed root when hasExactProperties (Set.ofList [ "status"; "failureReason"; "metadata" ]) root ->
                    let status =
                        tryString "status" root
                        |> Option.bind (function
                            | "starting" -> Some("Starting", "starting")
                            | "live" -> Some("Live", "live")
                            | "degraded" -> Some("Degraded", "degraded")
                            | "restarting" -> Some("Restarting", "restarting")
                            | "failed" -> Some("Failed", "failed")
                            | "offline" -> Some("Offline", "offline")
                            | _ -> None)

                    let reason = tryNullableString "failureReason" root

                    let validNullableNonNegativeInteger name metadata =
                        match tryProperty name metadata with
                        | Some value when value.ValueKind = JsonValueKind.Null -> true
                        | Some value when value.ValueKind = JsonValueKind.Number ->
                            let mutable parsed = 0
                            value.TryGetInt32(&parsed) && parsed >= 0
                        | _ -> false

                    let validNullableGuid metadata =
                        match tryProperty "activeQueueItemId" metadata with
                        | Some value when value.ValueKind = JsonValueKind.Null -> true
                        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString() |> tryPositiveGuid |> Option.isSome
                        | _ -> false

                    match status, reason, tryProperty "metadata" root with
                    | Some(persistedStatus, telemetryStatus), Some failureReason, Some metadata when hasExactProperties (Set.ofList [ "bitrateKbps"; "restartAttempt"; "activeQueueItemId" ]) metadata && validNullableNonNegativeInteger "bitrateKbps" metadata && validNullableNonNegativeInteger "restartAttempt" metadata && validNullableGuid metadata ->
                        FlowTelemetry.addTag "status" (box telemetryStatus) attempt
                        let metricTags = [ FlowTelemetry.status telemetryStatus ]
                        let payload = JsonSerializer.Serialize({| status = persistedStatus; failureReason = failureReason |> Option.defaultValue null; metadata = metadata |}, ApiJson.options)
                        let! published = publishEvent context DomainEventType.StreamNodeHeartbeatReceived "Web10.Radio.StreamNode" payload

                        if published then
                            context.Response.StatusCode <- StatusCodes.Status204NoContent
                            finish "accepted" metricTags
                            return StatusCodes.Status204NoContent
                        else
                            do! writeRepositoryFailure context
                            finish "error" metricTags
                            return StatusCodes.Status500InternalServerError
                    | _ ->
                        do! writeDomainProblem context StatusCodes.Status400BadRequest "stream-node.heartbeat.invalid" "Invalid stream-node heartbeat" "The heartbeat payload is invalid."
                        finish "invalid" []
                        return StatusCodes.Status400BadRequest
                | _ ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "stream-node.heartbeat.invalid" "Invalid stream-node heartbeat" "The heartbeat payload is invalid."
                    finish "invalid" []
                    return StatusCodes.Status400BadRequest
            with error ->
                FlowTelemetry.finishError "error" [] error attempt |> ignore
                return raise error
        }

    let private currentPlaybackAssignment (context: HttpContext) =
        task {
            let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let! result = PlaybackQueueRepository.getCurrentAssignment source context.RequestAborted
            match result with
            | Error _ ->
                do! repositoryReadFailed context
                return StatusCodes.Status500InternalServerError
            | Ok None -> context.Response.StatusCode <- StatusCodes.Status204NoContent; return StatusCodes.Status204NoContent
            | Ok(Some assignment) ->
                let dto: CurrentPlaybackAssignmentDto = { QueueItemId = assignment.QueueItemId.ToString("D"); ClaimOwner = assignment.ClaimOwner.ToString("D"); ClaimAttempt = assignment.ClaimAttempt; TrackId = assignment.TrackId.ToString("D"); CachePath = assignment.CachePath; ContentType = assignment.ContentType; Title = assignment.Title; Artist = assignment.Artist; DurationMs = max 0 assignment.DurationMs }
                do! writeOk context dto
                return StatusCodes.Status200OK
        }

    let private streamNodeControl (context: HttpContext) =
        task {
            let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let ids = context.RequestServices.GetRequiredService<IIdGenerator>()
            let clock = context.RequestServices.GetRequiredService<IClock>()
            let! result = StreamNodeControlRepository.getOrCreate source (ids.NewId()) clock.UtcNow context.RequestAborted
            match result with
            | Error _ ->
                do! repositoryReadFailed context
                return StatusCodes.Status500InternalServerError
            | Ok state ->
                do! writeOk context (toControlDto state)
                return StatusCodes.Status200OK
        }

    let private adminStreamControl operation (context: HttpContext) =
        task {
            match! readJsonBody (16 * 1024) context with
            | BodyParsed root when isExactEmptyJsonObject root ->
                let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let ids = context.RequestServices.GetRequiredService<IIdGenerator>()
                let clock = context.RequestServices.GetRequiredService<IClock>()
                let! initialized = StreamNodeControlRepository.getOrCreate source (ids.NewId()) clock.UtcNow context.RequestAborted
                let! result =
                    match initialized, operation with
                    | Error error, _ -> Task.FromResult(Error error)
                    | Ok _, "start" -> StreamNodeControlRepository.setDesiredState source Running clock.UtcNow context.RequestAborted
                    | Ok _, "stop" -> StreamNodeControlRepository.setDesiredState source Stopped clock.UtcNow context.RequestAborted
                    | Ok _, _ -> StreamNodeControlRepository.restart source clock.UtcNow context.RequestAborted
                match result with
                | Error _ ->
                    do! writeRepositoryFailure context
                    return StatusCodes.Status500InternalServerError
                | Ok state ->
                    do! ApiJson.write context StatusCodes.Status202Accepted ApiJson.JsonContentType (toControlDto state)
                    return StatusCodes.Status202Accepted
            | BodyTooLarge ->
                do! writeRequestTooLarge context
                return StatusCodes.Status413PayloadTooLarge
            | _ ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "stream-node.control.request_invalid" "Invalid stream-node control request" "The control request body must be an empty object."
                return StatusCodes.Status400BadRequest
        }

    let private adminStreamStatus (context: HttpContext) =
        task {
            let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let ids = context.RequestServices.GetRequiredService<IIdGenerator>()
            let clock = context.RequestServices.GetRequiredService<IClock>()
            let! control = StreamNodeControlRepository.getOrCreate source (ids.NewId()) clock.UtcNow context.RequestAborted
            let! snapshot = PlayerStateReadModel.loadSnapshot source clock context.RequestAborted
            match control, snapshot with
            | Ok state, Ok player ->
                let fresh = player.Stream.Status <> "offline"
                let controlDto = toControlDto state
                let dto: StreamNodeStatusDto =
                    { Status = player.Stream.Status
                      DesiredState = controlDto.DesiredState
                      LastHeartbeatUtc = (if fresh then player.Stream.StartedAtUtc else null)
                      FailureReason = (if fresh then player.Stream.OfflineReason else null)
                      BitrateKbps = (if fresh then player.Stream.BitrateKbps else 0)
                      RestartGeneration = controlDto.RestartGeneration }
                do! writeOk context dto
                return StatusCodes.Status200OK
            | _ ->
                do! repositoryReadFailed context
                return StatusCodes.Status500InternalServerError
        }
    let private adminDonationGoalUpdate (context: HttpContext) =
        task {
            match! readJsonBody (16 * 1024) context with
            | BodyTooLarge ->
                do! writeRequestTooLarge context
                return StatusCodes.Status413PayloadTooLarge
            | BodyParsed root when hasExactProperties (Set.ofList [ "title"; "goalStars" ]) root ->
                match tryString "title" root, tryProperty "goalStars" root with
                | Some title, Some starsElement when starsElement.ValueKind = JsonValueKind.Number ->
                    let trimmed = title.Trim()
                    let mutable stars = 0
                    if trimmed.Length < 1 || trimmed.Length > 120 || not (starsElement.TryGetInt32(&stars)) || stars < 1 then
                        do! writeDomainProblem context StatusCodes.Status400BadRequest "donation.goal.request_invalid" "Invalid donation goal request" "The donation goal body is invalid."
                        return StatusCodes.Status400BadRequest
                    else
                        let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                        let ids = context.RequestServices.GetRequiredService<IIdGenerator>()
                        let clock = context.RequestServices.GetRequiredService<IClock>()
                        let! result = AdminContentRepository.upsertDonationGoal source { Id = ids.NewId(); Title = trimmed; GoalStars = stars; UpdatedAtUtc = clock.UtcNow } context.RequestAborted
                        match result with
                        | Error _ ->
                            do! writeRepositoryFailure context
                            return StatusCodes.Status500InternalServerError
                        | Ok _ ->
                            let payload = JsonSerializer.Serialize({| title = trimmed; goalStars = stars |}, ApiJson.options)
                            let! published = publishEvent context DomainEventType.AdminGoalChanged "Web10.Radio.API.Admin" payload
                            if not published then
                                do! writeRepositoryFailure context
                                return StatusCodes.Status500InternalServerError
                            else
                                let! goal = PlayerStateReadModel.loadDonationGoal source context.RequestAborted
                                match goal with
                                | Ok dto ->
                                    do! writeOk context dto
                                    return StatusCodes.Status200OK
                                | Error _ ->
                                    do! repositoryReadFailed context
                                    return StatusCodes.Status500InternalServerError
                | _ ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "donation.goal.request_invalid" "Invalid donation goal request" "The donation goal body is invalid."
                    return StatusCodes.Status400BadRequest
            | _ ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "donation.goal.request_invalid" "Invalid donation goal request" "The donation goal body is invalid."
                return StatusCodes.Status400BadRequest
        }

    let private socialKinds = Set.ofList [ "telegram"; "youtube"; "instagram"; "discord"; "external" ]

    let private parseSocialReplacement (ids: IIdGenerator) (element: JsonElement) =
        if not (hasExactProperties (Set.ofList [ "id"; "kind"; "name"; "handle"; "url"; "glyph"; "color"; "qrImageUrl"; "isFeatured" ]) element) then None
        else
            match tryProperty "id" element, tryString "kind" element, tryString "name" element, tryNullableString "handle" element, tryString "url" element, tryNullableString "glyph" element, tryNullableString "color" element, tryNullableString "qrImageUrl" element, tryProperty "isFeatured" element with
            | Some idElement, Some kind, Some name, Some handle, Some url, Some glyph, Some color, Some qrImageUrl, Some featured when idElement.ValueKind = JsonValueKind.Null || idElement.ValueKind = JsonValueKind.String ->
                let id = if idElement.ValueKind = JsonValueKind.Null then Some(ids.NewId()) else idElement.GetString() |> tryPositiveGuid
                let parsedUrl = match Uri.TryCreate(url, UriKind.Absolute) with | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> Some uri | _ -> None
                let colorValid = color |> Option.forall (fun value -> value.Length = 7 && value.[0] = '#' && value |> Seq.skip 1 |> Seq.forall Uri.IsHexDigit)
                if id.IsNone || not (socialKinds.Contains kind) || name.Trim().Length < 1 || not colorValid || parsedUrl.IsNone || featured.ValueKind <> JsonValueKind.True && featured.ValueKind <> JsonValueKind.False then None
                else Some { Id = id.Value; Kind = kind; Name = name.Trim(); Handle = handle |> Option.map _.Trim(); Url = parsedUrl.Value.AbsoluteUri; Glyph = glyph; Color = color; QrImageUrl = qrImageUrl; IsFeatured = featured.GetBoolean() }
            | _ -> None

    let private adminSocialLinksUpdate (context: HttpContext) =
        task {
            match! readJsonBody (64 * 1024) context with
            | BodyTooLarge ->
                do! writeRequestTooLarge context
                return StatusCodes.Status413PayloadTooLarge
            | BodyParsed root when root.ValueKind = JsonValueKind.Array && root.GetArrayLength() <= 50 ->
                let ids = context.RequestServices.GetRequiredService<IIdGenerator>()
                let parsed = root.EnumerateArray() |> Seq.map (parseSocialReplacement ids) |> Seq.toList
                if parsed |> List.exists Option.isNone then
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "social-links.request_invalid" "Invalid social links request" "The social links body is invalid."
                    return StatusCodes.Status400BadRequest
                else
                    let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                    let clock = context.RequestServices.GetRequiredService<IClock>()
                    let! result = AdminContentRepository.replaceSocialLinks source (parsed |> List.choose id) clock.UtcNow context.RequestAborted
                    match result with
                    | Error _ ->
                        do! writeRepositoryFailure context
                        return StatusCodes.Status500InternalServerError
                    | Ok AdminContentMutation.NotFound ->
                        do! writeDomainProblem context StatusCodes.Status404NotFound "social-links.not_found" "Social link not found" "A referenced social link does not exist."
                        return StatusCodes.Status404NotFound
                    | Ok AdminContentMutation.Conflict ->
                        do! writeDomainProblem context StatusCodes.Status409Conflict "social-links.conflict" "Social links conflict" "The social links replacement conflicts with current state."
                        return StatusCodes.Status409Conflict
                    | Ok(AdminContentMutation.Applied links) ->
                        let! published = publishEvent context DomainEventType.SocialLinkChanged "Web10.Radio.API.Admin" (JsonSerializer.Serialize({| count = List.length links |}, ApiJson.options))
                        if not published then
                            do! writeRepositoryFailure context
                            return StatusCodes.Status500InternalServerError
                        else
                            do! writeOk context (links |> List.map (fun (link: SocialLink) -> ({ Id = link.Id.ToString("D"); Kind = link.Kind; Name = link.Name; Handle = link.Handle; Url = link.Url; Glyph = link.Glyph; Color = link.Color; QrImageUrl = link.QrImageUrl; IsFeatured = link.IsFeatured } : SocialLinkDto)))
                            return StatusCodes.Status200OK
            | _ ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "social-links.request_invalid" "Invalid social links request" "The social links body is invalid."
                return StatusCodes.Status400BadRequest
        }
    let private parsePlaylistBody root =
        if not (hasExactProperties (Set.ofList [ "name"; "description"; "isActive" ]) root) then None
        else
            match tryString "name" root, tryNullableString "description" root, tryProperty "isActive" root with
            | Some name, Some description, Some active when active.ValueKind = JsonValueKind.True || active.ValueKind = JsonValueKind.False ->
                let trimmed = name.Trim()
                let normalizedDescription = description |> Option.map _.Trim()
                if trimmed.Length < 1 || trimmed.Length > 120 || normalizedDescription |> Option.exists (fun text -> text.Length > 1000) then None
                else Some(trimmed, normalizedDescription, active.GetBoolean())
            | _ -> None

    let private adminPlaylists (context: HttpContext) =
        task {
            let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let! result = AdminContentRepository.listPlaylists source context.RequestAborted
            match result with
            | Error _ ->
                do! repositoryReadFailed context
                return StatusCodes.Status500InternalServerError
            | Ok playlists ->
                do! writeOk context (playlists |> List.map toPlaylistDto)
                return StatusCodes.Status200OK
        }

    let private createPlaylist (context: HttpContext) =
        task {
            match! readJsonBody (16 * 1024) context with
            | BodyTooLarge ->
                do! writeRequestTooLarge context
                return StatusCodes.Status413PayloadTooLarge
            | BodyParsed root ->
                match parsePlaylistBody root with
                | None ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "playlist.request_invalid" "Invalid playlist request" "The playlist body is invalid."
                    return StatusCodes.Status400BadRequest
                | Some(name, description, active) ->
                    let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                    let ids = context.RequestServices.GetRequiredService<IIdGenerator>()
                    let clock = context.RequestServices.GetRequiredService<IClock>()
                    let now = clock.UtcNow
                    let! result = AdminContentRepository.createPlaylist source { Id = ids.NewId(); Name = name; Description = description; IsActive = active; CreatedAtUtc = now; UpdatedAtUtc = now } context.RequestAborted
                    match result with
                    | Error _ ->
                        do! writeRepositoryFailure context
                        return StatusCodes.Status500InternalServerError
                    | Ok AdminContentMutation.NotFound ->
                        do! writeDomainProblem context StatusCodes.Status404NotFound "playlist.not_found" "Playlist not found" "The playlist does not exist."
                        return StatusCodes.Status404NotFound
                    | Ok AdminContentMutation.Conflict ->
                        do! writeDomainProblem context StatusCodes.Status409Conflict "playlist.conflict" "Playlist conflict" "The playlist conflicts with current state."
                        return StatusCodes.Status409Conflict
                    | Ok(AdminContentMutation.Applied playlist) ->
                        do! ApiJson.write context StatusCodes.Status201Created ApiJson.JsonContentType (toPlaylistDto playlist)
                        return StatusCodes.Status201Created
            | _ ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "playlist.request_invalid" "Invalid playlist request" "The playlist body is invalid."
                return StatusCodes.Status400BadRequest
        }

    let private updatePlaylist (context: HttpContext) =
        task {
            match parseGuidRoute "playlistId" context, (readJsonBody (16 * 1024) context) with
            | None, _ ->
                do! writeDomainProblem context StatusCodes.Status404NotFound "playlist.not_found" "Playlist not found" "The playlist does not exist."
                return StatusCodes.Status404NotFound
            | Some playlistId, bodyTask ->
                match! bodyTask with
                | BodyTooLarge ->
                    do! writeRequestTooLarge context
                    return StatusCodes.Status413PayloadTooLarge
                | BodyParsed root ->
                    match parsePlaylistBody root with
                    | None ->
                        do! writeDomainProblem context StatusCodes.Status400BadRequest "playlist.request_invalid" "Invalid playlist request" "The playlist body is invalid."
                        return StatusCodes.Status400BadRequest
                    | Some(name, description, active) ->
                        let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                        let clock = context.RequestServices.GetRequiredService<IClock>()
                        let! result = AdminContentRepository.updatePlaylist source playlistId { Name = name; Description = description; IsActive = active; UpdatedAtUtc = clock.UtcNow } context.RequestAborted
                        match result with
                        | Error _ ->
                            do! writeRepositoryFailure context
                            return StatusCodes.Status500InternalServerError
                        | Ok AdminContentMutation.NotFound ->
                            do! writeDomainProblem context StatusCodes.Status404NotFound "playlist.not_found" "Playlist not found" "The playlist does not exist."
                            return StatusCodes.Status404NotFound
                        | Ok AdminContentMutation.Conflict ->
                            do! writeDomainProblem context StatusCodes.Status409Conflict "playlist.conflict" "Playlist conflict" "The playlist conflicts with current state."
                            return StatusCodes.Status409Conflict
                        | Ok(AdminContentMutation.Applied playlist) ->
                            do! writeOk context (toPlaylistDto playlist)
                            return StatusCodes.Status200OK
                | _ ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "playlist.request_invalid" "Invalid playlist request" "The playlist body is invalid."
                    return StatusCodes.Status400BadRequest
        }

    let private playlistItems (context: HttpContext) =
        task {
            match parseGuidRoute "playlistId" context with
            | None ->
                do! writeDomainProblem context StatusCodes.Status404NotFound "playlist.not_found" "Playlist not found" "The playlist does not exist."
                return StatusCodes.Status404NotFound
            | Some playlistId ->
                let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let! result = AdminContentRepository.listPlaylistItems source playlistId context.RequestAborted
                match result with
                | Error _ ->
                    do! repositoryReadFailed context
                    return StatusCodes.Status500InternalServerError
                | Ok items ->
                    do! writeOk context (items |> List.map toPlaylistItemDto)
                    return StatusCodes.Status200OK
        }

    let private createPlaylistItem (context: HttpContext) =
        task {
            match parseGuidRoute "playlistId" context with
            | None ->
                do! writeDomainProblem context StatusCodes.Status404NotFound "playlist.not_found" "Playlist not found" "The playlist does not exist."
                return StatusCodes.Status404NotFound
            | Some playlistId ->
                match! readJsonBody (16 * 1024) context with
                | BodyTooLarge ->
                    do! writeRequestTooLarge context
                    return StatusCodes.Status413PayloadTooLarge
                | BodyParsed root when hasExactProperties (Set.ofList [ "trackId" ]) root ->
                    match tryString "trackId" root |> Option.bind tryPositiveGuid with
                    | None ->
                        do! writeDomainProblem context StatusCodes.Status400BadRequest "playlist.request_invalid" "Invalid playlist request" "The playlist item body is invalid."
                        return StatusCodes.Status400BadRequest
                    | Some trackId ->
                        let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                        let ids = context.RequestServices.GetRequiredService<IIdGenerator>()
                        let clock = context.RequestServices.GetRequiredService<IClock>()
                        let! result = AdminContentRepository.createPlaylistItem source playlistId { Id = ids.NewId(); TrackId = trackId; CreatedAtUtc = clock.UtcNow } context.RequestAborted
                        match result with
                        | Error _ ->
                            do! writeRepositoryFailure context
                            return StatusCodes.Status500InternalServerError
                        | Ok AdminContentMutation.NotFound ->
                            do! writeDomainProblem context StatusCodes.Status404NotFound "playlist.not_found" "Playlist not found" "The playlist or track does not exist."
                            return StatusCodes.Status404NotFound
                        | Ok AdminContentMutation.Conflict ->
                            do! writeDomainProblem context StatusCodes.Status409Conflict "playlist.conflict" "Playlist conflict" "The playlist conflicts with current state."
                            return StatusCodes.Status409Conflict
                        | Ok(AdminContentMutation.Applied item) ->
                            do! ApiJson.write context StatusCodes.Status201Created ApiJson.JsonContentType (toPlaylistItemDto item)
                            return StatusCodes.Status201Created
                | _ ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "playlist.request_invalid" "Invalid playlist request" "The playlist item body is invalid."
                    return StatusCodes.Status400BadRequest
        }
    let private replacePlaylistItems (context: HttpContext) =
        task {
            match parseGuidRoute "playlistId" context with
            | None ->
                do! writeDomainProblem context StatusCodes.Status404NotFound "playlist.not_found" "Playlist not found" "The playlist does not exist."
                return StatusCodes.Status404NotFound
            | Some playlistId ->
                match! readJsonBody (64 * 1024) context with
                | BodyTooLarge ->
                    do! writeRequestTooLarge context
                    return StatusCodes.Status413PayloadTooLarge
                | BodyParsed root when hasExactProperties (Set.ofList [ "items" ]) root ->
                    match tryProperty "items" root with
                    | Some items when items.ValueKind = JsonValueKind.Array ->
                        let ids = context.RequestServices.GetRequiredService<IIdGenerator>()
                        let parsed =
                            items.EnumerateArray()
                            |> Seq.map (fun item ->
                                if hasExactProperties (Set.ofList [ "id"; "trackId" ]) item then
                                    match tryProperty "id" item, tryString "trackId" item |> Option.bind tryPositiveGuid with
                                    | Some id, Some trackId when id.ValueKind = JsonValueKind.Null -> Some { Id = ids.NewId(); TrackId = trackId }
                                    | Some id, Some trackId when id.ValueKind = JsonValueKind.String -> id.GetString() |> tryPositiveGuid |> Option.map (fun itemId -> { Id = itemId; TrackId = trackId })
                                    | _ -> None
                                else None)
                            |> Seq.toList
                        if parsed |> List.exists Option.isNone then
                            do! writeDomainProblem context StatusCodes.Status400BadRequest "playlist.request_invalid" "Invalid playlist request" "The playlist items body is invalid."
                            return StatusCodes.Status400BadRequest
                        else
                            let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                            let clock = context.RequestServices.GetRequiredService<IClock>()
                            let! result = AdminContentRepository.replacePlaylistItems source playlistId (parsed |> List.choose id) clock.UtcNow context.RequestAborted
                            match result with
                            | Error _ ->
                                do! writeRepositoryFailure context
                                return StatusCodes.Status500InternalServerError
                            | Ok AdminContentMutation.NotFound ->
                                do! writeDomainProblem context StatusCodes.Status404NotFound "playlist.not_found" "Playlist not found" "The playlist or item does not exist."
                                return StatusCodes.Status404NotFound
                            | Ok AdminContentMutation.Conflict ->
                                do! writeDomainProblem context StatusCodes.Status409Conflict "playlist.conflict" "Playlist conflict" "The playlist conflicts with current state."
                                return StatusCodes.Status409Conflict
                            | Ok(AdminContentMutation.Applied values) ->
                                do! writeOk context (values |> List.map toPlaylistItemDto)
                                return StatusCodes.Status200OK
                    | _ ->
                        do! writeDomainProblem context StatusCodes.Status400BadRequest "playlist.request_invalid" "Invalid playlist request" "The playlist items body is invalid."
                        return StatusCodes.Status400BadRequest
                | _ ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "playlist.request_invalid" "Invalid playlist request" "The playlist items body is invalid."
                    return StatusCodes.Status400BadRequest
        }

    let private defaultStorageDto (options: StorageOptions) =
        match options.Type with
        | Local -> { Type = "local"; LocalRoot = options.LocalRoot; S3Bucket = null; S3Region = null; S3ServiceUrl = null; S3ForcePathStyle = false }
        | S3 -> { Type = "s3"; LocalRoot = null; S3Bucket = options.S3Bucket; S3Region = options.S3Region; S3ServiceUrl = options.S3ServiceUrl |> Option.map _.AbsoluteUri |> Option.defaultValue null; S3ForcePathStyle = options.S3ForcePathStyle }

    let private toAdditionalStorageDto (backend: AdditionalStorageBackend) : AdditionalStorageBackendDto =
        { Id = backend.Id.ToString("D"); Name = backend.Name; Type = backend.Type.ToLowerInvariant(); LocalRoot = backend.LocalRoot |> Option.defaultValue null; S3Bucket = backend.S3Bucket |> Option.defaultValue null; IsEnabled = backend.IsEnabled }

    let private adminStorage (context: HttpContext) =
        task {
            let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let options = context.RequestServices.GetRequiredService<StorageOptions>()
            let! result = AdminContentRepository.listAdditionalStorageBackends source context.RequestAborted
            match result with
            | Error _ ->
                do! repositoryReadFailed context
                return StatusCodes.Status500InternalServerError
            | Ok backends ->
                do! writeOk context { DefaultBackend = defaultStorageDto options; AdditionalBackends = backends |> List.map toAdditionalStorageDto }
                return StatusCodes.Status200OK
        }

    let private adminStorageUpdate (context: HttpContext) =
        task {
            match! readJsonBody (64 * 1024) context with
            | BodyTooLarge ->
                do! writeRequestTooLarge context
                return StatusCodes.Status413PayloadTooLarge
            | BodyParsed root when hasExactProperties (Set.ofList [ "additionalBackends" ]) root ->
                match tryProperty "additionalBackends" root with
                | Some array when array.ValueKind = JsonValueKind.Array && array.GetArrayLength() <= 20 ->
                    let ids = context.RequestServices.GetRequiredService<IIdGenerator>()
                    let parsed =
                        array.EnumerateArray()
                        |> Seq.map (fun item ->
                            if not (hasExactProperties (Set.ofList [ "id"; "name"; "type"; "localRoot"; "s3Bucket"; "isEnabled" ]) item) then None
                            else
                                match tryProperty "id" item, tryString "name" item, tryString "type" item, tryNullableString "localRoot" item, tryNullableString "s3Bucket" item, tryProperty "isEnabled" item with
                                | Some id, Some name, Some storageType, Some localRoot, Some bucket, Some enabled when (id.ValueKind = JsonValueKind.Null || id.ValueKind = JsonValueKind.String) && (enabled.ValueKind = JsonValueKind.True || enabled.ValueKind = JsonValueKind.False) ->
                                    let backendId = if id.ValueKind = JsonValueKind.Null then Some(ids.NewId()) else id.GetString() |> tryPositiveGuid
                                    let valid = name.Trim().Length >= 1 && ((storageType = "local" && localRoot |> Option.exists Path.IsPathFullyQualified && bucket.IsNone) || (storageType = "s3" && bucket |> Option.exists (fun text -> text.Trim().Length > 0) && localRoot.IsNone))
                                    if backendId.IsSome && valid then
                                        let replacement: AdditionalStorageBackendReplacement =
                                            { Id = backendId.Value
                                              Name = name.Trim()
                                              Type = (if storageType = "local" then "Local" else "S3")
                                              LocalRoot = localRoot |> Option.map _.Trim()
                                              S3Bucket = bucket |> Option.map _.Trim()
                                              IsEnabled = enabled.GetBoolean() }
                                        Some replacement
                                    else None
                                | _ -> None)
                        |> Seq.toList
                    if parsed |> List.exists Option.isNone then
                        do! writeDomainProblem context StatusCodes.Status400BadRequest "storage.request_invalid" "Invalid storage request" "The storage body is invalid."
                        return StatusCodes.Status400BadRequest
                    else
                        let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                        let options = context.RequestServices.GetRequiredService<StorageOptions>()
                        let clock = context.RequestServices.GetRequiredService<IClock>()
                        let! result = AdminContentRepository.replaceAdditionalStorageBackends source (parsed |> List.choose id) clock.UtcNow context.RequestAborted
                        match result with
                        | Error _ ->
                            do! writeRepositoryFailure context
                            return StatusCodes.Status500InternalServerError
                        | Ok AdminContentMutation.NotFound ->
                            do! writeDomainProblem context StatusCodes.Status404NotFound "storage.not_found" "Storage backend not found" "A referenced storage backend does not exist."
                            return StatusCodes.Status404NotFound
                        | Ok AdminContentMutation.Conflict ->
                            do! writeDomainProblem context StatusCodes.Status409Conflict "storage.conflict" "Storage conflict" "The storage replacement conflicts with current state."
                            return StatusCodes.Status409Conflict
                        | Ok(AdminContentMutation.Applied backends) ->
                            do! writeOk context { DefaultBackend = defaultStorageDto options; AdditionalBackends = backends |> List.map toAdditionalStorageDto }
                            return StatusCodes.Status200OK
                | _ ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "storage.request_invalid" "Invalid storage request" "The storage body is invalid."
                    return StatusCodes.Status400BadRequest
            | _ ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "storage.request_invalid" "Invalid storage request" "The storage body is invalid."
                return StatusCodes.Status400BadRequest
        }
    let private developmentFixtureInvalid context =
        writeDomainProblem
            context
            StatusCodes.Status400BadRequest
            "dev.fixture.invalid"
            "Invalid development fixture request"
            "The development fixture body is invalid."

    let private createPaidVerticalSliceFixture (context: HttpContext) =
        task {
            match! readJsonBody (16 * 1024) context with
            | BodyParsed root when hasExactProperties (Set.ofList [ "fixtureKey" ]) root ->
                match tryString "fixtureKey" root with
                | Some fixtureKey when fixtureKey.Trim().Length >= 1 && fixtureKey.Trim().Length <= 64 ->
                    let options = context.RequestServices.GetRequiredService<Web10Options>()
                    let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                    let idGenerator = context.RequestServices.GetRequiredService<IIdGenerator>()
                    let clock = context.RequestServices.GetRequiredService<IClock>()

                    let! result =
                        DevelopmentFixtures.createPaidVerticalSlice
                            dataSource
                            idGenerator
                            clock
                            options.Telegram.SayPriceStars
                            (fixtureKey.Trim())
                            context.RequestAborted

                    match result with
                    | Ok fixture ->
                        do! writeOk context fixture
                        return StatusCodes.Status200OK
                    | Error _ ->
                        do! writeRepositoryFailure context
                        return StatusCodes.Status500InternalServerError
                | _ ->
                    do! developmentFixtureInvalid context
                    return StatusCodes.Status400BadRequest
            | _ ->
                do! developmentFixtureInvalid context
                return StatusCodes.Status400BadRequest
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
        map streamNode logger "POST" "/heartbeat" "/api/v0/stream-node/heartbeat" streamHeartbeat
        map streamNode logger "GET" "/playback/current" "/api/v0/stream-node/playback/current" currentPlaybackAssignment
        map streamNode logger "GET" "/control" "/api/v0/stream-node/control" streamNodeControl
        map streamNode logger "POST" "/playback/{queueItemId}/lease" "/api/v0/stream-node/playback/{queueItemId}/lease" playbackLease
        map streamNode logger "POST" "/playback/{queueItemId}/completion" "/api/v0/stream-node/playback/{queueItemId}/completion" playbackCompletion

        map app logger "POST" "/api/v0/admin/auth/login" "/api/v0/admin/auth/login" adminLogin
        let admin = app.MapGroup("/api/v0/admin")
        admin.RequireAuthorization(AdminSessionAuthentication.PolicyName) |> ignore
        map admin logger "GET" "/auth/session" "/api/v0/admin/auth/session" adminSession
        map admin logger "POST" "/auth/logout" "/api/v0/admin/auth/logout" (csrfProtected adminLogout)
        if app.Environment.IsDevelopment()
           && String.Equals(app.Configuration["DEV:FIXTURES_ENABLED"], "true", StringComparison.Ordinal) then
            map
                admin
                logger
                "POST"
                "/dev/fixtures/paid-vertical-slice"
                "/api/v0/admin/dev/fixtures/paid-vertical-slice"
                (csrfProtected createPaidVerticalSliceFixture)
        map admin logger "GET" "/social-links" "/api/v0/admin/social-links" adminSocialLinks
        map admin logger "PUT" "/social-links" "/api/v0/admin/social-links" (csrfProtected adminSocialLinksUpdate)
        map admin logger "GET" "/donation-goal" "/api/v0/admin/donation-goal" adminDonationGoal
        map admin logger "PUT" "/donation-goal" "/api/v0/admin/donation-goal" (csrfProtected adminDonationGoalUpdate)
        map admin logger "POST" "/library/scan" "/api/v0/admin/library/scan" (csrfProtected libraryScan)
        map admin logger "GET" "/library/scan/{scanJobId}" "/api/v0/admin/library/scan/{scanJobId}" libraryScanStatus
        map admin logger "GET" "/tracks" "/api/v0/admin/tracks" adminTracks
        map admin logger "POST" "/playback/queue" "/api/v0/admin/playback/queue" (csrfProtected adminQueueTrack)
        map admin logger "GET" "/playlists" "/api/v0/admin/playlists" adminPlaylists
        map admin logger "POST" "/playlists" "/api/v0/admin/playlists" (csrfProtected createPlaylist)
        map admin logger "PUT" "/playlists/{playlistId}" "/api/v0/admin/playlists/{playlistId}" (csrfProtected updatePlaylist)
        map admin logger "GET" "/playlists/{playlistId}/items" "/api/v0/admin/playlists/{playlistId}/items" playlistItems
        map admin logger "POST" "/playlists/{playlistId}/items" "/api/v0/admin/playlists/{playlistId}/items" (csrfProtected createPlaylistItem)
        map admin logger "PUT" "/playlists/{playlistId}/items" "/api/v0/admin/playlists/{playlistId}/items" (csrfProtected replacePlaylistItems)
        map admin logger "GET" "/say-messages" "/api/v0/admin/say-messages" adminSayMessages
        map admin logger "POST" "/say-messages/{messageId}/approve" "/api/v0/admin/say-messages/{messageId}/approve" (csrfProtected approveSayMessage)
        map admin logger "POST" "/say-messages/{messageId}/reject" "/api/v0/admin/say-messages/{messageId}/reject" (csrfProtected rejectSayMessage)
        map admin logger "GET" "/storage" "/api/v0/admin/storage" adminStorage
        map admin logger "PUT" "/storage" "/api/v0/admin/storage" (csrfProtected adminStorageUpdate)
        map admin logger "GET" "/stream-node/status" "/api/v0/admin/stream-node/status" adminStreamStatus
        map admin logger "POST" "/stream-node/start" "/api/v0/admin/stream-node/start" (csrfProtected (adminStreamControl "start"))
        map admin logger "POST" "/stream-node/stop" "/api/v0/admin/stream-node/stop" (csrfProtected (adminStreamControl "stop"))
        map admin logger "POST" "/stream-node/restart" "/api/v0/admin/stream-node/restart" (csrfProtected (adminStreamControl "restart"))
