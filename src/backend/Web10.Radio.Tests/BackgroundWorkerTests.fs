namespace Web10.Radio.Tests

open System
open System.IO
open System.Threading
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Database
open Web10.Radio.Database.Repositories
open Web10.Radio.Telegram

module BackgroundWorkerTests =
    type private FixedClock(nowUtc: DateTimeOffset) =
        interface IClock with
            member _.UtcNow = nowUtc

    let private newId () =
        (UuidV7IdGenerator() :> IIdGenerator).NewId()

    let private idGenerator () =
        UuidV7IdGenerator() :> IIdGenerator

    let private clockAt value =
        FixedClock(value) :> IClock

    let private createDispatcher dataSource idGenerator clock streamNodeState =
        DomainEventDispatcher(
            dataSource,
            idGenerator,
            clock,
            streamNodeState,
            NullLogger<DomainEventDispatcher>.Instance
        )
        :> IDomainEventDispatcher

    let private createPublisher dataSource dispatcher clock =
        DomainEventPublisher(dataSource, dispatcher, clock, NullLogger<DomainEventPublisher>.Instance) :> IDomainEventPublisher

    type private ObservingFailingDispatcher(dataSource: NpgsqlDataSource) =
        let mutable observedPersistedRow = false

        member _.ObservedPersistedRow = observedPersistedRow

        interface IDomainEventDispatcher with
            member _.DispatchAsync envelope cancellationToken =
                task {
                    use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                    use command =
                        new NpgsqlCommand(
                            """SELECT count(*)
FROM "OutboxEvents"
WHERE "Id" = @EventId
  AND "Status" = 'Pending'
  AND "IsDeleted" = false;""",
                            connection
                        )

                    command.Parameters.AddWithValue("EventId", envelope.EventId) |> ignore
                    let! count = command.ExecuteScalarAsync(cancellationToken)
                    observedPersistedRow <- (count :?> int64) = 1L
                    return Error(InvalidPayload(DomainEventType.toString envelope.EventType, "forced dispatcher failure"))
                }

    let private testOptions connectionString localRoot =
        { Postgres = { ConnectionString = connectionString }
          Telegram =
            { BotToken = "test-bot-token"
              WebhookSecret = "test-webhook-secret"
              ChannelIdOrUsername = "@web10_test" }
          Stream =
            { RtmpUrl = Uri("rtmp://localhost/live")
              RtmpKey = "test-rtmp-key"
              StageUrl = Uri("http://localhost/stage") }
          Storage =
            { Type = Local
              LocalRoot = localRoot
              S3Bucket = "unused-test-bucket" }
          Otel = { ExporterOtlpEndpoint = Uri("http://localhost:4317") }
          DataProtection = { KeyRingPath = "/tmp/web10-radio-tests-keys" } }

    let private addParameter (name: string) (value: obj) (command: NpgsqlCommand) =
        command.Parameters.AddWithValue(name, value) |> ignore

    let private assertOkTrue description result =
        match result with
        | Ok true -> ()
        | actual -> Assert.Fail(sprintf "Expected %s to return Ok true, but got %A." description actual)

    let private assertOkFalse description result =
        match result with
        | Ok false -> ()
        | actual -> Assert.Fail(sprintf "Expected %s to return Ok false, but got %A." description actual)

    [<Test>]
    let ``Telegram update ingestor appends by update and event type pair and suppresses duplicate pair`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let generator = idGenerator ()
                let clock = clockAt (DateTimeOffset(2026, 7, 8, 16, 0, 0, TimeSpan.Zero))
                let streamNodeState = StreamNodeHeartbeatState()
                let dispatcher = createDispatcher dataSource generator clock streamNodeState
                let publisher = createPublisher dataSource dispatcher clock
                let ingestor =
                    TelegramUpdateEventIngestor(
                        dataSource,
                        generator,
                        clock,
                        publisher,
                        NullLogger<TelegramUpdateEventIngestor>.Instance
                    )
                    :> ITelegramUpdateEventIngestor

                let trackRequestPayloadJson = "{\"query\":\"Artist - Title\"}"
                let sayMessagePayloadJson = "{\"text\":\"hello\"}"
                let! firstResult =
                    ingestor.TryIngestAsync 9001L DomainEventType.TrackRequested "web10.radio.tests" trackRequestPayloadJson CancellationToken.None

                let! duplicateTrackRequestResult =
                    ingestor.TryIngestAsync 9001L DomainEventType.TrackRequested "web10.radio.tests" trackRequestPayloadJson CancellationToken.None

                let! differentEventTypeResult =
                    ingestor.TryIngestAsync 9001L DomainEventType.SayMessageSubmitted "web10.radio.tests" sayMessagePayloadJson CancellationToken.None

                let! duplicateSayMessageResult =
                    ingestor.TryIngestAsync 9001L DomainEventType.SayMessageSubmitted "web10.radio.tests" sayMessagePayloadJson CancellationToken.None

                assertOkTrue "first Telegram update ingest" firstResult
                assertOkFalse "duplicate Telegram update ingest for same update id and event type" duplicateTrackRequestResult
                assertOkTrue "Telegram update ingest with same update id and different event type" differentEventTypeResult
                assertOkFalse "duplicate Telegram update ingest for second event type" duplicateSayMessageResult

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use command =
                    new NpgsqlCommand(
                        """SELECT
    (SELECT count(*) FROM "TelegramUpdateInbox" WHERE "TelegramUpdateId" = @TelegramUpdateId AND "EventType" = 'TrackRequested' AND "IsDeleted" = false),
    (SELECT count(*) FROM "TelegramUpdateInbox" WHERE "TelegramUpdateId" = @TelegramUpdateId AND "EventType" = 'SayMessageSubmitted' AND "IsDeleted" = false),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'TrackRequested' AND "Status" = 'Processed' AND "IsDeleted" = false),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'SayMessageSubmitted' AND "Status" = 'Processed' AND "IsDeleted" = false);""",
                        connection
                    )

                addParameter "TelegramUpdateId" 9001L command
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetInt64(0), Is.EqualTo(1L))
                Assert.That(reader.GetInt64(1), Is.EqualTo(1L))
                Assert.That(reader.GetInt64(2), Is.EqualTo(1L))
                Assert.That(reader.GetInt64(3), Is.EqualTo(1L))
            })

    [<Test>]
    let ``local library scan imports Artist Title mp3 emits TrackDiscovered and completes job`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let tempRoot = Directory.CreateTempSubdirectory("web10-radio-library-scan-")
                let filePath = Path.Combine(tempRoot.FullName, "Artist - Title.mp3")

                try
                    File.WriteAllBytes(filePath, [| 0uy; 1uy; 2uy; 3uy |])
                    let jobId = newId ()
                    let requestedAtUtc = DateTimeOffset(2026, 7, 8, 17, 0, 0, TimeSpan.Zero)
                    let processedAtUtc = requestedAtUtc.AddMinutes(1.0)

                    use connection = new NpgsqlConnection(connectionString)
                    do! connection.OpenAsync()
                    use insertJobCommand =
                        new NpgsqlCommand(
                            """INSERT INTO "LibraryScanJobs" ("Id", "Status", "RequestedAtUtc", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES (@JobId, 'Queued', @RequestedAtUtc, @RequestedAtUtc, @RequestedAtUtc, false);""",
                            connection
                        )

                    addParameter "JobId" jobId insertJobCommand
                    addParameter "RequestedAtUtc" requestedAtUtc insertJobCommand
                    let! _ = insertJobCommand.ExecuteNonQueryAsync()

                    use dataSource = NpgsqlDataSource.Create(connectionString)
                    let generator = idGenerator ()
                    let clock = clockAt processedAtUtc
                    let streamNodeState = StreamNodeHeartbeatState()
                    let dispatcher = createDispatcher dataSource generator clock streamNodeState
                    let publisher = createPublisher dataSource dispatcher clock
                    let service =
                        new LibraryScanHostedService(
                            dataSource,
                            publisher,
                            generator,
                            clock,
                            testOptions connectionString tempRoot.FullName,
                            NullLogger<LibraryScanHostedService>.Instance
                        )

                    let! processResult = service.ProcessOneJobAsync(CancellationToken.None)
                    assertOkTrue "local library scan job processing" processResult

                    use assertCommand =
                        new NpgsqlCommand(
                            """SELECT
    (SELECT count(*) FROM "Tracks" WHERE "Artist" = 'Artist' AND "Title" = 'Title' AND "IsDeleted" = false),
    (SELECT count(*)
     FROM "TrackFiles" tf
     INNER JOIN "Tracks" t ON t."Id" = tf."TrackId"
     WHERE t."Artist" = 'Artist'
       AND t."Title" = 'Title'
       AND tf."StorageBackendId" IS NULL
       AND tf."StoragePath" = @FilePath
       AND tf."CachePath" = @FilePath
       AND tf."IsCached" = true
       AND tf."ContentType" = 'audio/mpeg'
       AND tf."SizeBytes" = @SizeBytes
       AND tf."IsDeleted" = false),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'TrackDiscovered' AND "IsDeleted" = false),
    (SELECT "Status" FROM "LibraryScanJobs" WHERE "Id" = @JobId AND "IsDeleted" = false),
    (SELECT "FinishedAtUtc" FROM "LibraryScanJobs" WHERE "Id" = @JobId AND "IsDeleted" = false);""",
                            connection
                        )

                    addParameter "FilePath" filePath assertCommand
                    addParameter "SizeBytes" (FileInfo(filePath).Length) assertCommand
                    addParameter "JobId" jobId assertCommand
                    let! reader = assertCommand.ExecuteReaderAsync()
                    use reader = reader
                    let! hasRow = reader.ReadAsync()
                    Assert.That(hasRow, Is.True)
                    Assert.That(reader.GetInt64(0), Is.EqualTo(1L))
                    Assert.That(reader.GetInt64(1), Is.EqualTo(1L))
                    Assert.That(reader.GetInt64(2), Is.EqualTo(1L))
                    Assert.That(reader.GetString(3), Is.EqualTo("Completed"))
                    Assert.That(reader.GetFieldValue<DateTimeOffset>(4), Is.EqualTo(processedAtUtc))
                finally
                    tempRoot.Delete(true)
            })

    [<Test>]
    let ``playback cache miss fails queue emits stream failure and records degraded heartbeat`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let queueItemId = newId ()
                let requestedAtUtc = DateTimeOffset(2026, 7, 8, 18, 0, 0, TimeSpan.Zero)
                let processedAtUtc = requestedAtUtc.AddSeconds(30.0)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES (@TrackId, 'Uncached title', 'Uncached artist', @RequestedAtUtc, @RequestedAtUtc, false);

INSERT INTO "PlaybackQueue" (
    "Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted"
)
VALUES (@QueueItemId, @TrackId, 'playlist', 'Queued', 0, @RequestedAtUtc, @RequestedAtUtc, @RequestedAtUtc, false);""",
                        connection
                    )

                addParameter "TrackId" trackId insertCommand
                addParameter "QueueItemId" queueItemId insertCommand
                addParameter "RequestedAtUtc" requestedAtUtc insertCommand
                let! _ = insertCommand.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let generator = idGenerator ()
                let clock = clockAt processedAtUtc
                let streamNodeState = StreamNodeHeartbeatState()
                let dispatcher = createDispatcher dataSource generator clock streamNodeState
                let publisher = createPublisher dataSource dispatcher clock
                let service =
                    new PlaybackProgramHostedService(
                        dataSource,
                        publisher,
                        generator,
                        clock,
                        NullLogger<PlaybackProgramHostedService>.Instance
                    )

                let! processResult = service.ProcessOneQueueItemAsync(CancellationToken.None)
                assertOkTrue "playback cache-miss processing" processResult

                use assertCommand =
                    new NpgsqlCommand(
                        """SELECT
    (SELECT "Status" FROM "PlaybackQueue" WHERE "Id" = @QueueItemId AND "IsDeleted" = false),
    (SELECT "FailureReason" FROM "PlaybackQueue" WHERE "Id" = @QueueItemId AND "IsDeleted" = false),
    (SELECT count(*) FROM "StreamNodeHeartbeats" WHERE "Status" = 'Degraded' AND "FailureReason" = 'cache path unavailable' AND "IsDeleted" = false),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'StreamNodeFailureDetected' AND "IsDeleted" = false);""",
                        connection
                    )

                addParameter "QueueItemId" queueItemId assertCommand
                let! reader = assertCommand.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Failed"))
                Assert.That(reader.GetString(1), Is.EqualTo("cache path unavailable"))
                Assert.That(reader.GetInt64(2), Is.EqualTo(1L))
                Assert.That(reader.GetInt64(3), Is.EqualTo(1L))
                Assert.That(streamNodeState.LastHeartbeatUtc, Is.EqualTo(Some processedAtUtc))
                Assert.That(streamNodeState.LastFailure, Is.EqualTo(Some "cache path unavailable"))
            })

    [<Test>]
    let ``outbox relay dispatches valid PlaybackStarted event and marks it processed`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let generator = idGenerator ()
                let occurredAtUtc = DateTimeOffset(2026, 7, 8, 19, 0, 0, TimeSpan.Zero)
                let processedAtUtc = occurredAtUtc.AddSeconds(5.0)
                let eventId = newId ()
                let queueItemId = newId ()
                let trackId = newId ()
                let payloadJson =
                    sprintf
                        "{\"queueItemId\":\"%O\",\"trackId\":\"%O\",\"cachePath\":\"/cache/artist-title.mp3\"}"
                        queueItemId
                        trackId

                let! appendResult =
                    OutboxEventRepository.append
                        dataSource
                        { Id = eventId
                          EventType = "PlaybackStarted"
                          OccurredAtUtc = occurredAtUtc
                          Producer = "web10.radio.tests"
                          CorrelationId = Some(newId ())
                          CausationId = None
                          PayloadJson = payloadJson }
                        CancellationToken.None

                match appendResult with
                | Ok () -> ()
                | actual -> Assert.Fail(sprintf "Expected outbox append to succeed, but got %A." actual)

                let clock = clockAt processedAtUtc
                let streamNodeState = StreamNodeHeartbeatState()
                let dispatcher = createDispatcher dataSource generator clock streamNodeState
                let services = ServiceCollection()
                services.AddSingleton<NpgsqlDataSource>(dataSource) |> ignore
                services.AddSingleton<IClock>(clock) |> ignore
                services.AddSingleton<IIdGenerator>(generator) |> ignore
                services.AddSingleton<StreamNodeHeartbeatState>(streamNodeState) |> ignore
                services.AddSingleton<IDomainEventDispatcher>(dispatcher) |> ignore
                use provider = services.BuildServiceProvider()
                let relay =
                    new OutboxRelayHostedService(
                        provider.GetRequiredService<IServiceScopeFactory>(),
                        NullLogger<OutboxRelayHostedService>.Instance
                    )

                let! relayResult = relay.ProcessDueEventsOnceAsync(CancellationToken.None)
                match relayResult with
                | Ok 1 -> ()
                | actual -> Assert.Fail(sprintf "Expected outbox relay to process one event, but got %A." actual)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use command =
                    new NpgsqlCommand(
                        """SELECT "Status", "Attempts", "ProcessedAtUtc", "UpdatedAtUtc"
FROM "OutboxEvents"
WHERE "Id" = @EventId
  AND "IsDeleted" = false;""",
                        connection
                    )

                addParameter "EventId" eventId command
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Processed"))
                Assert.That(reader.GetInt32(1), Is.EqualTo(1))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(2), Is.EqualTo(processedAtUtc))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(3), Is.EqualTo(processedAtUtc))
            })

    [<Test>]
    let ``invalid DonationPaid payload returns InvalidPayload without mutating payments`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let generator = idGenerator ()
                let clock = clockAt (DateTimeOffset(2026, 7, 8, 20, 0, 0, TimeSpan.Zero))
                let streamNodeState = StreamNodeHeartbeatState()
                let dispatcher = createDispatcher dataSource generator clock streamNodeState

                let envelopeResult =
                    DomainEventEnvelope.create generator clock DomainEventType.DonationPaid "web10.radio.tests" None None "{}"

                let envelope =
                    match envelopeResult with
                    | Ok envelope -> envelope
                    | Error error -> Assert.Fail(sprintf "Expected envelope creation to accept object payload, but got %A." error); Unchecked.defaultof<_>

                let! dispatchResult = dispatcher.DispatchAsync envelope CancellationToken.None
                match dispatchResult with
                | Error(InvalidPayload("DonationPaid", message)) -> Assert.That(message, Is.EqualTo("paymentId is required."))
                | actual -> Assert.Fail(sprintf "Expected invalid DonationPaid payload error, but got %A." actual)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use command = new NpgsqlCommand("""SELECT count(*) FROM "Payments" WHERE "IsDeleted" = false;""", connection)
                let! paymentCount = command.ExecuteScalarAsync()
                Assert.That(paymentCount :?> int64, Is.EqualTo(0L))
            })

    [<Test>]
    let ``bad stream-node heartbeat status flows through dispatcher as repository error without recording heartbeat`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let generator = idGenerator ()
                let heartbeatAtUtc = DateTimeOffset(2026, 7, 8, 21, 0, 0, TimeSpan.Zero)
                let clock = clockAt heartbeatAtUtc
                let streamNodeState = StreamNodeHeartbeatState()
                let dispatcher = createDispatcher dataSource generator clock streamNodeState
                let payloadJson = "{\"status\":\"Exploded\",\"metadata\":{\"node\":\"test\"}}"

                let envelopeResult =
                    DomainEventEnvelope.create
                        generator
                        clock
                        DomainEventType.StreamNodeHeartbeatReceived
                        "web10.radio.tests"
                        None
                        None
                        payloadJson

                let envelope =
                    match envelopeResult with
                    | Ok envelope -> envelope
                    | Error error -> Assert.Fail(sprintf "Expected envelope creation to accept object payload, but got %A." error); Unchecked.defaultof<_>

                let! dispatchResult = dispatcher.DispatchAsync envelope CancellationToken.None
                match dispatchResult with
                | Error(RepositoryError(InvalidStreamNodeStatus "Exploded")) -> ()
                | actual -> Assert.Fail(sprintf "Expected invalid stream-node status repository error, but got %A." actual)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use command = new NpgsqlCommand("""SELECT count(*) FROM "StreamNodeHeartbeats" WHERE "IsDeleted" = false;""", connection)
                let! heartbeatCount = command.ExecuteScalarAsync()
                Assert.That(heartbeatCount :?> int64, Is.EqualTo(0L))
                Assert.That(streamNodeState.LastHeartbeatUtc, Is.EqualTo(None))
                Assert.That(streamNodeState.LastFailure, Is.EqualTo(None))
            })

    [<Test>]
    let ``outbox relay keeps processing batch after unknown event type and leaves no claimed tail stranded`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let generator = idGenerator ()
                let occurredAtUtc = DateTimeOffset(2026, 7, 8, 22, 0, 0, TimeSpan.Zero)
                let processedAtUtc = occurredAtUtc.AddSeconds(5.0)
                let unknownEventId = newId ()
                let heartbeatEventId = newId ()

                for eventToAppend in
                    [ { Id = unknownEventId
                        EventType = "UnexpectedB2Event"
                        OccurredAtUtc = occurredAtUtc
                        Producer = "web10.radio.tests"
                        CorrelationId = Some(newId ())
                        CausationId = None
                        PayloadJson = "{}" }
                      { Id = heartbeatEventId
                        EventType = "StreamNodeHeartbeatReceived"
                        OccurredAtUtc = occurredAtUtc.AddMilliseconds(1.0)
                        Producer = "web10.radio.tests"
                        CorrelationId = Some(newId ())
                        CausationId = None
                        PayloadJson = "{\"status\":\"Live\",\"metadata\":{\"node\":\"relay-test\"}}" } ] do
                    let! appendResult = OutboxEventRepository.append dataSource eventToAppend CancellationToken.None
                    match appendResult with
                    | Ok () -> ()
                    | actual -> Assert.Fail(sprintf "Expected outbox append to succeed, but got %A." actual)

                let clock = clockAt processedAtUtc
                let streamNodeState = StreamNodeHeartbeatState()
                let dispatcher = createDispatcher dataSource generator clock streamNodeState
                let services = ServiceCollection()
                services.AddSingleton<NpgsqlDataSource>(dataSource) |> ignore
                services.AddSingleton<IClock>(clock) |> ignore
                services.AddSingleton<IIdGenerator>(generator) |> ignore
                services.AddSingleton<StreamNodeHeartbeatState>(streamNodeState) |> ignore
                services.AddSingleton<IDomainEventDispatcher>(dispatcher) |> ignore
                use provider = services.BuildServiceProvider()
                let relay =
                    new OutboxRelayHostedService(
                        provider.GetRequiredService<IServiceScopeFactory>(),
                        NullLogger<OutboxRelayHostedService>.Instance
                    )

                let! relayResult = relay.ProcessDueEventsOnceAsync(CancellationToken.None)
                match relayResult with
                | Error(UnknownEventType "UnexpectedB2Event") -> ()
                | actual -> Assert.Fail(sprintf "Expected unknown event type error after processing full batch, but got %A." actual)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use command =
                    new NpgsqlCommand(
                        """SELECT
    (SELECT "Status" FROM "OutboxEvents" WHERE "Id" = @UnknownEventId AND "IsDeleted" = false),
    (SELECT "Status" FROM "OutboxEvents" WHERE "Id" = @HeartbeatEventId AND "IsDeleted" = false),
    (SELECT count(*) FROM "OutboxEvents" WHERE "Status" = 'Processing' AND "IsDeleted" = false),
    (SELECT count(*) FROM "StreamNodeHeartbeats" WHERE "Status" = 'Live' AND "IsDeleted" = false);""",
                        connection
                    )

                addParameter "UnknownEventId" unknownEventId command
                addParameter "HeartbeatEventId" heartbeatEventId command
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Failed"))
                Assert.That(reader.GetString(1), Is.EqualTo("Processed"))
                Assert.That(reader.GetInt64(2), Is.EqualTo(0L))
                Assert.That(reader.GetInt64(3), Is.EqualTo(1L))
            })

    [<Test>]
    let ``PublishDurableAsync persists outbox row before dispatcher failure and marks it retryable`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let generator = idGenerator ()
                let nowUtc = DateTimeOffset(2026, 7, 8, 23, 0, 0, TimeSpan.Zero)
                let clock = clockAt nowUtc
                let dispatcher = ObservingFailingDispatcher(dataSource)
                let publisher =
                    DomainEventPublisher(
                        dataSource,
                        dispatcher :> IDomainEventDispatcher,
                        clock,
                        NullLogger<DomainEventPublisher>.Instance
                    )
                    :> IDomainEventPublisher

                let envelopeResult =
                    DomainEventEnvelope.create generator clock DomainEventType.TrackRequested "web10.radio.tests" None None "{\"query\":\"Artist - Title\"}"

                let envelope =
                    match envelopeResult with
                    | Ok envelope -> envelope
                    | Error error -> Assert.Fail(sprintf "Expected valid envelope, but got %A." error); Unchecked.defaultof<_>

                let! publishResult = publisher.PublishDurableAsync envelope CancellationToken.None
                match publishResult with
                | Error(InvalidPayload("TrackRequested", "forced dispatcher failure")) -> ()
                | actual -> Assert.Fail(sprintf "Expected fake dispatcher failure, but got %A." actual)

                Assert.That(dispatcher.ObservedPersistedRow, Is.True)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use command =
                    new NpgsqlCommand(
                        """SELECT "Status", "Attempts", "NextAttemptAtUtc", "UpdatedAtUtc"
FROM "OutboxEvents"
WHERE "Id" = @EventId
  AND "IsDeleted" = false;""",
                        connection
                    )

                addParameter "EventId" envelope.EventId command
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Failed"))
                Assert.That(reader.GetInt32(1), Is.EqualTo(0))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(2), Is.EqualTo(nowUtc.AddSeconds(2.0)))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(3), Is.EqualTo(nowUtc))
            })

    [<Test>]
    let ``playback cached track starts queue item and persists claimed and started outbox events`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let trackFileId = newId ()
                let queueItemId = newId ()
                let requestedAtUtc = DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero)
                let processedAtUtc = requestedAtUtc.AddSeconds(30.0)
                let cachePath = "/cache/artist-title.mp3"

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES (@TrackId, 'Cached title', 'Cached artist', @RequestedAtUtc, @RequestedAtUtc, false);

INSERT INTO "TrackFiles" (
    "Id", "TrackId", "StorageBackendId", "StoragePath", "CachePath", "ContentType", "SizeBytes", "IsCached",
    "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (@TrackFileId, @TrackId, NULL, '/library/artist-title.mp3', @CachePath, 'audio/mpeg', 1234, true, false, @RequestedAtUtc, @RequestedAtUtc);

INSERT INTO "PlaybackQueue" (
    "Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted"
)
VALUES (@QueueItemId, @TrackId, 'playlist', 'Queued', 0, @RequestedAtUtc, @RequestedAtUtc, @RequestedAtUtc, false);""",
                        connection
                    )

                addParameter "TrackId" trackId insertCommand
                addParameter "TrackFileId" trackFileId insertCommand
                addParameter "QueueItemId" queueItemId insertCommand
                addParameter "RequestedAtUtc" requestedAtUtc insertCommand
                addParameter "CachePath" cachePath insertCommand
                let! _ = insertCommand.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let generator = idGenerator ()
                let clock = clockAt processedAtUtc
                let streamNodeState = StreamNodeHeartbeatState()
                let dispatcher = createDispatcher dataSource generator clock streamNodeState
                let publisher = createPublisher dataSource dispatcher clock
                let service =
                    new PlaybackProgramHostedService(
                        dataSource,
                        publisher,
                        generator,
                        clock,
                        NullLogger<PlaybackProgramHostedService>.Instance
                    )

                let! processResult = service.ProcessOneQueueItemAsync(CancellationToken.None)
                assertOkTrue "cached playback processing" processResult

                use assertCommand =
                    new NpgsqlCommand(
                        """SELECT
    (SELECT "Status" FROM "PlaybackQueue" WHERE "Id" = @QueueItemId AND "IsDeleted" = false),
    (SELECT "StartedAtUtc" FROM "PlaybackQueue" WHERE "Id" = @QueueItemId AND "IsDeleted" = false),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'PlaybackQueueItemClaimed' AND "Payload"->>'queueItemId' = @QueueItemIdText AND "IsDeleted" = false),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'PlaybackStarted' AND "Payload"->>'queueItemId' = @QueueItemIdText AND "Payload"->>'trackId' = @TrackIdText AND "Payload"->>'cachePath' = @CachePath AND "IsDeleted" = false);""",
                        connection
                    )

                addParameter "QueueItemId" queueItemId assertCommand
                addParameter "QueueItemIdText" (string queueItemId) assertCommand
                addParameter "TrackIdText" (string trackId) assertCommand
                addParameter "CachePath" cachePath assertCommand
                let! reader = assertCommand.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Playing"))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(1), Is.EqualTo(processedAtUtc))
                Assert.That(reader.GetInt64(2), Is.EqualTo(1L))
                Assert.That(reader.GetInt64(3), Is.EqualTo(1L))
            })

    [<Test>]
    let ``playback service refuses a second cached queue item while first item is playing`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let firstTrackId = newId ()
                let secondTrackId = newId ()
                let firstTrackFileId = newId ()
                let secondTrackFileId = newId ()
                let firstQueueItemId = newId ()
                let secondQueueItemId = newId ()
                let requestedAtUtc = DateTimeOffset(2026, 7, 9, 1, 0, 0, TimeSpan.Zero)
                let processedAtUtc = requestedAtUtc.AddSeconds(30.0)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES
    (@FirstTrackId, 'First cached title', 'Cached artist', @RequestedAtUtc, @RequestedAtUtc, false),
    (@SecondTrackId, 'Second cached title', 'Cached artist', @RequestedAtUtc, @RequestedAtUtc, false);

INSERT INTO "TrackFiles" (
    "Id", "TrackId", "StorageBackendId", "StoragePath", "CachePath", "ContentType", "SizeBytes", "IsCached",
    "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES
    (@FirstTrackFileId, @FirstTrackId, NULL, '/library/first.mp3', '/cache/first.mp3', 'audio/mpeg', 1234, true, false, @RequestedAtUtc, @RequestedAtUtc),
    (@SecondTrackFileId, @SecondTrackId, NULL, '/library/second.mp3', '/cache/second.mp3', 'audio/mpeg', 1234, true, false, @RequestedAtUtc, @RequestedAtUtc);

INSERT INTO "PlaybackQueue" (
    "Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted"
)
VALUES
    (@FirstQueueItemId, @FirstTrackId, 'playlist', 'Queued', 0, @RequestedAtUtc, @RequestedAtUtc, @RequestedAtUtc, false),
    (@SecondQueueItemId, @SecondTrackId, 'playlist', 'Queued', 0, @SecondRequestedAtUtc, @SecondRequestedAtUtc, @SecondRequestedAtUtc, false);""",
                        connection
                    )

                addParameter "FirstTrackId" firstTrackId insertCommand
                addParameter "SecondTrackId" secondTrackId insertCommand
                addParameter "FirstTrackFileId" firstTrackFileId insertCommand
                addParameter "SecondTrackFileId" secondTrackFileId insertCommand
                addParameter "FirstQueueItemId" firstQueueItemId insertCommand
                addParameter "SecondQueueItemId" secondQueueItemId insertCommand
                addParameter "RequestedAtUtc" requestedAtUtc insertCommand
                addParameter "SecondRequestedAtUtc" (requestedAtUtc.AddSeconds(1.0)) insertCommand
                let! _ = insertCommand.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let generator = idGenerator ()
                let clock = clockAt processedAtUtc
                let streamNodeState = StreamNodeHeartbeatState()
                let dispatcher = createDispatcher dataSource generator clock streamNodeState
                let publisher = createPublisher dataSource dispatcher clock
                let service =
                    new PlaybackProgramHostedService(
                        dataSource,
                        publisher,
                        generator,
                        clock,
                        NullLogger<PlaybackProgramHostedService>.Instance
                    )

                let! firstResult = service.ProcessOneQueueItemAsync(CancellationToken.None)
                let! secondResult = service.ProcessOneQueueItemAsync(CancellationToken.None)
                assertOkTrue "first cached playback processing" firstResult
                assertOkFalse "second cached playback processing while first is active" secondResult

                use assertCommand =
                    new NpgsqlCommand(
                        """SELECT
    (SELECT count(*) FROM "PlaybackQueue" WHERE "Status" = 'Playing' AND "IsDeleted" = false),
    (SELECT "Status" FROM "PlaybackQueue" WHERE "Id" = @FirstQueueItemId AND "IsDeleted" = false),
    (SELECT "Status" FROM "PlaybackQueue" WHERE "Id" = @SecondQueueItemId AND "IsDeleted" = false);""",
                        connection
                    )

                addParameter "FirstQueueItemId" firstQueueItemId assertCommand
                addParameter "SecondQueueItemId" secondQueueItemId assertCommand
                let! reader = assertCommand.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetInt64(0), Is.EqualTo(1L))
                Assert.That(reader.GetString(1), Is.EqualTo("Playing"))
                Assert.That(reader.GetString(2), Is.EqualTo("Queued"))
            })

    [<Test>]
    let ``DonationPaid dispatcher rejects mismatched amount without marking payment paid`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let paymentId = newId ()
                let createdAtUtc = DateTimeOffset(2026, 7, 9, 2, 0, 0, TimeSpan.Zero)
                let paidAtUtc = createdAtUtc.AddMinutes(1.0)
                let chargeId = "telegram-charge-mismatch"

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "Payments" (
    "Id", "Purpose", "AmountStars", "Currency", "TelegramInvoicePayload", "Status", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted"
)
VALUES (@PaymentId, 'Donation', 42, 'XTR', 'invoice-payload', 'InvoiceCreated', @CreatedAtUtc, @CreatedAtUtc, false);""",
                        connection
                    )

                addParameter "PaymentId" paymentId insertCommand
                addParameter "CreatedAtUtc" createdAtUtc insertCommand
                let! _ = insertCommand.ExecuteNonQueryAsync()

                let generator = idGenerator ()
                let clock = clockAt paidAtUtc
                let streamNodeState = StreamNodeHeartbeatState()
                let dispatcher = createDispatcher dataSource generator clock streamNodeState
                let payloadJson =
                    sprintf
                        "{\"paymentId\":\"%O\",\"telegramPaymentChargeId\":\"%s\",\"amountStars\":41,\"currency\":\"XTR\"}"
                        paymentId
                        chargeId

                let envelopeResult =
                    DomainEventEnvelope.create generator clock DomainEventType.DonationPaid "web10.radio.tests" None None payloadJson

                let envelope =
                    match envelopeResult with
                    | Ok envelope -> envelope
                    | Error error -> Assert.Fail(sprintf "Expected valid envelope, but got %A." error); Unchecked.defaultof<_>

                let! dispatchResult = dispatcher.DispatchAsync envelope CancellationToken.None
                match dispatchResult with
                | Error(PaymentStateRejected rejectedPaymentId) -> Assert.That(rejectedPaymentId, Is.EqualTo(paymentId))
                | actual -> Assert.Fail(sprintf "Expected payment state rejection for mismatched amount, but got %A." actual)

                use assertCommand =
                    new NpgsqlCommand(
                        """SELECT "Status", "TelegramPaymentChargeId", "PaidAtUtc"
FROM "Payments"
WHERE "Id" = @PaymentId
  AND "IsDeleted" = false;""",
                        connection
                    )

                addParameter "PaymentId" paymentId assertCommand
                let! reader = assertCommand.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("InvoiceCreated"))
                Assert.That(reader.IsDBNull(1), Is.True)
                Assert.That(reader.IsDBNull(2), Is.True)
            })

    [<Test>]
    let ``production background worker composition resolves dispatch publisher ingestor and hosted workers`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let tempRoot = Directory.CreateTempSubdirectory("web10-radio-di-")

                try
                    let services = ServiceCollection()
                    services.AddLogging() |> ignore
                    services.AddSingleton<StreamNodeHeartbeatState>() |> ignore

                    services
                    |> DatabaseComposition.addDatabase { ConnectionString = connectionString }
                    |> ApplicationComposition.addApplicationServices
                    |> BackgroundWorkerComposition.addBackgroundWorkers (testOptions connectionString tempRoot.FullName)
                    |> ignore

                    use provider = services.BuildServiceProvider()
                    Assert.That(provider.GetRequiredService<IDomainEventDispatcher>(), Is.Not.Null)
                    Assert.That(provider.GetRequiredService<IDomainEventPublisher>(), Is.Not.Null)
                    Assert.That(provider.GetRequiredService<ITelegramUpdateEventIngestor>(), Is.Not.Null)

                    let hostedServices = provider.GetServices<IHostedService>() |> Seq.toArray
                    Assert.That(hostedServices |> Array.exists (fun service -> service :? OutboxRelayHostedService), Is.True)
                    Assert.That(hostedServices |> Array.exists (fun service -> service :? LibraryScanHostedService), Is.True)
                    Assert.That(hostedServices |> Array.exists (fun service -> service :? PlaybackProgramHostedService), Is.True)
                finally
                    tempRoot.Delete(true)
            })
