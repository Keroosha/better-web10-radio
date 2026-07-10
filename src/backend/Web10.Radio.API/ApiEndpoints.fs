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
            let separatorIndex = text.IndexOf(' ')

            let commandToken, argument =
                if separatorIndex < 0 then
                    text, String.Empty
                else
                    text.Substring(0, separatorIndex), text.Substring(separatorIndex + 1).Trim()

            let commandWithoutBot =
                let botSeparatorIndex = commandToken.IndexOf('@')
                if botSeparatorIndex < 0 then commandToken else commandToken.Substring(0, botSeparatorIndex)

            if String.Equals(commandWithoutBot, expectedCommand, StringComparison.OrdinalIgnoreCase) then Some argument else None

    let private telegramActor (message: Message) =
        match message.From with
        | None -> None, None
        | Some (user: User) ->
            let displayName =
                match user.Username with
                | Some username when not (String.IsNullOrWhiteSpace username) -> Some username
                | _ ->
                    [ Some user.FirstName; user.LastName ]
                    |> List.choose id
                    |> String.concat " "
                    |> fun value -> if String.IsNullOrWhiteSpace value then None else Some value

            Some user.Id, displayName


    let private mapTelegramUpdate (update: Update) : Result<TelegramMappedEvent option, string> =
        match update.Message with
        | Some message ->
            match message.SuccessfulPayment with
            | Some payment when payment.TotalAmount > int64 Int32.MaxValue ->
                Error "Telegram payment amount exceeds the supported range."
            | Some payment ->
                let payload =
                    JsonSerializer.Serialize(
                        {| paymentId = payment.InvoicePayload
                           telegramPaymentChargeId = payment.TelegramPaymentChargeId
                           amountStars = int payment.TotalAmount
                           currency = payment.Currency |},
                        ApiJson.options
                    )

                Ok(Some { EventType = DomainEventType.DonationPaid; PayloadJson = payload })
            | None ->
                match message.Text with
                | Some text ->
                    let telegramUserId, displayName = telegramActor message

                    match tryCommandArgument "/request" text, tryCommandArgument "/say" text with
                    | Some query, _ when not (String.IsNullOrWhiteSpace query) ->
                        let payload =
                            JsonSerializer.Serialize(
                                {| query = query
                                   telegramUpdateId = update.UpdateId
                                   telegramMessageId = message.MessageId
                                   telegramUserId = telegramUserId
                                   displayName = displayName |},
                                ApiJson.options
                            )

                        Ok(Some { EventType = DomainEventType.TrackRequested; PayloadJson = payload })
                    | _, Some submittedText when not (String.IsNullOrWhiteSpace submittedText) ->
                        let payload =
                            JsonSerializer.Serialize(
                                {| text = submittedText
                                   telegramUpdateId = update.UpdateId
                                   telegramMessageId = message.MessageId
                                   telegramUserId = telegramUserId
                                   displayName = displayName |},
                                ApiJson.options
                            )

                        Ok(Some { EventType = DomainEventType.SayMessageSubmitted; PayloadJson = payload })
                    | Some _, _ -> Error "/request requires a non-empty query."
                    | _, Some _ -> Error "/say requires non-empty text."
                    | _ -> Ok None
                | None -> Ok None
        | None -> Ok None

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
                                | Ok mappedEvent ->
                                    match mappedEvent with
                                    | None ->
                                        state.RecordUpdate(update.UpdateId)
                                        context.Response.StatusCode <- StatusCodes.Status204NoContent
                                        return StatusCodes.Status204NoContent
                                    | Some event ->
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
        map admin logger "GET" "/say-messages" "/api/v0/admin/say-messages" adminPlaceholder
        map admin logger "POST" "/say-messages/{messageId}/approve" "/api/v0/admin/say-messages/{messageId}/approve" adminPlaceholder
        map admin logger "POST" "/say-messages/{messageId}/reject" "/api/v0/admin/say-messages/{messageId}/reject" adminPlaceholder
        map admin logger "GET" "/storage" "/api/v0/admin/storage" adminPlaceholder
        map admin logger "PUT" "/storage" "/api/v0/admin/storage" adminPlaceholder
        map admin logger "POST" "/library/scan" "/api/v0/admin/library/scan" adminPlaceholder
        map admin logger "GET" "/stream-node/status" "/api/v0/admin/stream-node/status" adminPlaceholder
        map admin logger "POST" "/stream-node/restart" "/api/v0/admin/stream-node/restart" adminPlaceholder
