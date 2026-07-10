namespace Web10.Radio.Tests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.Mvc.Testing
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Hosting
open Npgsql
open NUnit.Framework

module ApiContractTests =
    [<Literal>]
    let private WebhookSecret = "test-webhook-secret"

    [<Literal>]
    let private AdminToken = "admin-token-Secret_1234567890"

    type private SseEvent =
        { Name: string
          Data: string }

    let private configurationPairs connectionString tempRoot =
        let strippedPairs =
            [ "POSTGRES:CONNECTION_STRING", connectionString
              "TELEGRAM:BOT_TOKEN", "123456:AbcdefghijklmnopQRSTuvwx"
              "TELEGRAM:WEBHOOK_SECRET", WebhookSecret
              "TELEGRAM:CHANNEL_ID_OR_USERNAME", "@netscapedidnothingwrong"
              "STREAM:RTMP_URL", "rtmps://dc4-1.rtmp.t.me/s/"
              "STREAM:RTMP_KEY", "rtmp-key-Secret_12345"
              "STREAM:STAGE_URL", "https://stage.web10.radio/"
              "STREAM:CALLBACK_TOKEN", "stream-callback-token-Secret_123456"
              "STORAGE:TYPE", "Local"
              "STORAGE:LOCAL_ROOT", Path.Combine(tempRoot, "library")
              "OTEL:EXPORTER_OTLP_ENDPOINT", "http://localhost:4317"
              "ADMIN:TOKEN", AdminToken
              "DATA_PROTECTION:KEY_RING_PATH", Path.Combine(tempRoot, "keys") ]

        let web10Mirrors =
            strippedPairs
            |> List.map (fun (key, value) -> sprintf "WEB10_%s" (key.Replace(":", "__")), value)

        strippedPairs @ web10Mirrors
        |> List.map (fun (key, value) -> KeyValuePair<string, string>(key, value))

    type private FixedClock(nowUtc: DateTimeOffset) =
        interface Web10.Radio.API.IClock with
            member _.UtcNow = nowUtc


    type private AuthenticatedTelegramIdentityProbe() =
        interface Web10.Radio.API.ITelegramIdentityProbe with
            member _.IsAuthenticatedBotAsync(_cancellationToken) = Task.FromResult(true)

    type private CapturedLog =
        { Level: LogLevel
          Message: string
          Exception: exn option }

    type private CapturingLogger(entries: ConcurrentQueue<CapturedLog>) =
        interface ILogger with
            member _.BeginScope<'TState>(_state: 'TState) : IDisposable = null
            member _.IsEnabled(_level: LogLevel) = true

            member _.Log<'TState>(level: LogLevel, _eventId: EventId, state: 'TState, error: exn, formatter: Func<'TState, exn, string>) =
                entries.Enqueue(
                    { Level = level
                      Message = formatter.Invoke(state, error)
                      Exception = Option.ofObj error }
                )

    type private CapturingLoggerProvider(entries: ConcurrentQueue<CapturedLog>) =
        interface ILoggerProvider with
            member _.CreateLogger(_categoryName) = CapturingLogger(entries) :> ILogger
            member _.Dispose() = ()

    type private ControlledPlayerEventsDelay() =
        let signals = new SemaphoreSlim(0)

        member _.Advance() = signals.Release() |> ignore

        interface Web10.Radio.API.IPlayerEventsDelay with
            member _.WaitForNextSnapshotAsync(cancellationToken) = signals.WaitAsync(cancellationToken)
    type private StartedResponseFeature(statusCode: int) =
        let mutable currentStatusCode = statusCode
        let mutable reasonPhrase: string = null
        let mutable headers: IHeaderDictionary = HeaderDictionary()
        let mutable body: Stream = new MemoryStream()

        interface IHttpResponseFeature with
            member _.StatusCode with get () = currentStatusCode and set value = currentStatusCode <- value
            member _.ReasonPhrase with get () = reasonPhrase and set value = reasonPhrase <- value
            member _.Headers with get () = headers and set value = headers <- value
            member _.Body with get () = body and set value = body <- value
            member _.HasStarted = true
            member _.OnStarting(_, _) = ()
            member _.OnCompleted(_, _) = ()

    let private createFactory
        connectionString
        tempRoot
        (clock: Web10.Radio.API.IClock option)
        (loggerProvider: ILoggerProvider option)
        (eventsDelay: Web10.Radio.API.IPlayerEventsDelay option)
        =
        let pairs = configurationPairs connectionString tempRoot

        (new WebApplicationFactory<Web10.Radio.API.ApiProgramMarker>())
            .WithWebHostBuilder(fun builder ->
                pairs |> List.iter (fun pair -> builder.UseSetting(pair.Key, pair.Value) |> ignore)

                builder.ConfigureAppConfiguration(fun _ configurationBuilder ->
                    configurationBuilder.AddInMemoryCollection(pairs) |> ignore)
                |> ignore

                match clock with
                | Some value ->
                    builder.ConfigureServices(fun services ->
                        services.AddSingleton<Web10.Radio.API.IClock>(value) |> ignore)
                    |> ignore
                | None -> ()

                match eventsDelay with
                | Some value ->
                    builder.ConfigureServices(fun services ->
                        services.AddSingleton<Web10.Radio.API.IPlayerEventsDelay>(value) |> ignore)
                    |> ignore
                | None -> ()

                match loggerProvider with
                | Some provider ->
                    builder.ConfigureLogging(fun logging -> logging.AddProvider(provider) |> ignore)
                    |> ignore
                | None -> ())

    let private withApiClient (work: string -> HttpClient -> Task<'T>) =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let tempRoot = Directory.CreateTempSubdirectory("web10-radio-api-tests-")
                let factory = createFactory connectionString tempRoot.FullName None None None
                let client = factory.CreateClient()

                try
                    let! result = work connectionString client
                    return result
                finally
                    client.Dispose()
                    factory.Dispose()
                    tempRoot.Delete(true)
            })

    let private withApiClientAndEventsDelay
        nowUtc
        (delay: ControlledPlayerEventsDelay)
        (work: string -> HttpClient -> Task<'T>)
        =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let tempRoot = Directory.CreateTempSubdirectory("web10-radio-api-tests-")
                let clock = FixedClock(nowUtc) :> Web10.Radio.API.IClock
                let factory =
                    createFactory
                        connectionString
                        tempRoot.FullName
                        (Some clock)
                        None
                        (Some(delay :> Web10.Radio.API.IPlayerEventsDelay))

                let client = factory.CreateClient()

                try
                    return! work connectionString client
                finally
                    client.Dispose()
                    factory.Dispose()
                    tempRoot.Delete(true)
            })

    let private withApiClientAt nowUtc (work: string -> string -> HttpClient -> Task<'T>) =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let tempRoot = Directory.CreateTempSubdirectory("web10-radio-api-tests-")
                let clock = FixedClock(nowUtc) :> Web10.Radio.API.IClock
                let factory = createFactory connectionString tempRoot.FullName (Some clock) None None
                let client = factory.CreateClient()

                try
                    let! result = work connectionString tempRoot.FullName client
                    return result
                finally
                    client.Dispose()
                    factory.Dispose()
                    tempRoot.Delete(true)
            })

    let private withApiClientWithServices
        (configureServices: IServiceCollection -> unit)
        (work: string -> string -> WebApplicationFactory<Web10.Radio.API.ApiProgramMarker> -> HttpClient -> Task<'T>)
        =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let tempRoot = Directory.CreateTempSubdirectory("web10-radio-api-tests-")

                let factory =
                    (createFactory connectionString tempRoot.FullName None None None)
                        .WithWebHostBuilder(fun builder ->
                            builder.ConfigureServices(fun services -> configureServices services) |> ignore)

                let client = factory.CreateClient()

                try
                    return! work connectionString tempRoot.FullName factory client
                finally
                    client.Dispose()
                    factory.Dispose()
                    tempRoot.Delete(true)
            })

    let private jsonDocument (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()

            try
                return JsonDocument.Parse(body)
            with ex ->
                return raise (AssertionException(sprintf "Expected JSON response but got: %s" body, ex))
        }

    let private jsonProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &value) then
            value
        else
            raise (AssertionException(sprintf "Expected JSON property '%s' on %O." name element))

    let private stringProperty (name: string) element =
        (jsonProperty name element).GetString()

    let private intProperty (name: string) element =
        (jsonProperty name element).GetInt32()

    let private boolProperty (name: string) element =
        (jsonProperty name element).GetBoolean()

    let private arrayLengthProperty (name: string) element =
        (jsonProperty name element).GetArrayLength()

    let private valueKindProperty (name: string) element =
        (jsonProperty name element).ValueKind


    let private assertProblemCode (expectedCode: string) (response: HttpResponseMessage) =
        task {
            let! document = jsonDocument response
            use document = document
            Assert.That(document.RootElement |> stringProperty "code", Is.EqualTo(expectedCode))
        }

    let private postJson (client: HttpClient) (uri: string) (body: string) (secret: string option) =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, uri)
            request.Content <- new StringContent(body, Encoding.UTF8, "application/json")

            match secret with
            | Some value -> request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", value)
            | None -> ()

            return! client.SendAsync(request)
        }

    let private sendWebhook (client: HttpClient) (body: string) (secrets: string list) =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, "/api/v0/telegram/webhook")
            request.Content <- new StringContent(body, Encoding.UTF8, "application/json")

            for secret in secrets do
                request.Headers.TryAddWithoutValidation("X-Telegram-Bot-Api-Secret-Token", secret) |> ignore

            return! client.SendAsync(request)
        }

    let private sendAdminRequest (client: HttpClient) (authorization: string option) (method': HttpMethod) (uri: string) =
        task {
            use request = new HttpRequestMessage(method', uri)

            if method' <> HttpMethod.Get then
                request.Content <- new StringContent("{}", Encoding.UTF8, "application/json")

            match authorization with
            | Some value -> request.Headers.TryAddWithoutValidation("Authorization", value) |> ignore
            | None -> ()

            return! client.SendAsync(request)
        }

    let private sendAdminRequestWithAuthorizations
        (client: HttpClient)
        (authorizations: string list)
        (method': HttpMethod)
        (uri: string)
        =
        task {
            use request = new HttpRequestMessage(method', uri)

            if method' <> HttpMethod.Get then
                request.Content <- new StringContent("{}", Encoding.UTF8, "application/json")

            for authorization in authorizations do
                request.Headers.TryAddWithoutValidation("Authorization", authorization) |> ignore

            return! client.SendAsync(request)
        }

    let private sendPlaybackCallback
        (client: HttpClient)
        (uri: string)
        (body: string)
        (authorizations: string list)
        =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, uri)
            request.Content <- new StringContent(body, Encoding.UTF8, "application/json")

            for authorization in authorizations do
                request.Headers.TryAddWithoutValidation("Authorization", authorization) |> ignore

            return! client.SendAsync(request)
        }

    let private typedRequestUpdate updateId query =
        sprintf
            """{"update_id":%d,"message":{"message_id":7,"date":1783400400,"chat":{"id":500,"type":"private"},"from":{"id":500,"is_bot":false,"first_name":"Regression"},"text":"/request %s"}}"""
            updateId
            query


    let private typedSayUpdate updateId text =
        sprintf
            """{"update_id":%d,"message":{"message_id":8,"date":1783400401,"chat":{"id":501,"type":"private"},"from":{"id":501,"is_bot":false,"first_name":"Relay"},"text":"/say %s"}}"""
            updateId
            text

    let private typedSuccessfulPaymentUpdate updateId paymentId chargeId amountStars =
        sprintf
            """{"update_id":%d,"message":{"message_id":9,"date":1783400402,"chat":{"id":502,"type":"private"},"from":{"id":502,"is_bot":false,"first_name":"Payer"},"successful_payment":{"currency":"XTR","total_amount":%d,"invoice_payload":"%O","telegram_payment_charge_id":"%s"}}}"""
            updateId
            amountStars
            paymentId
            chargeId
    let private paddedTypedRequestUpdate updateId targetBytes =
        let prefix =
            sprintf
                "{\"update_id\":%d,\"message\":{\"message_id\":7,\"date\":1783400400,\"chat\":{\"id\":500,\"type\":\"private\"},\"from\":{\"id\":500,\"is_bot\":false,\"first_name\":\"Regression\"},\"text\":\"/request at-limit\"},\"padding\":\""
                updateId

        let suffix = "\"}"
        let paddingLength = targetBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix)

        if paddingLength < 0 then
            raise (AssertionException(sprintf "Cannot form a %d-byte valid Telegram update." targetBytes))

        prefix + String.replicate paddingLength "x" + suffix

    let private countOutboxRows (connectionString: string) (eventType: string) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT count(*)
FROM "OutboxEvents"
WHERE "EventType" = @EventType
  AND "IsDeleted" = false;""",
                    connection
                )

            command.Parameters.AddWithValue("EventType", eventType) |> ignore
            let! count = command.ExecuteScalarAsync()
            return Convert.ToInt32(count)
        }

    let private countTrackRequestRows (connectionString: string) (query: string) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand("""SELECT count(*) FROM "TrackRequests" WHERE "Query" = @Query AND "IsDeleted" = false;""", connection)
            command.Parameters.AddWithValue("Query", query) |> ignore
            let! count = command.ExecuteScalarAsync()
            return Convert.ToInt32(count)
        }

    let private countSayMessageRows (connectionString: string) (text: string) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand("""SELECT count(*) FROM "SayMessages" WHERE "Text" = @Text AND "IsDeleted" = false;""", connection)
            command.Parameters.AddWithValue("Text", text) |> ignore
            let! count = command.ExecuteScalarAsync()
            return Convert.ToInt32(count)
        }

    let private seedDonationPayment connectionString paymentId amountStars =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """INSERT INTO "Payments" ("Id", "Purpose", "AmountStars", "Currency", "TelegramInvoicePayload", "Status", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@PaymentId, 'Donation', @AmountStars, 'XTR', @InvoicePayload, 'InvoiceCreated', false, @NowUtc, @NowUtc);""",
                    connection
                )

            command.Parameters.AddWithValue("PaymentId", paymentId) |> ignore
            command.Parameters.AddWithValue("AmountStars", amountStars) |> ignore
            command.Parameters.AddWithValue("InvoicePayload", string paymentId) |> ignore
            command.Parameters.AddWithValue("NowUtc", DateTimeOffset(2026, 7, 10, 22, 30, 0, TimeSpan.Zero)) |> ignore
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }


    let private seedHeartbeat connectionString status heartbeatAtUtc =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """INSERT INTO "StreamNodeHeartbeats" (
    "Id", "Status", "HeartbeatAtUtc", "FailureReason", "Metadata", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (@Id, @Status, @HeartbeatAtUtc, NULL, '{"bitrateKbps":192}'::jsonb, false, @HeartbeatAtUtc, @HeartbeatAtUtc);""",
                    connection
                )

            command.Parameters.AddWithValue("Id", Guid.NewGuid()) |> ignore
            command.Parameters.AddWithValue("Status", status) |> ignore
            command.Parameters.AddWithValue("HeartbeatAtUtc", heartbeatAtUtc) |> ignore
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    let private seedCachedPlayingTrack connectionString cacheRoot (startedAtUtc: DateTimeOffset) =
        task {
            Directory.CreateDirectory(cacheRoot) |> ignore
            let cachePath = Path.Combine(cacheRoot, "live-track.mp3")
            let bytes = Encoding.ASCII.GetBytes("0123456789")
            do! File.WriteAllBytesAsync(cachePath, bytes)

            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            let trackId = Guid.NewGuid()
            let fileId = Guid.NewGuid()
            let queueItemId = Guid.NewGuid()

            use command =
                new NpgsqlCommand(
                    """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@TrackId, 'Live range track', 'Regression', false, @NowUtc, @NowUtc);
INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "CachePath", "ContentType", "SizeBytes", "IsCached", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@FileId, @TrackId, 'live-track.mp3', @CachePath, 'audio/mpeg', 10, true, false, @NowUtc, @NowUtc);
INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "RequestedAtUtc", "StartedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@QueueItemId, @TrackId, 'admin', 'Playing', @NowUtc, @NowUtc, @ClaimOwner, 1, @ClaimLeaseExpiresAtUtc, false, @NowUtc, @NowUtc);""",
                    connection
                )

            command.Parameters.AddWithValue("TrackId", trackId) |> ignore
            command.Parameters.AddWithValue("FileId", fileId) |> ignore
            command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
            command.Parameters.AddWithValue("CachePath", cachePath) |> ignore
            command.Parameters.AddWithValue("NowUtc", startedAtUtc) |> ignore
            command.Parameters.AddWithValue("ClaimOwner", Guid.NewGuid()) |> ignore
            command.Parameters.AddWithValue("ClaimLeaseExpiresAtUtc", startedAtUtc.AddMinutes(1.0)) |> ignore
            let! _ = command.ExecuteNonQueryAsync()
            return bytes
        }

    let private countInboxRows (connectionString: string) (telegramUpdateId: int64) (eventType: string) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT count(*)
FROM "TelegramUpdateInbox"
WHERE "TelegramUpdateId" = @TelegramUpdateId
  AND "EventType" = @EventType
  AND "IsDeleted" = false;""",
                    connection
                )

            command.Parameters.AddWithValue("TelegramUpdateId", telegramUpdateId) |> ignore
            command.Parameters.AddWithValue("EventType", eventType) |> ignore
            let! count = command.ExecuteScalarAsync()
            return Convert.ToInt32(count)
        }

    let private countAllInboxRows (connectionString: string) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT count(*)
FROM "TelegramUpdateInbox"
WHERE "IsDeleted" = false;""",
                    connection
                )

            let! count = command.ExecuteScalarAsync()
            return Convert.ToInt32(count)
        }

    let private assertNoWebhookEffects connectionString =
        task {
            let! inboxCount = countAllInboxRows connectionString
            let! outboxCount = countOutboxRows connectionString "TrackRequested"
            Assert.That(inboxCount, Is.EqualTo(0), "Rejected webhook must not create an inbox record.")
            Assert.That(outboxCount, Is.EqualTo(0), "Rejected webhook must not create a durable domain event.")
        }

    let private seedAdminReadModels connectionString =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use socialCommand =
                new NpgsqlCommand(
                    """INSERT INTO "SocialLinks" (
    "Id", "Kind", "Name", "Handle", "Url", "Glyph", "Color", "QrImageUrl", "IsFeatured", "Position"
)
VALUES (@SocialLinkId, 'telegram', 'Telegram', NULL, 'https://t.me/web10radio', NULL, NULL, NULL, true, 2);""",
                    connection
                )

            socialCommand.Parameters.AddWithValue("SocialLinkId", Guid.Parse("01920000-0000-7000-8000-00000000a001")) |> ignore
            let! _ = socialCommand.ExecuteNonQueryAsync()

            use goalCommand =
                new NpgsqlCommand(
                    """INSERT INTO "DonationGoals" (
    "Id", "Title", "GoalStars", "RaisedStars", "IsActive"
)
VALUES (@DonationGoalId, 'Keep Web10.Radio live', 1000, 250, true);""",
                    connection
                )

            goalCommand.Parameters.AddWithValue("DonationGoalId", Guid.Parse("01920000-0000-7000-8000-00000000a002")) |> ignore
            let! _ = goalCommand.ExecuteNonQueryAsync()
            return ()
        }

    let private seedSseFragments connectionString nowUtc =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            let trackId = Guid.NewGuid()
            let queueItemId = Guid.NewGuid()
            let sayMessageId = Guid.NewGuid()

            use command =
                new NpgsqlCommand(
                    """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@TrackId, 'SSE queue track', 'SSE artist', false, @NowUtc, @NowUtc);
INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "RequestedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@QueueItemId, @TrackId, 'admin', 'Queued', @NowUtc, false, @NowUtc, @NowUtc);
INSERT INTO "SayMessages" ("Id", "DisplayName", "Text", "AmountStars", "Color", "Status", "SubmittedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@SayMessageId, 'SSE sender', 'SSE moderation payload', 100, '#e0439a', 'Approved', @NowUtc, false, @NowUtc, @NowUtc);""",
                    connection
                )

            command.Parameters.AddWithValue("TrackId", trackId) |> ignore
            command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
            command.Parameters.AddWithValue("SayMessageId", sayMessageId) |> ignore
            command.Parameters.AddWithValue("NowUtc", nowUtc) |> ignore
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    let private readSseEvent (reader: StreamReader) (cancellationToken: CancellationToken) =
        task {
            let dataLines = ResizeArray<string>()
            let mutable eventName = ""
            let mutable isComplete = false

            while not isComplete do
                let! line = (reader.ReadLineAsync()).WaitAsync(cancellationToken)

                if isNull line then
                    raise (AssertionException("SSE stream ended before an event was emitted."))
                elif line = "" then
                    if eventName <> "" || dataLines.Count > 0 then
                        isComplete <- true
                elif line.StartsWith("event:", StringComparison.Ordinal) then
                    eventName <- line.Substring("event:".Length).TrimStart()
                elif line.StartsWith("data:", StringComparison.Ordinal) then
                    dataLines.Add(line.Substring("data:".Length).TrimStart())

            return
                { Name = eventName
                  Data = String.concat "\n" dataLines }
        }

    let private readFirstSseEvent (stream: Stream) (cancellationToken: CancellationToken) =
        task {
            use reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true)
            return! readSseEvent reader cancellationToken
        }

    [<Test>]
    let ``GET player stream without live cached track returns stream unavailable problem`` () =
        withApiClient (fun _ client ->
            task {
                let! response = client.GetAsync("/api/v0/player/stream")
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable))
                do! assertProblemCode "stream.unavailable" response
            })



    let private adminRoutes =
        [ HttpMethod.Get, "/api/v0/admin/social-links", HttpStatusCode.OK
          HttpMethod.Put, "/api/v0/admin/social-links", HttpStatusCode.NotImplemented
          HttpMethod.Get, "/api/v0/admin/donation-goal", HttpStatusCode.OK
          HttpMethod.Put, "/api/v0/admin/donation-goal", HttpStatusCode.NotImplemented
          HttpMethod.Get, "/api/v0/admin/playlists", HttpStatusCode.NotImplemented
          HttpMethod.Post, "/api/v0/admin/playlists", HttpStatusCode.NotImplemented
          HttpMethod.Get, "/api/v0/admin/playlists/01920000-0000-7000-8000-000000000111/items", HttpStatusCode.NotImplemented
          HttpMethod.Post, "/api/v0/admin/playlists/01920000-0000-7000-8000-000000000111/items", HttpStatusCode.NotImplemented
          HttpMethod.Put, "/api/v0/admin/playlists/01920000-0000-7000-8000-000000000111/items", HttpStatusCode.NotImplemented
          HttpMethod.Get, "/api/v0/admin/say-messages?status=pending", HttpStatusCode.NotImplemented
          HttpMethod.Post, "/api/v0/admin/say-messages/01920000-0000-7000-8000-000000000112/approve", HttpStatusCode.NotImplemented
          HttpMethod.Post, "/api/v0/admin/say-messages/01920000-0000-7000-8000-000000000112/reject", HttpStatusCode.NotImplemented
          HttpMethod.Get, "/api/v0/admin/storage", HttpStatusCode.NotImplemented
          HttpMethod.Put, "/api/v0/admin/storage", HttpStatusCode.NotImplemented
          HttpMethod.Post, "/api/v0/admin/library/scan", HttpStatusCode.NotImplemented
          HttpMethod.Get, "/api/v0/admin/stream-node/status", HttpStatusCode.NotImplemented
          HttpMethod.Post, "/api/v0/admin/stream-node/restart", HttpStatusCode.NotImplemented ]

    let private outboxPayloadText connectionString eventType =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT "Payload" ->> 'query'
FROM "OutboxEvents"
WHERE "EventType" = @EventType
  AND "IsDeleted" = false
ORDER BY "OccurredAtUtc" ASC
LIMIT 1;""",
                    connection
                )

            command.Parameters.AddWithValue("EventType", eventType) |> ignore
            let! value = command.ExecuteScalarAsync()
            return if isNull value || value = box DBNull.Value then null else string value
        }

    [<Test>]
    let ``every admin route rejects missing wrong and multiple bearer values before its handler`` () =
        withApiClient (fun connectionString client ->
            task {
                do! seedAdminReadModels connectionString

                let rejectedCredentials =
                    [ "missing", []
                      "wrong", [ "Bearer wrong-admin-token-Secret_123456" ]
                      "multiple", [ sprintf "Bearer %s" AdminToken; "Bearer another-admin-token-Secret_123456" ] ]

                for method', uri, expectedAuthorizedStatus in adminRoutes do
                    for credentialName, authorizations in rejectedCredentials do
                        let! rejected = sendAdminRequestWithAuthorizations client authorizations method' uri
                        use rejected = rejected

                        Assert.That(
                            rejected.StatusCode,
                            Is.EqualTo(HttpStatusCode.Unauthorized),
                            sprintf "%O %s with %s credentials" method' uri credentialName
                        )

                        Assert.That(
                            rejected.Headers.GetValues("WWW-Authenticate") |> String.concat ",",
                            Is.EqualTo("Bearer"),
                            sprintf "%O %s must issue the Bearer challenge for %s credentials" method' uri credentialName
                        )

                        do! assertProblemCode "admin.auth.required" rejected

                    let! authorized = sendAdminRequest client (Some(sprintf "Bearer %s" AdminToken)) method' uri
                    use authorized = authorized
                    Assert.That(authorized.StatusCode, Is.EqualTo(expectedAuthorizedStatus), sprintf "%O %s with valid bearer" method' uri)
            })

    [<Test>]
    let ``valid bearer exposes seeded social link and donation goal wire values`` () =
        withApiClient (fun connectionString client ->
            task {
                do! seedAdminReadModels connectionString

                let! socialsResponse =
                    sendAdminRequest client (Some(sprintf "Bearer %s" AdminToken)) HttpMethod.Get "/api/v0/admin/social-links"

                use socialsResponse = socialsResponse
                Assert.That(socialsResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! socials = jsonDocument socialsResponse
                use socials = socials
                Assert.That(socials.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array))
                Assert.That(socials.RootElement.GetArrayLength(), Is.EqualTo(1))
                let social = socials.RootElement.[0]
                Assert.That(social |> stringProperty "id", Is.EqualTo("01920000-0000-7000-8000-00000000a001"))
                Assert.That(social |> stringProperty "kind", Is.EqualTo("telegram"))
                Assert.That(social |> stringProperty "name", Is.EqualTo("Telegram"))
                Assert.That(social |> stringProperty "url", Is.EqualTo("https://t.me/web10radio"))
                Assert.That(social |> boolProperty "isFeatured", Is.True)

                let! goalResponse =
                    sendAdminRequest client (Some(sprintf "Bearer %s" AdminToken)) HttpMethod.Get "/api/v0/admin/donation-goal"

                use goalResponse = goalResponse
                Assert.That(goalResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! goal = jsonDocument goalResponse
                use goal = goal
                Assert.That(goal.RootElement |> stringProperty "title", Is.EqualTo("Keep Web10.Radio live"))
                Assert.That(goal.RootElement |> intProperty "raisedStars", Is.EqualTo(250))
                Assert.That(goal.RootElement |> intProperty "goalStars", Is.EqualTo(1000))
            })

    [<Test>]
    let ``webhook rejects absent wrong and multi-value secret headers without durable effects`` () =
        withApiClient (fun connectionString client ->
            task {
                let body = typedRequestUpdate 9101L "secret validation"

                for secrets in [ []; [ "wrong-secret" ]; [ WebhookSecret; WebhookSecret ] ] do
                    let! response = sendWebhook client body secrets
                    use response = response
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
                    do! assertProblemCode "telegram.webhook.secret_invalid" response
                    do! assertNoWebhookEffects connectionString
            })

    [<Test>]
    let ``accepted typed request webhook durably emits one domain event before duplicate acknowledgement`` () =
        withApiClient (fun connectionString client ->
            task {
                let updateId = 9201L
                let body = typedRequestUpdate updateId "vaporwave"
                let! firstResponse = sendWebhook client body [ WebhookSecret ]
                use firstResponse = firstResponse
                Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                let! inboxCountAfterFirst = countInboxRows connectionString updateId "TrackRequested"
                let! outboxCountAfterFirst = countOutboxRows connectionString "TrackRequested"
                let! payloadText = outboxPayloadText connectionString "TrackRequested"
                Assert.That(inboxCountAfterFirst, Is.EqualTo(1), "204 is not valid until the typed update is durably ingested.")
                Assert.That(outboxCountAfterFirst, Is.EqualTo(1), "204 is not valid until the derived domain event is durably appended.")
                Assert.That(payloadText, Is.EqualTo("vaporwave"), "The durable event must preserve the parsed Telegram command query.")

                let! duplicateResponse = sendWebhook client body [ WebhookSecret ]
                use duplicateResponse = duplicateResponse
                Assert.That(duplicateResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                let! inboxCountAfterDuplicate = countInboxRows connectionString updateId "TrackRequested"
                let! outboxCountAfterDuplicate = countOutboxRows connectionString "TrackRequested"
                Assert.That(inboxCountAfterDuplicate, Is.EqualTo(1))
                Assert.That(outboxCountAfterDuplicate, Is.EqualTo(1))
            })

    [<Test>]
    let ``concurrent delivery of one typed update leaves one durable domain event`` () =
        withApiClient (fun connectionString client ->
            task {
                let updateId = 9202L
                let body = typedRequestUpdate updateId "concurrent request"
                let! responses = Task.WhenAll([| sendWebhook client body [ WebhookSecret ]; sendWebhook client body [ WebhookSecret ] |])

                try
                    for response in responses do
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                finally
                    for response in responses do
                        response.Dispose()

                let! inboxCount = countInboxRows connectionString updateId "TrackRequested"
                let! outboxCount = countOutboxRows connectionString "TrackRequested"
                Assert.That(inboxCount, Is.EqualTo(1))
                Assert.That(outboxCount, Is.EqualTo(1))
            })

    [<Test>]
    let ``malformed webhook JSON is rejected before typed ingestion`` () =
        withApiClient (fun connectionString client ->
            task {
                let! response = sendWebhook client "{not-json" [ WebhookSecret ]
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                do! assertProblemCode "request.invalid" response
                do! assertNoWebhookEffects connectionString
            })

    [<Test>]
    let ``a heartbeat exactly thirty seconds old remains live and serves range requests`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero)

        withApiClientAt nowUtc (fun connectionString tempRoot client ->
            task {
                do! seedHeartbeat connectionString "Live" (nowUtc.AddSeconds(-30.0))
                let! bytes = seedCachedPlayingTrack connectionString tempRoot nowUtc
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let clock = FixedClock(nowUtc) :> Web10.Radio.API.IClock
                let! cachedFile = Web10.Radio.API.PlayerStateReadModel.loadStreamFile dataSource clock CancellationToken.None

                match cachedFile with
                | Ok(Some file) -> Assert.That(File.Exists(file.CachePath), Is.True)
                | Ok None -> Assert.Fail("The seeded fresh Playing track must be stream-readable before the HTTP request.")
                | Error error -> Assert.Fail(sprintf "Stream file read model failed: %A" error)

                dataSource.Dispose()

                let! stateResponse = client.GetAsync("/api/v0/player/state")
                use stateResponse = stateResponse
                Assert.That(stateResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! state = jsonDocument stateResponse
                use state = state
                Assert.That(state.RootElement |> jsonProperty "stream" |> stringProperty "status", Is.EqualTo("live"))

                let! healthResponse = client.GetAsync("/api/v0/player/health")
                use healthResponse = healthResponse
                Assert.That(healthResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! health = jsonDocument healthResponse
                use health = health
                Assert.That(health.RootElement |> stringProperty "status", Is.EqualTo("live"))

                use streamRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v0/player/stream")
                streamRequest.Headers.TryAddWithoutValidation("Range", "bytes=2-5") |> ignore
                let! streamResponse = client.SendAsync(streamRequest)
                use streamResponse = streamResponse
                Assert.That(streamResponse.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent))
                Assert.That(streamResponse.Content.Headers.ContentRange.ToString(), Is.EqualTo("bytes 2-5/10"))
                let! rangedBytes = streamResponse.Content.ReadAsByteArrayAsync()
                Assert.That(rangedBytes, Is.EqualTo(bytes.[2..5] :> obj))
            })

    [<Test>]
    let ``a heartbeat older than thirty seconds is offline in state and health and gates stream`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero)

        withApiClientAt nowUtc (fun connectionString tempRoot client ->
            task {
                do! seedHeartbeat connectionString "Live" (nowUtc.AddSeconds(-30.001))
                let! _ = seedCachedPlayingTrack connectionString tempRoot nowUtc

                let! stateResponse = client.GetAsync("/api/v0/player/state")
                use stateResponse = stateResponse
                Assert.That(stateResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! state = jsonDocument stateResponse
                use state = state
                Assert.That(state.RootElement |> jsonProperty "stream" |> stringProperty "status", Is.EqualTo("offline"))
                Assert.That(
                    state.RootElement |> jsonProperty "stream" |> stringProperty "offlineReason",
                    Is.EqualTo("stream-node heartbeat stale")
                )

                let! healthResponse = client.GetAsync("/api/v0/player/health")
                use healthResponse = healthResponse
                Assert.That(healthResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! health = jsonDocument healthResponse
                use health = health
                Assert.That(health.RootElement |> stringProperty "status", Is.EqualTo("offline"))
                Assert.That(health.RootElement |> stringProperty "offlineReason", Is.EqualTo("stream-node heartbeat stale"))

                let! streamResponse = client.GetAsync("/api/v0/player/stream")
                use streamResponse = streamResponse
                Assert.That(streamResponse.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable))
                do! assertProblemCode "stream.unavailable" streamResponse
            })

    [<Test>]
    let ``webhook accepts exactly one mebibyte typed update and rejects the next byte before durable ingestion`` () =
        withApiClient (fun connectionString client ->
            task {
                let maxBytes = 1024 * 1024
                let acceptedUpdateId = 9301L
                let acceptedBody = paddedTypedRequestUpdate acceptedUpdateId maxBytes
                Assert.That(Encoding.UTF8.GetByteCount(acceptedBody), Is.EqualTo(maxBytes))

                let! accepted = sendWebhook client acceptedBody [ WebhookSecret ]
                use accepted = accepted
                Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                let! acceptedInbox = countInboxRows connectionString acceptedUpdateId "TrackRequested"
                let! acceptedOutbox = countOutboxRows connectionString "TrackRequested"
                Assert.That(acceptedInbox, Is.EqualTo(1))
                Assert.That(acceptedOutbox, Is.EqualTo(1))

                let oversizedUpdateId = 9302L
                let oversizedBody = paddedTypedRequestUpdate oversizedUpdateId (maxBytes + 1)
                Assert.That(Encoding.UTF8.GetByteCount(oversizedBody), Is.EqualTo(maxBytes + 1))
                let! oversized = sendWebhook client oversizedBody [ WebhookSecret ]
                use oversized = oversized
                Assert.That(oversized.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge))
                do! assertProblemCode "request.too_large" oversized
                let! oversizedInbox = countInboxRows connectionString oversizedUpdateId "TrackRequested"
                let! outboxAfterOversized = countOutboxRows connectionString "TrackRequested"
                Assert.That(oversizedInbox, Is.EqualTo(0))
                Assert.That(outboxAfterOversized, Is.EqualTo(1))
            })

    [<Test>]
    let ``telegram adapter exposes parse failure then clears it on a newer accepted update without regressing last update id`` () =
        withApiClient (fun _ client ->
            task {
                let firstUpdateId = 9402L
                let! first = sendWebhook client (typedRequestUpdate firstUpdateId "adapter state") [ WebhookSecret ]
                use first = first
                Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                let! malformed = sendWebhook client "{not-json" [ WebhookSecret ]
                use malformed = malformed
                Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                do! assertProblemCode "request.invalid" malformed

                let! failedHealthResponse = client.GetAsync("/api/v0/telegram/health")
                use failedHealthResponse = failedHealthResponse
                Assert.That(failedHealthResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! failedHealth = jsonDocument failedHealthResponse
                use failedHealth = failedHealth
                let failedLastUpdateId = failedHealth.RootElement |> jsonProperty "lastUpdateId" |> fun value -> value.GetInt64()
                Assert.That(failedLastUpdateId, Is.EqualTo(firstUpdateId))
                Assert.That(failedHealth.RootElement |> stringProperty "lastError", Is.EqualTo("request.invalid"))

                let latestUpdateId = 9403L
                let! latest = sendWebhook client (typedRequestUpdate latestUpdateId "clear adapter error") [ WebhookSecret ]
                use latest = latest
                Assert.That(latest.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                let! older = sendWebhook client (typedRequestUpdate 9401L "older update") [ WebhookSecret ]
                use older = older
                Assert.That(older.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                let! recoveredHealthResponse = client.GetAsync("/api/v0/telegram/health")
                use recoveredHealthResponse = recoveredHealthResponse
                let! recoveredHealth = jsonDocument recoveredHealthResponse
                use recoveredHealth = recoveredHealth
                let recoveredLastUpdateId = recoveredHealth.RootElement |> jsonProperty "lastUpdateId" |> fun value -> value.GetInt64()
                Assert.That(recoveredLastUpdateId, Is.EqualTo(latestUpdateId))
                Assert.That(recoveredHealth.RootElement |> valueKindProperty "lastError", Is.EqualTo(JsonValueKind.Null))
            })

    [<Test>]
    let ``player events publish every required name with its exact state fragment after a deterministic cycle signal`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero)
        let delay = ControlledPlayerEventsDelay()

        withApiClientAndEventsDelay nowUtc delay (fun connectionString client ->
            task {
                do! seedAdminReadModels connectionString
                do! seedSseFragments connectionString nowUtc
                do! seedHeartbeat connectionString "Live" nowUtc

                use cancellation = new CancellationTokenSource()
                let! response =
                    client.GetAsync("/api/v0/player/events", HttpCompletionOption.ResponseHeadersRead, cancellation.Token)

                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("text/event-stream"))
                let! stream = response.Content.ReadAsStreamAsync(cancellation.Token)
                use stream = stream
                use reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true)

                let! initial = readSseEvent reader cancellation.Token
                Assert.That(initial.Name, Is.EqualTo("player.state"))
                use initialDocument = JsonDocument.Parse(initial.Data)
                let initialQueue = initialDocument.RootElement |> jsonProperty "queue"
                let initialItems = initialQueue |> jsonProperty "items"
                Assert.That(initialItems.GetArrayLength(), Is.EqualTo(1))
                Assert.That(initialItems.[0] |> stringProperty "title", Is.EqualTo("SSE queue track"))
                let initialMessages = initialDocument.RootElement |> jsonProperty "superChat" |> jsonProperty "messages"
                Assert.That(initialMessages.[0] |> stringProperty "text", Is.EqualTo("SSE moderation payload"))
                Assert.That(initialDocument.RootElement |> jsonProperty "donationGoal" |> stringProperty "title", Is.EqualTo("Keep Web10.Radio live"))

                delay.Advance()
                let! queue = readSseEvent reader cancellation.Token
                Assert.That(queue.Name, Is.EqualTo("player.queue"))
                use queueDocument = JsonDocument.Parse(queue.Data)
                let queueItems = queueDocument.RootElement |> jsonProperty "items"
                Assert.That(queueItems.[0] |> stringProperty "title", Is.EqualTo("SSE queue track"))

                let! say = readSseEvent reader cancellation.Token
                Assert.That(say.Name, Is.EqualTo("player.say"))
                use sayDocument = JsonDocument.Parse(say.Data)
                let sayMessages = sayDocument.RootElement |> jsonProperty "messages"
                Assert.That(sayMessages.[0] |> stringProperty "text", Is.EqualTo("SSE moderation payload"))

                let! donation = readSseEvent reader cancellation.Token
                Assert.That(donation.Name, Is.EqualTo("player.donation"))
                use donationDocument = JsonDocument.Parse(donation.Data)
                Assert.That(donationDocument.RootElement |> stringProperty "title", Is.EqualTo("Keep Web10.Radio live"))
                Assert.That(donationDocument.RootElement |> intProperty "raisedStars", Is.EqualTo(250))
                Assert.That(donationDocument.RootElement |> intProperty "goalStars", Is.EqualTo(1000))

                let! health = readSseEvent reader cancellation.Token
                Assert.That(health.Name, Is.EqualTo("player.health"))
                use healthDocument = JsonDocument.Parse(health.Data)
                Assert.That(healthDocument.RootElement |> stringProperty "status", Is.EqualTo("live"))
                Assert.That(healthDocument.RootElement |> intProperty "bitrateKbps", Is.EqualTo(192))
            })

    [<Test>]
    let ``route failure before response start writes problem and captures the exception with request identity`` () =
        task {
            let entries = ConcurrentQueue<CapturedLog>()
            let logger = CapturingLogger(entries) :> ILogger
            let context = DefaultHttpContext()
            context.TraceIdentifier <- "trace-before-response"
            context.Request.Headers["X-Correlation-Id"] <- "correlation-before-response"
            context.Response.Body <- new MemoryStream()

            let handler: Web10.Radio.API.ApiRouteHandler =
                fun _ -> Task.FromException<int>(InvalidOperationException("before response failure"))

            do! Web10.Radio.API.ApiEndpoints.execute logger "/test/before-response" handler context
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status500InternalServerError))
            let body = (context.Response.Body :?> MemoryStream).ToArray() |> Encoding.UTF8.GetString
            use document = JsonDocument.Parse(body)
            Assert.That(document.RootElement |> stringProperty "code", Is.EqualTo("api.unhandled"))

            let failure =
                entries
                |> Seq.tryFind (fun entry ->
                    entry.Level = LogLevel.Error
                    && (entry.Exception
                        |> Option.exists (fun error -> error :? InvalidOperationException && error.Message = "before response failure")))

            match failure with
            | None -> Assert.Fail("Expected ApiRouteFailed to capture the original exception.")
            | Some entry ->
                Assert.That(entry.Message.Contains("/test/before-response"), Is.True)
                Assert.That(entry.Message.Contains("500"), Is.True)
                Assert.That(entry.Message.Contains("trace-before-response"), Is.True)
                Assert.That(entry.Message.Contains("correlation-before-response"), Is.True)
        }

    [<Test>]
    let ``route failure after response start preserves wire response and captures original exception with wire status`` () =
        task {
            let entries = ConcurrentQueue<CapturedLog>()
            let logger = CapturingLogger(entries) :> ILogger
            let context = DefaultHttpContext()
            context.Response.Body <- new MemoryStream()
            let responseBody = context.Response.Body :?> MemoryStream
            let feature = StartedResponseFeature(418)
            context.Features.Set<IHttpResponseFeature>(feature)
            context.TraceIdentifier <- "trace-after-response"
            context.Request.Headers["X-Correlation-Id"] <- "correlation-after-response"
            let alreadyWritten = Encoding.UTF8.GetBytes("already-written")
            do! responseBody.WriteAsync(alreadyWritten)

            let handler: Web10.Radio.API.ApiRouteHandler =
                fun _ -> Task.FromException<int>(InvalidOperationException("after response failure"))

            do! Web10.Radio.API.ApiEndpoints.execute logger "/test/after-response" handler context
            Assert.That(context.Response.StatusCode, Is.EqualTo(418), "A started response cannot be replaced with a problem response.")
            Assert.That(responseBody.ToArray(), Is.EqualTo(alreadyWritten :> obj))

            let failure =
                entries
                |> Seq.tryFind (fun entry ->
                    entry.Level = LogLevel.Error
                    && (entry.Exception
                        |> Option.exists (fun error -> error :? InvalidOperationException && error.Message = "after response failure")))

            match failure with
            | None -> Assert.Fail("Expected ApiRouteFailed to capture the post-start exception.")
            | Some entry ->
                Assert.That(entry.Message.Contains("/test/after-response"), Is.True)
                Assert.That(entry.Message.Contains("418"), Is.True)
                Assert.That(entry.Message.Contains("trace-after-response"), Is.True)
                Assert.That(entry.Message.Contains("correlation-after-response"), Is.True)
        }

    [<Test>]
    let ``stream-node callbacks enforce bearer body and fence contracts then persist both terminal outcomes`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 22, 0, 0, TimeSpan.Zero)
        let callbackToken = "stream-callback-token-Secret_123456"

        withApiClientWithServices
            (fun services ->
                services.RemoveAll<IHostedService>() |> ignore
                services.RemoveAll<Web10.Radio.API.IClock>() |> ignore
                services.AddSingleton<Web10.Radio.API.IClock>(FixedClock(nowUtc) :> Web10.Radio.API.IClock) |> ignore)
            (fun connectionString _ _ client ->
                task {
                    let playedItemId, playedOwner = Guid.NewGuid(), Guid.NewGuid()
                    let failedItemId, failedOwner = Guid.NewGuid(), Guid.NewGuid()

                    use connection = new NpgsqlConnection(connectionString)
                    do! connection.OpenAsync()

                    let seedPlaying queueItemId owner =
                        task {
                            use command =
                                new NpgsqlCommand(
                                    """INSERT INTO "PlaybackQueue" ("Id", "Source", "Status", "Priority", "RequestedAtUtc", "ClaimedAtUtc", "StartedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc", "IsDeleted")
VALUES (@Id, 'admin', 'Playing', 0, @NowUtc, @NowUtc, @NowUtc, @Owner, 1, @LeaseExpiresAtUtc, false);""",
                                    connection
                                )

                            command.Parameters.AddWithValue("Id", queueItemId) |> ignore
                            command.Parameters.AddWithValue("Owner", owner) |> ignore
                            command.Parameters.AddWithValue("NowUtc", nowUtc) |> ignore
                            command.Parameters.AddWithValue("LeaseExpiresAtUtc", nowUtc.AddMinutes(1.0)) |> ignore
                            let! _ = command.ExecuteNonQueryAsync()
                            return ()
                        }

                    do! seedPlaying playedItemId playedOwner
                    do! seedPlaying failedItemId failedOwner

                    let playedPath = sprintf "/api/v0/stream-node/playback/%O" playedItemId
                    let leaseBody = sprintf "{\"claimOwner\":\"%O\",\"claimAttempt\":1}" playedOwner
                    let completionBody = sprintf "{\"claimOwner\":\"%O\",\"claimAttempt\":1,\"status\":\"played\"}" playedOwner
                    let endpoints = [ "/lease", leaseBody; "/completion", completionBody ]

                    let rejectedCredentials =
                        [ "missing", []
                          "wrong", [ "Bearer wrong-stream-node-token-Secret_123456" ]
                          "multiple", [ sprintf "Bearer %s" callbackToken; "Bearer another-stream-node-token-Secret_123456" ] ]

                    for suffix, body in endpoints do
                        for credentialName, authorizations in rejectedCredentials do
                            let! rejected = sendPlaybackCallback client (playedPath + suffix) body authorizations
                            use rejected = rejected

                            Assert.That(
                                rejected.StatusCode,
                                Is.EqualTo(HttpStatusCode.Unauthorized),
                                sprintf "%s callback with %s credentials" suffix credentialName
                            )

                            Assert.That(
                                rejected.Headers.GetValues("WWW-Authenticate") |> String.concat ",",
                                Is.EqualTo("Bearer"),
                                sprintf "%s callback must issue the Bearer challenge for %s credentials" suffix credentialName
                            )

                            do! assertProblemCode "stream-node.auth.required" rejected

                        let! malformed = sendPlaybackCallback client (playedPath + suffix) "{" [ sprintf "Bearer %s" callbackToken ]
                        use malformed = malformed
                        Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), sprintf "%s rejects malformed JSON" suffix)
                        do! assertProblemCode "request.invalid" malformed

                        let! oversized =
                            sendPlaybackCallback
                                client
                                (playedPath + suffix)
                                (String.replicate 4097 "x")
                                [ sprintf "Bearer %s" callbackToken ]

                        use oversized = oversized
                        Assert.That(oversized.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge), sprintf "%s rejects an oversized body" suffix)
                        do! assertProblemCode "request.too_large" oversized

                    let! renewed = sendPlaybackCallback client (playedPath + "/lease") leaseBody [ sprintf "Bearer %s" callbackToken ]
                    use renewed = renewed
                    Assert.That(renewed.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "The active playing claim renews its lease.")

                    let! staleOwner =
                        sendPlaybackCallback
                            client
                            (playedPath + "/lease")
                            (sprintf "{\"claimOwner\":\"%O\",\"claimAttempt\":1}" (Guid.NewGuid()))
                            [ sprintf "Bearer %s" callbackToken ]

                    use staleOwner = staleOwner
                    Assert.That(staleOwner.StatusCode, Is.EqualTo(HttpStatusCode.Conflict))
                    do! assertProblemCode "playback.claim_stale" staleOwner

                    let! staleAttempt =
                        sendPlaybackCallback
                            client
                            (playedPath + "/completion")
                            (sprintf "{\"claimOwner\":\"%O\",\"claimAttempt\":2,\"status\":\"played\"}" playedOwner)
                            [ sprintf "Bearer %s" callbackToken ]

                    use staleAttempt = staleAttempt
                    Assert.That(staleAttempt.StatusCode, Is.EqualTo(HttpStatusCode.Conflict))
                    do! assertProblemCode "playback.claim_stale" staleAttempt

                    let! played =
                        sendPlaybackCallback client (playedPath + "/completion") completionBody [ sprintf "Bearer %s" callbackToken ]

                    use played = played
                    Assert.That(played.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                    let failedPath = sprintf "/api/v0/stream-node/playback/%O/completion" failedItemId

                    let! failed =
                        sendPlaybackCallback
                            client
                            failedPath
                            (sprintf "{\"claimOwner\":\"%O\",\"claimAttempt\":1,\"status\":\"failed\",\"failureReason\":\"decoder exited\"}" failedOwner)
                            [ sprintf "Bearer %s" callbackToken ]

                    use failed = failed
                    Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                    use check =
                        new NpgsqlCommand(
                            """SELECT
    (SELECT "Status" FROM "PlaybackQueue" WHERE "Id" = @PlayedItemId),
    (SELECT "FinishedAtUtc" IS NOT NULL FROM "PlaybackQueue" WHERE "Id" = @PlayedItemId),
    (SELECT "ClaimOwner" IS NULL FROM "PlaybackQueue" WHERE "Id" = @PlayedItemId),
    (SELECT "Status" FROM "PlaybackQueue" WHERE "Id" = @FailedItemId),
    (SELECT "FailureReason" FROM "PlaybackQueue" WHERE "Id" = @FailedItemId),
    (SELECT "FinishedAtUtc" IS NOT NULL FROM "PlaybackQueue" WHERE "Id" = @FailedItemId),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'PlaybackEnded' AND "Status" = 'Pending' AND "Payload" ->> 'queueItemId' = @PlayedItemIdText AND "Payload" ->> 'status' = 'played'),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'PlaybackEnded' AND "Status" = 'Pending' AND "Payload" ->> 'queueItemId' = @FailedItemIdText AND "Payload" ->> 'status' = 'failed' AND "Payload" ->> 'failureReason' = 'decoder exited');""",
                            connection
                        )

                    check.Parameters.AddWithValue("PlayedItemId", playedItemId) |> ignore
                    check.Parameters.AddWithValue("FailedItemId", failedItemId) |> ignore
                    check.Parameters.AddWithValue("PlayedItemIdText", string playedItemId) |> ignore
                    check.Parameters.AddWithValue("FailedItemIdText", string failedItemId) |> ignore
                    let! reader = check.ExecuteReaderAsync()
                    use reader = reader
                    let! hasRow = reader.ReadAsync()
                    Assert.That(hasRow, Is.True)
                    Assert.That(reader.GetString(0), Is.EqualTo("Played"))
                    Assert.That(reader.GetBoolean(1), Is.True)
                    Assert.That(reader.GetBoolean(2), Is.True)
                    Assert.That(reader.GetString(3), Is.EqualTo("Failed"))
                    Assert.That(reader.GetString(4), Is.EqualTo("decoder exited"))
                    Assert.That(reader.GetBoolean(5), Is.True)
                    Assert.That(reader.GetInt64(6), Is.EqualTo(1L), "Successful completion appends one pending PlaybackEnded event.")
                    Assert.That(reader.GetInt64(7), Is.EqualTo(1L), "Failed completion appends one pending PlaybackEnded event.")
                })

    [<Test>]
    let ``duplicate request and say webhooks relay exactly one domain row each`` () =
        withApiClientWithServices
            (fun services ->
                services.RemoveAll<IHostedService>() |> ignore
                services.AddSingleton<Web10.Radio.API.OutboxRelayHostedService>() |> ignore)
            (fun connectionString _ factory client ->
                task {
                    let relay = factory.Services.GetRequiredService<Web10.Radio.API.OutboxRelayHostedService>()

                    let processOne eventName =
                        task {
                            let! result = relay.ProcessDueEventsOnceAsync(CancellationToken.None)

                            match result with
                            | Ok 1 -> return ()
                            | Ok count -> return raise (AssertionException(sprintf "Expected one relayed %s event, got %d." eventName count))
                            | Error error -> return raise (AssertionException(sprintf "Relaying %s failed: %O" eventName error))
                        }

                    let requestUpdateId = 9501L
                    let requestQuery = "one exact request row"
                    let requestBody = typedRequestUpdate requestUpdateId requestQuery
                    let! firstRequest = sendWebhook client requestBody [ WebhookSecret ]
                    use firstRequest = firstRequest
                    Assert.That(firstRequest.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                    let! duplicateRequest = sendWebhook client requestBody [ WebhookSecret ]
                    use duplicateRequest = duplicateRequest
                    Assert.That(duplicateRequest.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                    do! processOne "TrackRequested"
                    let! requestRows = countTrackRequestRows connectionString requestQuery
                    let! requestOutboxRows = countOutboxRows connectionString "TrackRequested"
                    Assert.That(requestRows, Is.EqualTo(1), "Duplicate /request delivery must create one TrackRequests row after relay.")
                    Assert.That(requestOutboxRows, Is.EqualTo(1), "Duplicate /request delivery must relay one TrackRequested event.")

                    let sayUpdateId = 9502L
                    let sayText = "one exact say row"
                    let sayBody = typedSayUpdate sayUpdateId sayText
                    let! firstSay = sendWebhook client sayBody [ WebhookSecret ]
                    use firstSay = firstSay
                    Assert.That(firstSay.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                    let! duplicateSay = sendWebhook client sayBody [ WebhookSecret ]
                    use duplicateSay = duplicateSay
                    Assert.That(duplicateSay.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                    do! processOne "SayMessageSubmitted"
                    let! sayRows = countSayMessageRows connectionString sayText
                    let! sayOutboxRows = countOutboxRows connectionString "SayMessageSubmitted"
                    Assert.That(sayRows, Is.EqualTo(1), "Duplicate /say delivery must create one SayMessages row after relay.")
                    Assert.That(sayOutboxRows, Is.EqualTo(1), "Duplicate /say delivery must relay one SayMessageSubmitted event.")
                })

    [<Test>]
    let ``duplicate successful payment webhook relays once and persists one paid charge`` () =
        withApiClientWithServices
            (fun services ->
                services.RemoveAll<IHostedService>() |> ignore
                services.AddSingleton<Web10.Radio.API.OutboxRelayHostedService>() |> ignore)
            (fun connectionString _ factory client ->
                task {
                    let relay = factory.Services.GetRequiredService<Web10.Radio.API.OutboxRelayHostedService>()
                    let paymentId = Guid.NewGuid()
                    let chargeId = "telegram-charge-idempotent-9503"
                    do! seedDonationPayment connectionString paymentId 42

                    let paymentBody = typedSuccessfulPaymentUpdate 9503L paymentId chargeId 42
                    let! firstPayment = sendWebhook client paymentBody [ WebhookSecret ]
                    use firstPayment = firstPayment
                    Assert.That(firstPayment.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                    let! duplicatePayment = sendWebhook client paymentBody [ WebhookSecret ]
                    use duplicatePayment = duplicatePayment
                    Assert.That(duplicatePayment.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                    let! firstRelay = relay.ProcessDueEventsOnceAsync(CancellationToken.None)

                    match firstRelay with
                    | Ok 1 -> ()
                    | Ok count -> Assert.Fail(sprintf "Expected one relayed DonationPaid event, got %d." count)
                    | Error error -> Assert.Fail(sprintf "Relaying DonationPaid failed: %O" error)

                    let! secondRelay = relay.ProcessDueEventsOnceAsync(CancellationToken.None)

                    match secondRelay with
                    | Ok 0 -> ()
                    | Ok count -> Assert.Fail(sprintf "Duplicate successful_payment created %d additional relay work items." count)
                    | Error error -> Assert.Fail(sprintf "Relaying duplicate DonationPaid failed: %O" error)

                    use check =
                        new NpgsqlCommand(
                            """SELECT "Status", "TelegramPaymentChargeId", "AmountStars", "Currency", "PaidAtUtc" IS NOT NULL,
    (SELECT count(*) FROM "Payments" WHERE "Id" = @PaymentId AND "IsDeleted" = false)
FROM "Payments"
WHERE "Id" = @PaymentId AND "IsDeleted" = false;""",
                            new NpgsqlConnection(connectionString)
                        )

                    do! check.Connection.OpenAsync()
                    check.Parameters.AddWithValue("PaymentId", paymentId) |> ignore
                    let! reader = check.ExecuteReaderAsync()
                    use reader = reader
                    let! hasRow = reader.ReadAsync()
                    Assert.That(hasRow, Is.True)
                    Assert.That(reader.GetString(0), Is.EqualTo("Paid"))
                    Assert.That(reader.GetString(1), Is.EqualTo(chargeId))
                    Assert.That(reader.GetInt32(2), Is.EqualTo(42))
                    Assert.That(reader.GetString(3), Is.EqualTo("XTR"))
                    Assert.That(reader.GetBoolean(4), Is.True)
                    Assert.That(reader.GetInt64(5), Is.EqualTo(1L), "Duplicate successful_payment must preserve a single Payment row.")

                    let! paymentOutboxRows = countOutboxRows connectionString "DonationPaid"
                    Assert.That(paymentOutboxRows, Is.EqualTo(1), "Duplicate successful_payment must append one DonationPaid event.")
                })

    [<Test>]
    let ``live cached metadata whose file disappears returns stream unavailable instead of an internal error`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 23, 0, 0, TimeSpan.Zero)

        withApiClientAt nowUtc (fun connectionString tempRoot client ->
            task {
                do! seedHeartbeat connectionString "Live" nowUtc
                let! _ = seedCachedPlayingTrack connectionString tempRoot nowUtc
                File.Delete(Path.Combine(tempRoot, "live-track.mp3"))

                let! response = client.GetAsync("/api/v0/player/stream")
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable))
                do! assertProblemCode "stream.unavailable" response
            })

    [<Test>]
    let ``health live stays reachable and ready reports every operational check healthy`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 23, 30, 0, TimeSpan.Zero)

        withApiClientWithServices
            (fun services ->
                services.RemoveAll<Web10.Radio.API.ITelegramIdentityProbe>() |> ignore
                services.AddSingleton<Web10.Radio.API.ITelegramIdentityProbe>(AuthenticatedTelegramIdentityProbe()) |> ignore
                services.RemoveAll<Web10.Radio.API.IClock>() |> ignore
                services.AddSingleton<Web10.Radio.API.IClock>(FixedClock(nowUtc) :> Web10.Radio.API.IClock) |> ignore)
            (fun connectionString tempRoot _ client ->
                task {
                    Directory.CreateDirectory(Path.Combine(tempRoot, "library")) |> ignore
                    do! seedHeartbeat connectionString "Live" nowUtc

                    let! liveResponse = client.GetAsync("/health/live")
                    use liveResponse = liveResponse
                    Assert.That(liveResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                    let! readyResponse = client.GetAsync("/health/ready")
                    use readyResponse = readyResponse
                    Assert.That(readyResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    let! ready = jsonDocument readyResponse
                    use ready = ready
                    Assert.That(ready.RootElement |> stringProperty "status", Is.EqualTo("Healthy"))

                    let checks = ready.RootElement |> jsonProperty "checks"

                    let expectedChecks =
                        [ "api", "Healthy"
                          "telegram-adapter", "Healthy"
                          "postgresql", "Healthy"
                          "storage", "Healthy"
                          "stream-node-heartbeat", "Healthy" ]

                    Assert.That(checks.GetArrayLength(), Is.EqualTo(expectedChecks.Length))

                    for name, expectedStatus in expectedChecks do
                        let check =
                            checks.EnumerateArray()
                            |> Seq.tryFind (fun item -> item |> stringProperty "name" = name)

                        match check with
                        | Some item -> Assert.That(item |> stringProperty "status", Is.EqualTo(expectedStatus), name)
                        | None -> Assert.Fail(sprintf "Readiness response omitted required %s check." name)
                })
