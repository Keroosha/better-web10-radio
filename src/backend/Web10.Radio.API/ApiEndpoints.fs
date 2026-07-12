namespace Web10.Radio.API

open System
open System.Globalization
open Dodo.Primitives
open Web10.Radio.Application
open System.Buffers
open System.Security.Claims
open System.Text.Encodings.Web
open System.Collections.Generic
open System.Diagnostics
open FsToolkit.ErrorHandling
open System.IO
open System.Net.ServerSentEvents
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
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

type private PlayerEventsEnumerator(dataSource: NpgsqlDataSource, clock: TimeProvider, delay: IPlayerEventsDelay, cancellationToken: CancellationToken) =
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
                    | 4 ->
                        current <- SseItem<JsonElement>(ApiJson.toElement snapshot.Stream, "player.health")
                        eventIndex <- 5
                    | _ ->
                        current <- SseItem<JsonElement>(ApiJson.toElement snapshot, "player.state")
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

type private PlayerEvents(dataSource: NpgsqlDataSource, clock: TimeProvider, delay: IPlayerEventsDelay, requestAborted: CancellationToken) =
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
            let clock = context.RequestServices.GetRequiredService<TimeProvider>()
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
            let clock = context.RequestServices.GetRequiredService<TimeProvider>()
            let delay = context.RequestServices.GetRequiredService<IPlayerEventsDelay>()
            let events = PlayerEvents(dataSource, clock, delay, context.RequestAborted) :> IAsyncEnumerable<SseItem<JsonElement>>
            let result = TypedResults.ServerSentEvents<JsonElement>(events) :> IResult
            do! result.ExecuteAsync(context)
            return StatusCodes.Status200OK
        }

    let private playerStream (context: HttpContext) =
        task {
            let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
            let clock = context.RequestServices.GetRequiredService<TimeProvider>()
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
            let clock = context.RequestServices.GetRequiredService<TimeProvider>()
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
            let clock = context.RequestServices.GetRequiredService<TimeProvider>()
            let! result = PlayerStateReadModel.loadStreamHealth dataSource clock context.RequestAborted

            match result with
            | Ok health ->
                do! writeOk context { health with TraceId = ApiTrace.traceId context }
                return StatusCodes.Status200OK
            | Error _ ->
                do! repositoryReadFailed context
                return StatusCodes.Status500InternalServerError
        }


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

    type private CoverBody =
        | CoverTooLarge
        | CoverInvalid
        | CoverParsed of byte array * string * string * string

    let private readCoverBody (context: HttpContext) =
        task {
            let contentType = context.Request.ContentType
            if isNull contentType || (contentType <> "image/jpeg" && contentType <> "image/png" && contentType <> "image/webp") then
                return CoverInvalid
            elif context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > 10L * 1024L * 1024L then
                return CoverTooLarge
            else
                let buffer = ArrayPool<byte>.Shared.Rent(10 * 1024 * 1024 + 1)
                use _lease = { new IDisposable with member _.Dispose() = ArrayPool<byte>.Shared.Return(buffer, clearArray = true) }
                let! length = readBoundedBody (10 * 1024 * 1024) context buffer
                match length with
                | None -> return CoverTooLarge
                | Some bodyLength when bodyLength > 0 ->
                    let bytes = buffer.AsSpan(0, bodyLength).ToArray()
                    let detected =
                        if bytes.Length >= 3 && bytes[0] = 0xFFuy && bytes[1] = 0xD8uy && bytes[2] = 0xFFuy then Some("image/jpeg", ".jpg")
                        elif bytes.Length >= 8 && bytes[0] = 0x89uy && bytes[1] = 0x50uy && bytes[2] = 0x4Euy && bytes[3] = 0x47uy && bytes[4] = 0x0Duy && bytes[5] = 0x0Auy && bytes[6] = 0x1Auy && bytes[7] = 0x0Auy then Some("image/png", ".png")
                        elif bytes.Length >= 12 && bytes[0] = 0x52uy && bytes[1] = 0x49uy && bytes[2] = 0x46uy && bytes[3] = 0x46uy && bytes[8] = 0x57uy && bytes[9] = 0x45uy && bytes[10] = 0x42uy && bytes[11] = 0x50uy then Some("image/webp", ".webp")
                        else None
                    match detected with
                    | Some(detectedType, extension) when detectedType = contentType ->
                        let sha = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
                        return CoverParsed(bytes, detectedType, extension, sha)
                    | _ -> return CoverInvalid
                | _ -> return CoverInvalid
        }
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
          HasCachedFile = track.HasCachedFile
          CoverImageUrl = track.CoverImageUrl
          MetadataSource =
            match track.MetadataSource with
            | "Embedded" -> "embedded"
            | "Manual" -> "manual"
            | _ -> "filename" }

    let private playlistTypeDto = function
        | PlaylistType.General -> "general"
        | PlaylistType.OncePerSongs -> "oncePerSongs"
        | PlaylistType.OncePerMinutes -> "oncePerMinutes"
        | PlaylistType.OncePerHour -> "oncePerHour"

    let private playlistSourceDto = function
        | PlaylistSource.Manual -> "manual"
        | PlaylistSource.AllStorage -> "allStorage"

    let private playlistOrderDto = function
        | PlaylistOrder.Sequential -> "sequential"
        | PlaylistOrder.Shuffle -> "shuffle"
        | PlaylistOrder.Random -> "random"

    let private toPlaylistScheduleDto (schedule: PlaylistSchedule) : PlaylistScheduleDto =
        { Id = schedule.Id |> Option.map (fun value -> value.ToString("D")) |> Option.defaultValue null
          DaysOfWeek = schedule.DaysOfWeek
          StartTime = schedule.StartTime.ToString("hh\\:mm", CultureInfo.InvariantCulture)
          EndTime = schedule.EndTime.ToString("hh\\:mm", CultureInfo.InvariantCulture)
          StartDate = schedule.StartDate |> Option.map (fun value -> value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) |> Option.defaultValue null
          EndDate = schedule.EndDate |> Option.map (fun value -> value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) |> Option.defaultValue null
          TimeZoneId = schedule.TimeZoneId }

    let private toPlaylistDto (playlist: PlaylistSummary) : PlaylistSummaryDto =
        { Id = playlist.Id.ToString("D")
          Name = playlist.Name
          Description = playlist.Description |> Option.defaultValue null
          IsActive = playlist.IsActive
          Type = playlist.Type |> playlistTypeDto
          Source = playlist.Source |> playlistSourceDto
          Order = playlist.Order |> playlistOrderDto
          Weight = playlist.Weight
          IsJingle = playlist.IsJingle
          Interrupt = playlist.Interrupt
          AvoidDuplicates = playlist.AvoidDuplicates
          PlayEverySongs = playlist.PlayEverySongs
          PlayEveryMinutes = playlist.PlayEveryMinutes
          PlayAtMinute = playlist.PlayAtMinute
          IsSystem = playlist.IsSystem
          ItemCount = max 0 playlist.ItemCount
          Schedules = playlist.Schedules |> List.map toPlaylistScheduleDto }

    let private toPlaylistItemDto (item: PlaylistItem) : PlaylistItemDto =
        { Id = item.Id.ToString("D")
          TrackId = item.TrackId.ToString("D")
          Title = item.Title
          Artist = item.Artist
          Position = max 0 item.Position }

    let private toControlDto (state: StreamNodeControlState) (commands: StreamNodePlaybackCommandDto list) (nextPlaybackGeneration: int64) : StreamNodeControlDto =
        { DesiredState = match state.DesiredState with | Running -> "running" | Stopped -> "stopped"
          RestartGeneration = max 0 state.RestartGeneration
          PlaybackCommands = commands
          NextPlaybackGeneration = nextPlaybackGeneration }

    let private isExactEmptyJsonObject (root: JsonElement) =
        root.ValueKind = JsonValueKind.Object
        && (root.EnumerateObject() |> Seq.isEmpty)
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
                        Ok(claimOwner, claimAttempt, Some PlaybackCompletion.Succeeded)
                    | "failed" ->
                        if root.TryGetProperty("failureReason", &reasonElement)
                           && reasonElement.ValueKind = JsonValueKind.String
                           && not (String.IsNullOrWhiteSpace(reasonElement.GetString())) then
                            Ok(
                                claimOwner,
                                claimAttempt,
                                Some(PlaybackCompletion.Failed(reasonElement.GetString().Trim()))
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
            let clock = context.RequestServices.GetRequiredService<TimeProvider>()
            match DomainEventEnvelope.create clock eventType producer None None payload with
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
                    let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                    let! result = LibraryScanRepository.createOrGetActiveJob source (Uuid.CreateVersion7().ToGuidBigEndian()) storageBackendId (clock.GetUtcNow()) context.RequestAborted
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
                        let payload = JsonSerializer.Serialize({| libraryScanJobId = job.Id.ToString("D") |}, DomainJson.options)
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
            let cursor = if context.Request.Query.ContainsKey("cursor") then context.Request.Query["cursor"].ToString() else null
            let mutable limit = 0

            let validCursor =
                if isNull cursor then
                    true
                elif cursor.Length > 512 || cursor.Length = 0 then
                    false
                else
                    try
                        let normalized = cursor.Replace('-', '+').Replace('_', '/')
                        let padded = normalized + String('=', (4 - normalized.Length % 4) % 4)
                        use document = JsonDocument.Parse(Convert.FromBase64String padded)
                        let root = document.RootElement
                        let mutable created = Unchecked.defaultof<JsonElement>
                        let mutable id = Unchecked.defaultof<JsonElement>
                        let mutable parsedId = Guid.Empty
                        let mutable parsedCreated = DateTimeOffset.MinValue
                        root.ValueKind = JsonValueKind.Object
                        && root.TryGetProperty("createdAtUtc", &created)
                        && root.TryGetProperty("id", &id)
                        && created.ValueKind = JsonValueKind.String
                        && id.ValueKind = JsonValueKind.String
                        && DateTimeOffset.TryParse(created.GetString(), &parsedCreated)
                        && Guid.TryParse(id.GetString(), &parsedId)
                    with _ -> false

            if query.Length > 200 || not (Int32.TryParse(limitText, &limit)) || limit < 1 || limit > 100 || not validCursor then
                do! writeDomainProblem context StatusCodes.Status400BadRequest "track.request_invalid" "Invalid track request" "The track query, limit, or cursor is invalid."
                return StatusCodes.Status400BadRequest
            else
                let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let cursorOption = if isNull cursor then None else Some cursor
                let! result = AdminContentRepository.listActiveTracksPage source query limit cursorOption context.RequestAborted
                match result with
                | Error _ ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "track.request_invalid" "Invalid track request" "The track query, limit, or cursor is invalid."
                    return StatusCodes.Status400BadRequest
                | Ok page ->
                    let dto : AdminTrackPageDto = { Items = page.Items |> List.map toTrackDto; NextCursor = page.NextCursor |> Option.defaultValue null }
                    do! writeOk context dto
                    return StatusCodes.Status200OK
        }

    let private appendTrackMetadataChanged (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (clock: TimeProvider) (trackId: Guid) (changedFields: string list) cancellationToken : Task<Result<unit, RepositoryError>> =
        task {
            let payload = JsonSerializer.Serialize({| trackId = trackId.ToString("D"); changedFields = changedFields |}, DomainJson.options)
            match DomainEventEnvelope.create clock DomainEventType.TrackMetadataChanged "Web10.Radio.API.Admin" None None payload with
            | Error error -> return Error(DatabaseError("TrackMetadataChanged", error.ToString()))
            | Ok envelope -> return! OutboxEventRepository.appendInTransaction connection transaction (OutboxMapping.toOutboxEvent envelope) cancellationToken |> TaskResult.mapError id
        }

    let private adminUpdateTrackMetadata (context: HttpContext) =
        task {
            match parseGuidRoute "trackId" context with
            | None ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "track.request_invalid" "Invalid track request" "The track identifier is invalid."
                return StatusCodes.Status400BadRequest
            | Some trackId ->
                match! readJsonBody (16 * 1024) context with
                | BodyTooLarge ->
                    do! writeRequestTooLarge context
                    return StatusCodes.Status413PayloadTooLarge
                | BodyParsed root when hasExactProperties (Set.ofList [ "title"; "artist"; "album" ]) root ->
                    let title = tryString "title" root |> Option.map (fun value -> value.Trim())
                    let artist = tryString "artist" root |> Option.map (fun value -> value.Trim())
                    let album = tryNullableString "album" root |> Option.map (Option.map (fun value -> value.Trim()))
                    if title |> Option.exists (fun value -> value.Length < 1 || value.Length > 200) |> not
                       || artist |> Option.exists (fun value -> value.Length < 1 || value.Length > 200) |> not
                       || album.IsNone then
                        do! writeDomainProblem context StatusCodes.Status400BadRequest "track.request_invalid" "Invalid track request" "Title and artist must be 1 to 200 characters and album must be null or 1 to 200 characters."
                        return StatusCodes.Status400BadRequest
                    else
                        let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                        let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                        let! result =
                            DatabaseSession.withTransactionResult source (fun connection transaction token ->
                                taskResult {
                                    let! mutation = AdminContentRepository.updateTrackMetadataInTransaction connection transaction trackId title.Value artist.Value album.Value (clock.GetUtcNow()) token
                                    match mutation with
                                    | AdminContentMutation.Applied track ->
                                        do! appendTrackMetadataChanged connection transaction clock trackId [ "title"; "artist"; "album" ] token
                                        return AdminContentMutation.Applied track
                                    | AdminContentMutation.NotFound -> return AdminContentMutation.NotFound
                                    | AdminContentMutation.Conflict -> return AdminContentMutation.Conflict
                                }) context.RequestAborted
                        match result with
                        | Error _ ->
                            do! writeRepositoryFailure context
                            return StatusCodes.Status500InternalServerError
                        | Ok AdminContentMutation.NotFound ->
                            do! writeDomainProblem context StatusCodes.Status404NotFound "track.not_found" "Track not found" "The requested track does not exist."
                            return StatusCodes.Status404NotFound
                        | Ok AdminContentMutation.Conflict ->
                            do! writeDomainProblem context StatusCodes.Status409Conflict "track.conflict" "Track conflict" "The track could not be updated."
                            return StatusCodes.Status409Conflict
                        | Ok(AdminContentMutation.Applied track) ->
                            do! writeOk context (toTrackDto track)
                            return StatusCodes.Status200OK
                | _ ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "track.request_invalid" "Invalid track request" "The metadata body is invalid."
                    return StatusCodes.Status400BadRequest
        }

    let private writeManagedCover (cacheRoot: string) (trackId: Guid) (bytes: byte array) (sha256: string) (extension: string) =
        let directory = Path.Combine(cacheRoot, "covers", trackId.ToString("D"))
        let temporaryDirectory = Path.Combine(cacheRoot, "tmp")
        Directory.CreateDirectory(directory) |> ignore
        Directory.CreateDirectory(temporaryDirectory) |> ignore
        let finalPath = Path.Combine(directory, sha256 + extension)
        let existed = File.Exists finalPath
        let temporary = Path.Combine(temporaryDirectory, sprintf "%s.tmp" (Uuid.CreateVersion7().ToGuidBigEndian().ToString("N")))
        File.WriteAllBytes(temporary, bytes)
        File.Move(temporary, finalPath, true)
        finalPath, existed

    let private deleteManagedCoverFile (path: string) (existedBeforeWrite: bool) =
        if not existedBeforeWrite then
            try File.Delete(path) with _ -> ()

    let private cleanupManagedCovers (cacheRoot: string) (trackId: Guid) (keepPath: string) =
        let directory = Path.Combine(cacheRoot, "covers", trackId.ToString("D"))
        if Directory.Exists directory then
            for path in Directory.EnumerateFiles(directory) do
                if not (String.Equals(Path.GetFullPath(path), Path.GetFullPath(keepPath), StringComparison.Ordinal)) then
                    try File.Delete(path) with _ -> ()


    let private adminReplaceTrackCover (context: HttpContext) =
        task {
            match parseGuidRoute "trackId" context with
            | None ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "track.request_invalid" "Invalid track request" "The track identifier is invalid."
                return StatusCodes.Status400BadRequest
            | Some trackId ->
                match! readCoverBody context with
                | CoverTooLarge ->
                    do! writeDomainProblem context StatusCodes.Status413PayloadTooLarge "track.cover_invalid" "Cover too large" "The cover exceeds 10 MiB."
                    return StatusCodes.Status413PayloadTooLarge
                | CoverInvalid ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "track.cover_invalid" "Invalid cover" "The cover MIME type or magic bytes are invalid."
                    return StatusCodes.Status400BadRequest
                | CoverParsed(bytes, contentType, extension, sha256) ->
                    let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                    let options = context.RequestServices.GetRequiredService<StorageOptions>()
                    let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                    let finalPath =
                        try Some(writeManagedCover options.CacheRoot trackId bytes sha256 extension)
                        with _ -> None
                    match finalPath with
                    | None ->
                        do! writeDomainProblem context StatusCodes.Status503ServiceUnavailable "track.cover_unavailable" "Cover unavailable" "The cover could not be stored."
                        return StatusCodes.Status503ServiceUnavailable
                    | Some(cachePath, existedBeforeWrite) ->
                        let! result =
                            DatabaseSession.withTransactionResult source (fun connection transaction token ->
                                taskResult {
                                    let! mutation = AdminContentRepository.replaceTrackCoverInTransaction connection transaction trackId cachePath contentType (int64 bytes.LongLength) sha256 (clock.GetUtcNow()) token
                                    match mutation with
                                    | AdminContentMutation.Applied track ->
                                        do! appendTrackMetadataChanged connection transaction clock trackId [ "cover" ] token
                                        return AdminContentMutation.Applied track
                                    | AdminContentMutation.NotFound -> return AdminContentMutation.NotFound
                                    | AdminContentMutation.Conflict -> return AdminContentMutation.Conflict
                                }) context.RequestAborted
                        match result with
                        | Error _ ->
                            deleteManagedCoverFile cachePath existedBeforeWrite
                            do! writeDomainProblem context StatusCodes.Status503ServiceUnavailable "track.cover_unavailable" "Cover unavailable" "The cover could not be persisted."
                            return StatusCodes.Status503ServiceUnavailable
                        | Ok AdminContentMutation.NotFound ->
                            deleteManagedCoverFile cachePath existedBeforeWrite
                            do! writeDomainProblem context StatusCodes.Status404NotFound "track.not_found" "Track not found" "The requested track does not exist."
                            return StatusCodes.Status404NotFound
                        | Ok AdminContentMutation.Conflict ->
                            deleteManagedCoverFile cachePath existedBeforeWrite
                            do! writeDomainProblem context StatusCodes.Status409Conflict "track.conflict" "Track conflict" "The cover could not be updated."
                            return StatusCodes.Status409Conflict
                        | Ok(AdminContentMutation.Applied track) ->
                            cleanupManagedCovers options.CacheRoot trackId cachePath
                            do! writeOk context (toTrackDto track)
                            return StatusCodes.Status200OK
        }

    let private adminRemoveTrackCover (context: HttpContext) =
        task {
            match parseGuidRoute "trackId" context with
            | None ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "track.request_invalid" "Invalid track request" "The track identifier is invalid."
                return StatusCodes.Status400BadRequest
            | Some trackId ->
                let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                let options = context.RequestServices.GetRequiredService<StorageOptions>()
                let! result =
                    DatabaseSession.withTransactionResult source (fun connection transaction token ->
                        taskResult {
                            let! mutation = AdminContentRepository.removeTrackCoverInTransaction connection transaction trackId (clock.GetUtcNow()) token
                            match mutation with
                            | AdminContentMutation.Applied track ->
                                do! appendTrackMetadataChanged connection transaction clock trackId [ "cover" ] token
                                return AdminContentMutation.Applied track
                            | AdminContentMutation.NotFound -> return AdminContentMutation.NotFound
                            | AdminContentMutation.Conflict -> return AdminContentMutation.Conflict
                        }) context.RequestAborted
                match result with
                | Error _ ->
                    do! writeRepositoryFailure context
                    return StatusCodes.Status500InternalServerError
                | Ok AdminContentMutation.NotFound ->
                    do! writeDomainProblem context StatusCodes.Status404NotFound "track.not_found" "Track not found" "The requested track does not exist."
                    return StatusCodes.Status404NotFound
                | Ok AdminContentMutation.Conflict ->
                    do! writeDomainProblem context StatusCodes.Status409Conflict "track.conflict" "Track conflict" "The cover could not be removed."
                    return StatusCodes.Status409Conflict
                | Ok(AdminContentMutation.Applied track) ->
                    cleanupManagedCovers options.CacheRoot trackId String.Empty
                    do! writeOk context (toTrackDto track)
                    return StatusCodes.Status200OK
        }
    let private playerCover (context: HttpContext) =
        task {
            match parseGuidRoute "trackId" context with
            | None ->
                do! writeDomainProblem context StatusCodes.Status404NotFound "track.cover_not_found" "Cover not found" "The requested cover does not exist."
                return StatusCodes.Status404NotFound
            | Some trackId ->
                let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let options = context.RequestServices.GetRequiredService<StorageOptions>()
                let! result = TrackAssetReadModel.tryGetManagedCover source trackId context.RequestAborted
                match result with
                | Error _ ->
                    do! ApiProblems.write context StatusCodes.Status503ServiceUnavailable "track.cover_unavailable" "Cover unavailable" "The cover could not be read."
                    return StatusCodes.Status503ServiceUnavailable
                | Ok None ->
                    do! ApiProblems.write context StatusCodes.Status404NotFound "track.cover_not_found" "Cover not found" "The requested cover does not exist."
                    return StatusCodes.Status404NotFound
                | Ok(Some cover) ->
                    match TrackAssetReadModel.tryCanonicalCachePath options.CacheRoot cover.CachePath with
                    | None ->
                        do! ApiProblems.write context StatusCodes.Status503ServiceUnavailable "track.cover_unavailable" "Cover unavailable" "The cover path is invalid."
                        return StatusCodes.Status503ServiceUnavailable
                    | Some path ->
                        try
                            use stream = File.OpenRead(path)
                            let etag = sprintf "\"%s\"" cover.Sha256
                            context.Response.Headers.ETag <- etag
                            context.Response.Headers.CacheControl <- "public,max-age=3600"
                            context.Response.ContentType <- cover.ContentType
                            let mutable ifNoneMatch = StringValues()
                            let matched = context.Request.Headers.TryGetValue("If-None-Match", &ifNoneMatch) && (ifNoneMatch |> Seq.exists (fun value -> value.Split(',') |> Array.exists (fun candidate -> candidate.Trim() = "*" || candidate.Trim() = etag)))
                            if matched then
                                context.Response.StatusCode <- StatusCodes.Status304NotModified
                                return StatusCodes.Status304NotModified
                            else
                                do! stream.CopyToAsync(context.Response.Body, context.RequestAborted)
                                return StatusCodes.Status200OK
                        with
                        | :? FileNotFoundException
                        | :? DirectoryNotFoundException ->
                            do! ApiProblems.write context StatusCodes.Status404NotFound "track.cover_not_found" "Cover not found" "The requested cover does not exist."
                            return StatusCodes.Status404NotFound
                        | :? UnauthorizedAccessException
                        | :? IOException ->
                            do! ApiProblems.write context StatusCodes.Status503ServiceUnavailable "track.cover_unavailable" "Cover unavailable" "The cover could not be read."
                            return StatusCodes.Status503ServiceUnavailable
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
                    let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                    let queueItemId = Uuid.CreateVersion7().ToGuidBigEndian()
                    let requestedAtUtc = clock.GetUtcNow()
                    let payload = JsonSerializer.Serialize({| queueItemId = queueItemId.ToString("D"); trackId = trackId.ToString("D") |}, DomainJson.options)
                    match DomainEventEnvelope.create clock DomainEventType.AdminTrackQueued "Web10.Radio.API.Admin" None None payload with
                    | Error _ ->
                        do! writeRepositoryFailure context
                        return StatusCodes.Status500InternalServerError
                    | Ok envelope ->
                        let! result =
                            AdminContentRepository.enqueueAdminTrackWithEvent
                                source
                                { Id = queueItemId; TrackId = trackId; RequestedAtUtc = requestedAtUtc }
                                (OutboxMapping.toOutboxEvent envelope)
                                context.RequestAborted
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
                            do! ApiJson.write context StatusCodes.Status202Accepted ApiJson.JsonContentType ({ QueueItemId = item.Id.ToString("D") } : PlaybackQueueAcceptedDto)
                            return StatusCodes.Status202Accepted
            | _ ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "playback.request_invalid" "Invalid playback request" "The playback request body is invalid."
                return StatusCodes.Status400BadRequest
        }
    let private adminReorderQueue (context: HttpContext) =
        task {
            match! readJsonBody (64 * 1024) context with
            | BodyParsed root when hasExactProperties (Set.ofList [ "queueItemIds" ]) root ->
                match tryProperty "queueItemIds" root with
                | Some values when values.ValueKind = JsonValueKind.Array ->
                    let parsed =
                        values.EnumerateArray()
                        |> Seq.map (fun value -> if value.ValueKind = JsonValueKind.String then tryPositiveGuid (value.GetString()) else None)
                        |> Seq.toList
                    if parsed |> List.exists Option.isNone then
                        do! writeDomainProblem context StatusCodes.Status400BadRequest "playback.request_invalid" "Invalid playback request" "Every queue item identifier must be a UUID."
                        return StatusCodes.Status400BadRequest
                    else
                        let queueItemIds = parsed |> List.choose id
                        let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                        let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                        let! result = PlaybackQueueRepository.reorderQueued source queueItemIds (clock.GetUtcNow()) context.RequestAborted
                        match result with
                        | Error _ ->
                            do! writeRepositoryFailure context
                            return StatusCodes.Status500InternalServerError
                        | Ok PlaybackControlOutcome.NotFound ->
                            do! writeDomainProblem context StatusCodes.Status409Conflict "playback.conflict" "Playback conflict" "The queue changed while it was being reordered."
                            return StatusCodes.Status409Conflict
                        | Ok PlaybackControlOutcome.Conflict ->
                            do! writeDomainProblem context StatusCodes.Status409Conflict "playback.conflict" "Playback conflict" "The queue changed while it was being reordered."
                            return StatusCodes.Status409Conflict
                        | Ok(PlaybackControlOutcome.Applied _) ->
                            let payload = JsonSerializer.Serialize({| queueItemIds = queueItemIds |> List.map (fun id -> id.ToString("D")) |}, DomainJson.options)
                            let! published = publishEvent context DomainEventType.PlaybackReordered "Web10.Radio.API.Admin" payload
                            if not published then
                                do! writeRepositoryFailure context
                                return StatusCodes.Status500InternalServerError
                            else
                                do! writeOk context {| queueItemIds = queueItemIds |> List.map (fun id -> id.ToString("D")) |}
                                return StatusCodes.Status200OK
                | _ ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "playback.request_invalid" "Invalid playback request" "queueItemIds must be an array."
                    return StatusCodes.Status400BadRequest
            | BodyTooLarge ->
                do! writeRequestTooLarge context
                return StatusCodes.Status413PayloadTooLarge
            | _ ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "playback.request_invalid" "Invalid playback request" "The queue reorder request body is invalid."
                return StatusCodes.Status400BadRequest
        }

    let private adminSkipCurrent (context: HttpContext) =
        task {
            match! readJsonBody (16 * 1024) context with
            | BodyParsed root when isExactEmptyJsonObject root ->
                let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                let! result = PlaybackQueueRepository.skipCurrent source (clock.GetUtcNow()) context.RequestAborted
                match result with
                | Error _ ->
                    do! writeRepositoryFailure context
                    return StatusCodes.Status500InternalServerError
                | Ok PlaybackControlOutcome.Conflict
                | Ok PlaybackControlOutcome.NotFound ->
                    do! writeDomainProblem context StatusCodes.Status409Conflict "playback.conflict" "Playback conflict" "There is no current track to skip."
                    return StatusCodes.Status409Conflict
                | Ok(PlaybackControlOutcome.Applied command) ->
                    let payload = JsonSerializer.Serialize({| queueItemId = command.Fence.QueueItemId.ToString("D"); claimOwner = command.Fence.ClaimOwner.ToString("D"); claimAttempt = command.Fence.ClaimAttempt; commandGeneration = command.Generation |}, DomainJson.options)
                    let! published = publishEvent context DomainEventType.PlaybackSkipped "Web10.Radio.API.Admin" payload
                    if not published then
                        do! writeRepositoryFailure context
                        return StatusCodes.Status500InternalServerError
                    else
                        do! ApiJson.write context StatusCodes.Status202Accepted ApiJson.JsonContentType {| generation = command.Generation |}
                        return StatusCodes.Status202Accepted
            | BodyTooLarge ->
                do! writeRequestTooLarge context
                return StatusCodes.Status413PayloadTooLarge
            | _ ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "playback.request_invalid" "Invalid playback request" "The request body must be an empty object."
                return StatusCodes.Status400BadRequest
        }

    let private adminRestartCurrent (context: HttpContext) =
        task {
            match! readJsonBody (16 * 1024) context with
            | BodyParsed root when isExactEmptyJsonObject root ->
                let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                let! result = PlaybackQueueRepository.restartCurrent source (clock.GetUtcNow()) context.RequestAborted
                match result with
                | Error _ ->
                    do! writeRepositoryFailure context
                    return StatusCodes.Status500InternalServerError
                | Ok PlaybackControlOutcome.Conflict
                | Ok PlaybackControlOutcome.NotFound ->
                    do! writeDomainProblem context StatusCodes.Status409Conflict "playback.conflict" "Playback conflict" "There is no currently playing track to restart."
                    return StatusCodes.Status409Conflict
                | Ok(PlaybackControlOutcome.Applied command) ->
                    let payload = JsonSerializer.Serialize({| queueItemId = command.Fence.QueueItemId.ToString("D"); claimOwner = command.Fence.ClaimOwner.ToString("D"); claimAttempt = command.Fence.ClaimAttempt; commandGeneration = command.Generation |}, DomainJson.options)
                    let! published = publishEvent context DomainEventType.PlaybackRestarted "Web10.Radio.API.Admin" payload
                    if not published then
                        do! writeRepositoryFailure context
                        return StatusCodes.Status500InternalServerError
                    else
                        do! ApiJson.write context StatusCodes.Status202Accepted ApiJson.JsonContentType {| generation = command.Generation |}
                        return StatusCodes.Status202Accepted
            | BodyTooLarge ->
                do! writeRequestTooLarge context
                return StatusCodes.Status413PayloadTooLarge
            | _ ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "playback.request_invalid" "Invalid playback request" "The request body must be an empty object."
                return StatusCodes.Status400BadRequest
        }

    let private adminPlayNow (context: HttpContext) =
        task {
            match! readJsonBody (16 * 1024) context with
            | BodyParsed root when hasExactProperties (Set.ofList [ "trackId" ]) root ->
                match tryString "trackId" root |> Option.bind tryPositiveGuid with
                | None ->
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "playback.request_invalid" "Invalid playback request" "The track identifier is invalid."
                    return StatusCodes.Status400BadRequest
                | Some trackId ->
                    let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                    let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                    let queueItemId = Uuid.CreateVersion7().ToGuidBigEndian()
                    let! result = PlaybackQueueRepository.forcePlayNow source queueItemId trackId (clock.GetUtcNow()) context.RequestAborted
                    match result with
                    | Error _ ->
                        do! writeRepositoryFailure context
                        return StatusCodes.Status500InternalServerError
                    | Ok PlaybackControlOutcome.NotFound ->
                        do! writeDomainProblem context StatusCodes.Status404NotFound "playback.not_found" "Track not found" "The requested track is not playable."
                        return StatusCodes.Status404NotFound
                    | Ok PlaybackControlOutcome.Conflict ->
                        do! writeDomainProblem context StatusCodes.Status409Conflict "playback.conflict" "Playback conflict" "The queue changed while the track was being forced."
                        return StatusCodes.Status409Conflict
                    | Ok(PlaybackControlOutcome.Applied applied) ->
                        let payload =
                            JsonSerializer.Serialize(
                                {| queueItemId = applied.QueueItemId.ToString("D")
                                   trackId = applied.TrackId.ToString("D")
                                   interruptedQueueItemId = applied.Interrupted |> Option.map (fun fence -> fence.QueueItemId.ToString("D")) |},
                                DomainJson.options
                            )
                        let! published = publishEvent context DomainEventType.TrackForcePlayed "Web10.Radio.API.Admin" payload
                        if not published then
                            do! writeRepositoryFailure context
                            return StatusCodes.Status500InternalServerError
                        else
                            do! ApiJson.write context StatusCodes.Status202Accepted ApiJson.JsonContentType ({ QueueItemId = applied.QueueItemId.ToString("D") } : PlaybackQueueAcceptedDto)
                            return StatusCodes.Status202Accepted
            | BodyTooLarge ->
                do! writeRequestTooLarge context
                return StatusCodes.Status413PayloadTooLarge
            | _ ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "playback.request_invalid" "Invalid playback request" "The play-now request body is invalid."
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
                        let payload = JsonSerializer.Serialize({| status = persistedStatus; failureReason = failureReason |> Option.defaultValue null; metadata = metadata |}, DomainJson.options)
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
            let clock = context.RequestServices.GetRequiredService<TimeProvider>()
            let parseNonNegative name defaultValue maxValue =
                match context.Request.Query.TryGetValue(name) with
                | false, _ -> Ok defaultValue
                | true, values when values.Count = 1 ->
                    let mutable parsed = 0L
                    if Int64.TryParse(values[0], &parsed) && parsed >= 0L && parsed <= maxValue then Ok parsed
                    else Error ()
                | _ -> Error ()
            let parseLimit () =
                match context.Request.Query.TryGetValue("limit") with
                | false, _ -> Ok 100
                | true, values when values.Count = 1 ->
                    let mutable parsed = 0
                    if Int32.TryParse(values[0], &parsed) && parsed >= 1 && parsed <= 100 then Ok parsed else Error ()
                | _ -> Error ()
            match parseNonNegative "afterPlaybackGeneration" 0L Int64.MaxValue, parseLimit () with
            | Error (), _
            | _, Error () ->
                do! writeDomainProblem context StatusCodes.Status400BadRequest "stream-node.control.request_invalid" "Invalid stream-node control request" "afterPlaybackGeneration must be a non-negative integer and limit must be between 1 and 100."
                return StatusCodes.Status400BadRequest
            | Ok afterGeneration, Ok limit ->
                let! result = StreamNodeControlRepository.getOrCreate source (Uuid.CreateVersion7().ToGuidBigEndian()) (clock.GetUtcNow()) context.RequestAborted
                let! commands = StreamNodeControlRepository.getPlaybackCommands source afterGeneration limit context.RequestAborted
                match result, commands with
                | Error _, _
                | _, Error _ ->
                    do! repositoryReadFailed context
                    return StatusCodes.Status500InternalServerError
                | Ok state, Ok(commandRows, nextGeneration) ->
                    let dtos : StreamNodePlaybackCommandDto list =
                        commandRows
                        |> List.map (fun command ->
                            { Generation = command.Generation
                              Action = command.Action
                              QueueItemId = command.QueueItemId.ToString("D")
                              ClaimOwner = command.ClaimOwner.ToString("D")
                              ClaimAttempt = command.ClaimAttempt })
                    do! writeOk context (toControlDto state dtos nextGeneration)
                    return StatusCodes.Status200OK
        }

    let private adminStreamControl operation (context: HttpContext) =
        task {
            match! readJsonBody (16 * 1024) context with
            | BodyParsed root when isExactEmptyJsonObject root ->
                let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                let! initialized = StreamNodeControlRepository.getOrCreate source (Uuid.CreateVersion7().ToGuidBigEndian()) (clock.GetUtcNow()) context.RequestAborted
                let! result =
                    match initialized, operation with
                    | Error error, _ -> Task.FromResult(Error error)
                    | Ok _, "start" -> StreamNodeControlRepository.setDesiredState source Running (clock.GetUtcNow()) context.RequestAborted
                    | Ok _, "stop" -> StreamNodeControlRepository.setDesiredState source Stopped (clock.GetUtcNow()) context.RequestAborted
                    | Ok _, _ -> StreamNodeControlRepository.restart source (clock.GetUtcNow()) context.RequestAborted
                match result with
                | Error _ ->
                    do! writeRepositoryFailure context
                    return StatusCodes.Status500InternalServerError
                | Ok state ->
                    do! ApiJson.write context StatusCodes.Status202Accepted ApiJson.JsonContentType (toControlDto state [] 0L)
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
            let clock = context.RequestServices.GetRequiredService<TimeProvider>()
            let! control = StreamNodeControlRepository.getOrCreate source (Uuid.CreateVersion7().ToGuidBigEndian()) (clock.GetUtcNow()) context.RequestAborted
            let! snapshot = PlayerStateReadModel.loadSnapshot source clock context.RequestAborted
            match control, snapshot with
            | Ok state, Ok player ->
                let fresh = player.Stream.Status <> "offline"
                let controlDto = toControlDto state [] 0L
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
                        let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                        let! result = AdminContentRepository.upsertDonationGoal source { Id = Uuid.CreateVersion7().ToGuidBigEndian(); Title = trimmed; GoalStars = stars; UpdatedAtUtc = (clock.GetUtcNow()) } context.RequestAborted
                        match result with
                        | Error _ ->
                            do! writeRepositoryFailure context
                            return StatusCodes.Status500InternalServerError
                        | Ok _ ->
                            let payload = JsonSerializer.Serialize({| title = trimmed; goalStars = stars |}, DomainJson.options)
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

    let private parseSocialReplacement (element: JsonElement) =
        if not (hasExactProperties (Set.ofList [ "id"; "kind"; "name"; "handle"; "url"; "glyph"; "color"; "qrImageUrl"; "isFeatured" ]) element) then None
        else
            match tryProperty "id" element, tryString "kind" element, tryString "name" element, tryNullableString "handle" element, tryString "url" element, tryNullableString "glyph" element, tryNullableString "color" element, tryNullableString "qrImageUrl" element, tryProperty "isFeatured" element with
            | Some idElement, Some kind, Some name, Some handle, Some url, Some glyph, Some color, Some qrImageUrl, Some featured when idElement.ValueKind = JsonValueKind.Null || idElement.ValueKind = JsonValueKind.String ->
                let id = if idElement.ValueKind = JsonValueKind.Null then Some(Uuid.CreateVersion7().ToGuidBigEndian()) else idElement.GetString() |> tryPositiveGuid
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
                let parsed = root.EnumerateArray() |> Seq.map parseSocialReplacement |> Seq.toList
                if parsed |> List.exists Option.isNone then
                    do! writeDomainProblem context StatusCodes.Status400BadRequest "social-links.request_invalid" "Invalid social links request" "The social links body is invalid."
                    return StatusCodes.Status400BadRequest
                else
                    let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                    let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                    let! result = AdminContentRepository.replaceSocialLinks source (parsed |> List.choose id) (clock.GetUtcNow()) context.RequestAborted
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
                        let! published = publishEvent context DomainEventType.SocialLinkChanged "Web10.Radio.API.Admin" (JsonSerializer.Serialize({| count = List.length links |}, DomainJson.options))
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
    type private PlaylistBody =
        { Name: string
          Description: string option
          IsActive: bool
          Type: PlaylistType
          Source: PlaylistSource
          Order: PlaylistOrder
          Weight: int
          IsJingle: bool
          Interrupt: bool
          AvoidDuplicates: bool
          PlayEverySongs: int option
          PlayEveryMinutes: int option
          PlayAtMinute: int option
          Schedules: PlaylistSchedule list }

    let private tryInt (name: string) (root: JsonElement) =
        match tryProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Number ->
            let mutable parsed = 0
            if value.TryGetInt32(&parsed) then Some parsed else None
        | _ -> None

    let private tryNullableInt (name: string) (root: JsonElement) =
        match tryProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Null -> Some None
        | Some value when value.ValueKind = JsonValueKind.Number ->
            let mutable parsed = 0
            if value.TryGetInt32(&parsed) then Some(Some parsed) else None
        | _ -> None

    let private tryBool (name: string) (root: JsonElement) =
        match tryProperty name root with
        | Some value when value.ValueKind = JsonValueKind.True -> Some true
        | Some value when value.ValueKind = JsonValueKind.False -> Some false
        | _ -> None

    let private parsePlaylistType = function
        | "general" -> Some PlaylistType.General
        | "oncePerSongs" -> Some PlaylistType.OncePerSongs
        | "oncePerMinutes" -> Some PlaylistType.OncePerMinutes
        | "oncePerHour" -> Some PlaylistType.OncePerHour
        | _ -> None

    let private parsePlaylistSource = function
        | "manual" -> Some PlaylistSource.Manual
        | "allStorage" -> Some PlaylistSource.AllStorage
        | _ -> None

    let private parsePlaylistOrder = function
        | "sequential" -> Some PlaylistOrder.Sequential
        | "shuffle" -> Some PlaylistOrder.Shuffle
        | "random" -> Some PlaylistOrder.Random
        | _ -> None

    let private parseDate value =
        try Some(DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None)) with _ -> None

    let private parseTime value =
        try Some(TimeSpan.ParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture)) with _ -> None

    let private parsePlaylistSchedule (element: JsonElement) =
        if not (hasExactProperties (Set.ofList [ "id"; "daysOfWeek"; "startTime"; "endTime"; "startDate"; "endDate"; "timeZoneId" ]) element) then None
        else
            match tryProperty "id" element, tryProperty "daysOfWeek" element, tryString "startTime" element, tryString "endTime" element, tryNullableString "startDate" element, tryNullableString "endDate" element, tryString "timeZoneId" element with
            | Some idElement, Some days, Some startTime, Some endTime, Some startDate, Some endDate, Some timeZoneId
                when (idElement.ValueKind = JsonValueKind.Null || idElement.ValueKind = JsonValueKind.String) && days.ValueKind = JsonValueKind.Array && not (String.IsNullOrWhiteSpace timeZoneId) ->
                let scheduleId = if idElement.ValueKind = JsonValueKind.Null then Some(Uuid.CreateVersion7().ToGuidBigEndian()) else idElement.GetString() |> tryPositiveGuid
                let dayValues =
                    days.EnumerateArray()
                    |> Seq.map (fun day -> let mutable parsed = 0 in if day.ValueKind = JsonValueKind.Number && day.TryGetInt32(&parsed) then Some parsed else None)
                    |> Seq.toList
                let startDateValue = startDate |> Option.bind parseDate
                let endDateValue = endDate |> Option.bind parseDate
                let values = dayValues |> List.choose (fun value -> value)
                let validDays = dayValues.Length = values.Length && Set.ofList values |> Set.count = values.Length && values |> List.forall (fun value -> value >= 1 && value <= 7)
                let validTimeZone = try TimeZoneInfo.FindSystemTimeZoneById(timeZoneId) |> ignore; true with _ -> false
                match scheduleId, parseTime startTime, parseTime endTime, startDateValue, endDateValue with
                | Some scheduleId, Some start, Some finish, Some parsedStartDate, Some parsedEndDate when validDays && validTimeZone && parsedStartDate <= parsedEndDate ->
                    Some { Id = Some scheduleId; DaysOfWeek = values; StartTime = start; EndTime = finish; StartDate = Some parsedStartDate; EndDate = Some parsedEndDate; TimeZoneId = timeZoneId }
                | Some scheduleId, Some start, Some finish, None, None when validDays && validTimeZone ->
                    Some { Id = Some scheduleId; DaysOfWeek = values; StartTime = start; EndTime = finish; StartDate = None; EndDate = None; TimeZoneId = timeZoneId }
                | _ -> None
            | _ -> None

    let private parsePlaylistBody root : PlaylistBody option =
        let expected = Set.ofList [ "name"; "description"; "isActive"; "type"; "source"; "order"; "weight"; "isJingle"; "interrupt"; "avoidDuplicates"; "playEverySongs"; "playEveryMinutes"; "playAtMinute"; "schedules" ]
        if not (hasExactProperties expected root) then None
        else
            match tryString "name" root, tryNullableString "description" root, tryBool "isActive" root, tryString "type" root, tryString "source" root, tryString "order" root, tryInt "weight" root, tryBool "isJingle" root, tryBool "interrupt" root, tryBool "avoidDuplicates" root, tryNullableInt "playEverySongs" root, tryNullableInt "playEveryMinutes" root, tryNullableInt "playAtMinute" root, tryProperty "schedules" root with
            | Some name, Some description, Some isActive, Some typeValue, Some sourceValue, Some orderValue, Some weight, Some isJingle, Some interrupt, Some avoidDuplicates, Some everySongs, Some everyMinutes, Some atMinute, Some schedules when schedules.ValueKind = JsonValueKind.Array ->
                match parsePlaylistType typeValue, parsePlaylistSource sourceValue, parsePlaylistOrder orderValue with
                | Some playlistType, Some playlistSource, Some playlistOrder ->
                    let cadenceValid =
                        match playlistType, everySongs, everyMinutes, atMinute with
                        | PlaylistType.General, None, None, None -> true
                        | PlaylistType.OncePerSongs, Some value, None, None -> value >= 1 && value <= 1000
                        | PlaylistType.OncePerMinutes, None, Some value, None -> value >= 1 && value <= 10080
                        | PlaylistType.OncePerHour, None, None, Some value -> value >= 0 && value <= 59
                        | _ -> false
                    let parsedSchedules = schedules.EnumerateArray() |> Seq.map parsePlaylistSchedule |> Seq.toList
                    let normalizedName = name.Trim()
                    let normalizedDescription = description |> Option.map _.Trim()
                    if cadenceValid && normalizedName.Length >= 1 && normalizedName.Length <= 120 && normalizedDescription |> Option.exists (fun value -> value.Length > 1000) |> not && weight >= 1 && weight <= 25 && parsedSchedules.Length <= 32 && parsedSchedules |> List.forall Option.isSome then
                        Some { Name = normalizedName; Description = normalizedDescription; IsActive = isActive; Type = playlistType; Source = playlistSource; Order = playlistOrder; Weight = weight; IsJingle = isJingle; Interrupt = interrupt; AvoidDuplicates = avoidDuplicates; PlayEverySongs = everySongs; PlayEveryMinutes = everyMinutes; PlayAtMinute = atMinute; Schedules = parsedSchedules |> List.choose (fun value -> value) }
                    else None
                | _ -> None
            | _ -> None

    let private playlistChangedOutbox (clock: TimeProvider) (playlistId: Guid) =
        let payload = JsonSerializer.Serialize({| playlistId = playlistId.ToString("D") |}, DomainJson.options)
        DomainEventEnvelope.create clock PlaylistChanged "Web10.Radio.API.Admin" None None payload
        |> Result.map OutboxMapping.toOutboxEvent
        |> Result.defaultWith (fun error -> failwithf "Unable to create PlaylistChanged event: %s" (error.ToString()))

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
                | Some body ->
                    let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                    let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                    let now = clock.GetUtcNow()
                    let playlistId = Uuid.CreateVersion7().ToGuidBigEndian()
                    let! result =
                        AdminContentRepository.createPlaylistWithEvent
                            source
                            { Id = playlistId
                              Name = body.Name
                              Description = body.Description
                              IsActive = body.IsActive
                              Type = body.Type
                              Source = body.Source
                              Order = body.Order
                              Weight = body.Weight
                              IsJingle = body.IsJingle
                              Interrupt = body.Interrupt
                              AvoidDuplicates = body.AvoidDuplicates
                              PlayEverySongs = body.PlayEverySongs
                              PlayEveryMinutes = body.PlayEveryMinutes
                              PlayAtMinute = body.PlayAtMinute
                              IsSystem = false
                              Schedules = body.Schedules
                              CreatedAtUtc = now
                              UpdatedAtUtc = now }
                            (Some(playlistChangedOutbox clock playlistId))
                            context.RequestAborted
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
                    | Some body ->
                        let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                        let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                        let! result =
                            AdminContentRepository.updatePlaylistWithEvent
                                source
                                playlistId
                                { Name = body.Name
                                  Description = body.Description
                                  IsActive = body.IsActive
                                  Type = body.Type
                                  Source = body.Source
                                  Order = body.Order
                                  Weight = body.Weight
                                  IsJingle = body.IsJingle
                                  Interrupt = body.Interrupt
                                  AvoidDuplicates = body.AvoidDuplicates
                                  PlayEverySongs = body.PlayEverySongs
                                  PlayEveryMinutes = body.PlayEveryMinutes
                                  PlayAtMinute = body.PlayAtMinute
                                  Schedules = body.Schedules
                                  UpdatedAtUtc = clock.GetUtcNow() }
                                (Some(playlistChangedOutbox clock playlistId))
                                context.RequestAborted
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
                        let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                        let! result = AdminContentRepository.createPlaylistItem source playlistId { Id = Uuid.CreateVersion7().ToGuidBigEndian(); TrackId = trackId; CreatedAtUtc = (clock.GetUtcNow()) } context.RequestAborted
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
                        let parsed =
                            items.EnumerateArray()
                            |> Seq.map (fun item ->
                                if hasExactProperties (Set.ofList [ "id"; "trackId" ]) item then
                                    match tryProperty "id" item, tryString "trackId" item |> Option.bind tryPositiveGuid with
                                    | Some id, Some trackId when id.ValueKind = JsonValueKind.Null -> Some { Id = Uuid.CreateVersion7().ToGuidBigEndian(); TrackId = trackId }
                                    | Some id, Some trackId when id.ValueKind = JsonValueKind.String -> id.GetString() |> tryPositiveGuid |> Option.map (fun itemId -> { Id = itemId; TrackId = trackId })
                                    | _ -> None
                                else None)
                            |> Seq.toList
                        if parsed |> List.exists Option.isNone then
                            do! writeDomainProblem context StatusCodes.Status400BadRequest "playlist.request_invalid" "Invalid playlist request" "The playlist items body is invalid."
                            return StatusCodes.Status400BadRequest
                        else
                            let source = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                            let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                            let! result = AdminContentRepository.replacePlaylistItems source playlistId (parsed |> List.choose id) (clock.GetUtcNow()) context.RequestAborted
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
                    let parsed =
                        array.EnumerateArray()
                        |> Seq.map (fun item ->
                            if not (hasExactProperties (Set.ofList [ "id"; "name"; "type"; "localRoot"; "s3Bucket"; "isEnabled" ]) item) then None
                            else
                                match tryProperty "id" item, tryString "name" item, tryString "type" item, tryNullableString "localRoot" item, tryNullableString "s3Bucket" item, tryProperty "isEnabled" item with
                                | Some id, Some name, Some storageType, Some localRoot, Some bucket, Some enabled when (id.ValueKind = JsonValueKind.Null || id.ValueKind = JsonValueKind.String) && (enabled.ValueKind = JsonValueKind.True || enabled.ValueKind = JsonValueKind.False) ->
                                    let backendId = if id.ValueKind = JsonValueKind.Null then Some(Uuid.CreateVersion7().ToGuidBigEndian()) else id.GetString() |> tryPositiveGuid
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
                        let clock = context.RequestServices.GetRequiredService<TimeProvider>()
                        let! result = AdminContentRepository.replaceAdditionalStorageBackends source (parsed |> List.choose id) (clock.GetUtcNow()) context.RequestAborted
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
                    let dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>()
                    let clock = context.RequestServices.GetRequiredService<TimeProvider>()

                    let! result =
                        DevelopmentFixtures.createPaidVerticalSlice
                            dataSource
                            clock
                            50
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
        map app logger "GET" "/api/v0/player/assets/cover/{trackId}" "/api/v0/player/assets/cover/{trackId}" playerCover
        map admin logger "PUT" "/tracks/{trackId}" "/api/v0/admin/tracks/{trackId}" (csrfProtected adminUpdateTrackMetadata)
        map admin logger "PUT" "/tracks/{trackId}/cover" "/api/v0/admin/tracks/{trackId}/cover" (csrfProtected adminReplaceTrackCover)
        map admin logger "DELETE" "/tracks/{trackId}/cover" "/api/v0/admin/tracks/{trackId}/cover" (csrfProtected adminRemoveTrackCover)
        map admin logger "GET" "/social-links" "/api/v0/admin/social-links" adminSocialLinks
        map admin logger "PUT" "/social-links" "/api/v0/admin/social-links" (csrfProtected adminSocialLinksUpdate)
        map admin logger "GET" "/donation-goal" "/api/v0/admin/donation-goal" adminDonationGoal
        map admin logger "PUT" "/donation-goal" "/api/v0/admin/donation-goal" (csrfProtected adminDonationGoalUpdate)
        map admin logger "POST" "/library/scan" "/api/v0/admin/library/scan" (csrfProtected libraryScan)
        map admin logger "GET" "/library/scan/{scanJobId}" "/api/v0/admin/library/scan/{scanJobId}" libraryScanStatus
        map admin logger "GET" "/tracks" "/api/v0/admin/tracks" adminTracks
        map admin logger "POST" "/playback/queue" "/api/v0/admin/playback/queue" (csrfProtected adminQueueTrack)
        map admin logger "PUT" "/playback/queue/order" "/api/v0/admin/playback/queue/order" (csrfProtected adminReorderQueue)
        map admin logger "POST" "/playback/skip" "/api/v0/admin/playback/skip" (csrfProtected adminSkipCurrent)
        map admin logger "POST" "/playback/restart-current" "/api/v0/admin/playback/restart-current" (csrfProtected adminRestartCurrent)
        map admin logger "POST" "/playback/play-now" "/api/v0/admin/playback/play-now" (csrfProtected adminPlayNow)
        map admin logger "GET" "/playlists" "/api/v0/admin/playlists" adminPlaylists
        map admin logger "POST" "/playlists" "/api/v0/admin/playlists" (csrfProtected createPlaylist)
        map admin logger "PUT" "/playlists/{playlistId}" "/api/v0/admin/playlists/{playlistId}" (csrfProtected updatePlaylist)
        map admin logger "GET" "/playlists/{playlistId}/items" "/api/v0/admin/playlists/{playlistId}/items" playlistItems
        map admin logger "POST" "/playlists/{playlistId}/items" "/api/v0/admin/playlists/{playlistId}/items" (csrfProtected createPlaylistItem)
        map admin logger "PUT" "/playlists/{playlistId}/items" "/api/v0/admin/playlists/{playlistId}/items" (csrfProtected replacePlaylistItems)
        map admin logger "GET" "/storage" "/api/v0/admin/storage" adminStorage
        map admin logger "PUT" "/storage" "/api/v0/admin/storage" (csrfProtected adminStorageUpdate)
        map admin logger "GET" "/stream-node/status" "/api/v0/admin/stream-node/status" adminStreamStatus
        map admin logger "POST" "/stream-node/start" "/api/v0/admin/stream-node/start" (csrfProtected (adminStreamControl "start"))
        map admin logger "POST" "/stream-node/stop" "/api/v0/admin/stream-node/stop" (csrfProtected (adminStreamControl "stop"))
        map admin logger "POST" "/stream-node/restart" "/api/v0/admin/stream-node/restart" (csrfProtected (adminStreamControl "restart"))
