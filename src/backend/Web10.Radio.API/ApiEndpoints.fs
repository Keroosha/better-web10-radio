namespace Web10.Radio.API

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.ServerSentEvents
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Primitives
open Npgsql
open Web10.Radio.Database.Repositories
open Web10.Radio.Telegram

[<RequireQualifiedAccess>]
module ApiRouteLog =
    let private completedMessage =
        LoggerMessage.Define<string, int, string, string, double>(
            LogLevel.Information,
            EventId(3000, "ApiRouteCompleted"),
            "API route {Route} completed with {Status} traceId {TraceId} correlationId {CorrelationId} in {ElapsedMs} ms"
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

type private ApiRouteHandler = HttpContext -> Task<int>

type private PlayerEventsEnumerator(dataSource: NpgsqlDataSource, clock: IClock, cancellationToken: CancellationToken) =
    let mutable first = true
    let mutable current = Unchecked.defaultof<SseItem<JsonElement>>

    let moveNextCore () =
        task {
            try
                if cancellationToken.IsCancellationRequested then
                    return false
                else
                    let isFirst = first
                    first <- false

                    if not isFirst then
                        do! Task.Delay(TimeSpan.FromSeconds(5.0), cancellationToken)

                    if cancellationToken.IsCancellationRequested then
                        return false
                    else
                        let! snapshotResult = PlayerStateReadModel.loadSnapshot dataSource clock cancellationToken

                        match snapshotResult with
                        | Error _ -> return false
                        | Ok snapshot ->
                            if isFirst then
                                current <- SseItem<JsonElement>(ApiJson.toElement snapshot, "player.state")
                            else
                                current <- SseItem<JsonElement>(ApiJson.toElement snapshot.Stream, "player.health")

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

type private PlayerEvents(dataSource: NpgsqlDataSource, clock: IClock, requestAborted: CancellationToken) =
    interface IAsyncEnumerable<SseItem<JsonElement>> with
        member _.GetAsyncEnumerator(enumeratorCancellationToken: CancellationToken) =
            let cancellationToken =
                if enumeratorCancellationToken.CanBeCanceled then
                    enumeratorCancellationToken
                else
                    requestAborted

            PlayerEventsEnumerator(dataSource, clock, cancellationToken) :> IAsyncEnumerator<SseItem<JsonElement>>

[<RequireQualifiedAccess>]
module ApiEndpoints =
    let private execute (logger: ILogger) (route: string) (handler: ApiRouteHandler) (context: HttpContext) : Task =
        task {
            let stopwatch = Stopwatch.StartNew()
            let mutable statusCode = StatusCodes.Status500InternalServerError

            try
                let! handledStatusCode = handler context
                statusCode <- handledStatusCode
            with
            | :? OperationCanceledException when context.RequestAborted.IsCancellationRequested ->
                statusCode <- if context.Response.HasStarted then context.Response.StatusCode else 499
            | _ ->
                statusCode <- StatusCodes.Status500InternalServerError

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

    let private map (app: WebApplication) (method: string) (route: string) (handler: ApiRouteHandler) =
        let logger = app.Logger
        app.MapMethods(route, [| method |], RequestDelegate(fun context -> execute logger route handler context)) |> ignore

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
            let events = PlayerEvents(dataSource, clock, context.RequestAborted) :> IAsyncEnumerable<SseItem<JsonElement>>
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
                let! fileResult = PlayerStateReadModel.loadStreamFile dataSource context.RequestAborted

                match fileResult with
                | Error _ ->
                    do! repositoryReadFailed context
                    return StatusCodes.Status500InternalServerError
                | Ok None ->
                    do! streamUnavailable context
                    return StatusCodes.Status503ServiceUnavailable
                | Ok (Some file) when not (File.Exists file.CachePath) ->
                    do! streamUnavailable context
                    return StatusCodes.Status503ServiceUnavailable
                | Ok (Some file) ->
                    let stream = File.OpenRead(file.CachePath)
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

    let private fixedTimeEqualsUtf8 (expected: string) (actual: string) =
        let actual = if isNull actual then String.Empty else actual
        let expectedBytes = Encoding.UTF8.GetBytes(expected)
        let actualBytes = Encoding.UTF8.GetBytes(actual)

        if expectedBytes.Length = actualBytes.Length then
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes)
        else
            let paddedActual = Array.zeroCreate<byte> expectedBytes.Length
            let copyLength = min expectedBytes.Length actualBytes.Length

            if copyLength > 0 then
                Buffer.BlockCopy(actualBytes, 0, paddedActual, 0, copyLength)

            CryptographicOperations.FixedTimeEquals(expectedBytes, paddedActual) |> ignore
            false

    let private webhookSecretHeader (context: HttpContext) =
        let mutable values = StringValues()

        if context.Request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", &values) then
            values.ToString()
        else
            null

    let private tryParseTelegramUpdateId (rawJson: string) =
        try
            use document = JsonDocument.Parse(rawJson)
            let mutable updateId = Unchecked.defaultof<JsonElement>

            if document.RootElement.ValueKind <> JsonValueKind.Object then
                Error "JSON body must be an object."
            elif not (document.RootElement.TryGetProperty("update_id", &updateId)) then
                Error "update_id is required."
            elif updateId.ValueKind <> JsonValueKind.Number then
                Error "update_id must be an integer."
            else
                let mutable parsed = 0L

                if updateId.TryGetInt64(&parsed) then
                    Ok parsed
                else
                    Error "update_id must be an integer."
        with
        | :? JsonException -> Error "JSON body must be valid."

    let private telegramWebhook (context: HttpContext) =
        task {
            let telegramOptions = context.RequestServices.GetRequiredService<TelegramOptions>()
            let suppliedSecret = webhookSecretHeader context

            if not (fixedTimeEqualsUtf8 telegramOptions.WebhookSecret suppliedSecret) then
                do!
                    ApiProblems.write
                        context
                        StatusCodes.Status401Unauthorized
                        "telegram.webhook.secret_invalid"
                        "Telegram webhook unauthorized"
                        "Telegram webhook secret token is invalid."

                return StatusCodes.Status401Unauthorized
            else
                use reader = new StreamReader(context.Request.Body, Encoding.UTF8, false, 4096, false)
                let! rawJson = reader.ReadToEndAsync(context.RequestAborted)

                match tryParseTelegramUpdateId rawJson with
                | Error message ->
                    do! ApiProblems.write context StatusCodes.Status400BadRequest "request.invalid" "Invalid request" message
                    return StatusCodes.Status400BadRequest
                | Ok updateId ->
                    let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                    let idGenerator = context.RequestServices.GetRequiredService<IIdGenerator>()
                    let clock = context.RequestServices.GetRequiredService<IClock>()
                    let! result = TelegramWebhookInbox.tryRecordRaw dataSource idGenerator clock updateId rawJson context.RequestAborted

                    match result with
                    | Ok _ ->
                        context.Response.StatusCode <- StatusCodes.Status204NoContent
                        return StatusCodes.Status204NoContent
                    | Error _ ->
                        do!
                            ApiProblems.write
                                context
                                StatusCodes.Status500InternalServerError
                                "telegram.webhook.record_failed"
                                "Telegram webhook failed"
                                "Telegram webhook update could not be recorded."

                        return StatusCodes.Status500InternalServerError
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
        map app "GET" "/api/v0/player/state" playerState
        map app "GET" "/api/v0/player/events" playerEvents
        map app "GET" "/api/v0/player/stream" playerStream
        map app "GET" "/api/v0/player/song" playerSong
        map app "GET" "/api/v0/player/health" playerHealth

        map app "POST" "/api/v0/telegram/webhook" telegramWebhook
        map app "GET" "/api/v0/telegram/health" telegramHealth

        map app "GET" "/api/v0/admin/social-links" adminSocialLinks
        map app "PUT" "/api/v0/admin/social-links" adminPlaceholder
        map app "GET" "/api/v0/admin/donation-goal" adminDonationGoal
        map app "PUT" "/api/v0/admin/donation-goal" adminPlaceholder
        map app "GET" "/api/v0/admin/playlists" adminPlaceholder
        map app "POST" "/api/v0/admin/playlists" adminPlaceholder
        map app "GET" "/api/v0/admin/playlists/{playlistId}/items" adminPlaceholder
        map app "POST" "/api/v0/admin/playlists/{playlistId}/items" adminPlaceholder
        map app "PUT" "/api/v0/admin/playlists/{playlistId}/items" adminPlaceholder
        map app "GET" "/api/v0/admin/say-messages" adminPlaceholder
        map app "POST" "/api/v0/admin/say-messages/{messageId}/approve" adminPlaceholder
        map app "POST" "/api/v0/admin/say-messages/{messageId}/reject" adminPlaceholder
        map app "GET" "/api/v0/admin/storage" adminPlaceholder
        map app "PUT" "/api/v0/admin/storage" adminPlaceholder
        map app "POST" "/api/v0/admin/library/scan" adminPlaceholder
        map app "GET" "/api/v0/admin/stream-node/status" adminPlaceholder
        map app "POST" "/api/v0/admin/stream-node/restart" adminPlaceholder
