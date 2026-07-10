namespace Web10.Radio.Tests

open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Amazon.Runtime
open Amazon.S3
open Amazon.S3.Model
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Database
open Web10.Radio.Database.Repositories

module BackgroundWorkerTests =
    type private PagedAmazonS3Client() =
        inherit AmazonS3Client(AnonymousAWSCredentials(), AmazonS3Config(ServiceURL = "http://localhost", ForcePathStyle = true))

        let continuationTokens = ResizeArray<string option>()

        member _.ContinuationTokens = List.ofSeq continuationTokens

        override _.ListObjectsV2Async(request: ListObjectsV2Request, cancellationToken: CancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()
            continuationTokens.Add(if isNull request.ContinuationToken then None else Some request.ContinuationToken)
            let response = ListObjectsV2Response()
            let item = S3Object()

            if isNull request.ContinuationToken then
                item.Key <- "first.mp3"
                item.Size <- Nullable 101L
                response.IsTruncated <- Nullable true
                response.NextContinuationToken <- "page-2"
            else
                item.Key <- "second.flac"
                item.Size <- Nullable 202L
                response.IsTruncated <- Nullable false

            response.S3Objects <- ResizeArray [ item ]
            Task.FromResult(response)

    type private FixedClock(nowUtc: DateTimeOffset) =
        interface IClock with
            member _.UtcNow = nowUtc
    type private SteppingClock(initialUtc: DateTimeOffset) =
        let mutable nextUtc = initialUtc

        interface IClock with
            member _.UtcNow =
                let currentUtc = nextUtc
                nextUtc <- nextUtc.AddMinutes(1.0)
                currentUtc


    type private TestApplicationLifetime() =
        let stopping = new CancellationTokenSource()
        member _.Stop() = stopping.Cancel()


        interface IHostApplicationLifetime with
            member _.ApplicationStarted = CancellationToken.None
            member _.ApplicationStopping = stopping.Token
            member _.ApplicationStopped = CancellationToken.None
            member _.StopApplication() = stopping.Cancel()

    type private NoopPlaybackWorkflow() =
        interface IPlaybackQueueWorkflow with
            member _.HandleAsync _ _ = Task.FromResult(Ok())

    type private NoopLibraryWorkflow() =
        interface ILibraryScanWorkflow with
            member _.HandleAsync _ _ = Task.FromResult(Ok())

    type private BlockingRelayDispatcher() =
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable calls = 0

        member _.Entered = entered.Task
        member _.Release() = release.TrySetResult(()) |> ignore
        member _.Calls = Volatile.Read(&calls)

        interface IDomainEventDispatcher with
            member _.DispatchAsync _ _ =
                task {
                    Interlocked.Increment(&calls) |> ignore
                    entered.TrySetResult(()) |> ignore
                    do! release.Task
                    return Ok()
                }

    type private PagedS3Enumerator(pages: S3ObjectDescriptor list list) =
        let mutable calls = 0

        member _.Calls = Volatile.Read(&calls)

        interface IS3ObjectEnumerator with
            member _.VisitPagesAsync(_, visitPage, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    for page in pages do
                        Interlocked.Increment(&calls) |> ignore
                        do! visitPage (page |> List.toArray :> Collections.Generic.IReadOnlyList<S3ObjectDescriptor>) cancellationToken
                }
                :> Task

            member _.ProbeBucketAsync(_, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                Task.CompletedTask

    type private LeaseObservingS3Enumerator(dataSource: NpgsqlDataSource, jobId: Guid, pages: S3ObjectDescriptor list list) =
        let observedLeaseExpirations = ResizeArray<DateTimeOffset>()
        let mutable calls = 0

        member _.Calls = Volatile.Read(&calls)
        member _.ObservedLeaseExpirations = List.ofSeq observedLeaseExpirations

        interface IS3ObjectEnumerator with
            member _.VisitPagesAsync(_, visitPage, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    for page in pages do
                        Interlocked.Increment(&calls) |> ignore
                        do! visitPage (page |> List.toArray :> Collections.Generic.IReadOnlyList<S3ObjectDescriptor>) cancellationToken

                        use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                        use command = new NpgsqlCommand("SELECT \"ClaimLeaseExpiresAtUtc\" FROM \"LibraryScanJobs\" WHERE \"Id\" = @JobId;", connection)
                        command.Parameters.AddWithValue("JobId", jobId) |> ignore
                        use! reader = command.ExecuteReaderAsync(cancellationToken)
                        let! hasRow = reader.ReadAsync(cancellationToken)

                        if not hasRow then
                            return raise (InvalidOperationException("The scan job disappeared before its page callback completed."))

                        observedLeaseExpirations.Add(reader.GetFieldValue<DateTimeOffset>(0))
                }
                :> Task

            member _.ProbeBucketAsync(_, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                Task.CompletedTask

    type private BlockingS3Enumerator() =
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        member _.Entered = entered.Task

        interface IS3ObjectEnumerator with
            member _.VisitPagesAsync(_, _, cancellationToken) =
                let pending = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                entered.TrySetResult(()) |> ignore
                cancellationToken.Register(fun () -> pending.TrySetCanceled(cancellationToken) |> ignore) |> ignore
                pending.Task :> Task

            member _.ProbeBucketAsync(_, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                Task.CompletedTask

    type private BlockingQueueWorkflow() =
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable calls = 0

        member _.Entered = entered.Task
        member _.Release() = release.TrySetResult(()) |> ignore
        member _.Calls = Volatile.Read(&calls)

        interface IPlaybackQueueWorkflow with
            member _.HandleAsync _ _ =
                task {
                    let call = Interlocked.Increment(&calls)

                    if call = 1 then
                        entered.TrySetResult(()) |> ignore
                        do! release.Task

                    return Ok()
                }

    let private newId () =
        (UuidV7IdGenerator() :> IIdGenerator).NewId()

    let private idGenerator () =
        UuidV7IdGenerator() :> IIdGenerator

    let private clockAt value =
        FixedClock(value) :> IClock

    let private testOptions connectionString storageType localRoot bucket =
        { Postgres = { ConnectionString = connectionString }
          Telegram =
            { BotToken = "test-bot-token"
              WebhookSecret = "test-webhook-secret"
              ChannelIdOrUsername = "@web10_test" }
          Stream =
            { RtmpUrl = Uri("rtmp://localhost/live")
              RtmpKey = "test-rtmp-key"
              StageUrl = Uri("http://localhost/stage")
              CallbackToken = "test-callback-token-Secret_12345" }
          Storage =
            { Type = storageType
              LocalRoot = localRoot
              S3Bucket = bucket
              S3Region = "us-east-1"
              S3ServiceUrl = None
              S3ForcePathStyle = false }
          Admin = { Token = "test-admin-token" }
          Otel = { ExporterOtlpEndpoint = Uri("http://localhost:4317") }
          DataProtection = { KeyRingPath = "/tmp/web10-radio-tests-keys" } }

    let private assertOkTrue description result =
        match result with
        | Ok true -> ()
        | actual -> Assert.Fail(sprintf "Expected %s to return Ok true, but got %A." description actual)

    let private envelope generator clock eventType payload =
        match DomainEventEnvelope.create generator clock eventType "web10.radio.tests" None None payload with
        | Ok value -> value
        | Error error -> Assert.Fail(sprintf "Expected a valid %s test envelope, but got %A." (DomainEventType.toString eventType) error); Unchecked.defaultof<_>


    [<Test>]
    let ``relay drives claimed playback through Played and PlaybackEnded before the next queue item progresses`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let nowUtc = DateTimeOffset(2026, 7, 10, 17, 0, 0, TimeSpan.Zero)
                let firstTrackId = newId ()
                let secondTrackId = newId ()
                let firstFileId = newId ()
                let secondFileId = newId ()
                let firstQueueItemId = newId ()
                let secondQueueItemId = newId ()

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@FirstTrackId, 'First', 'Artist', false),
       (@SecondTrackId, 'Second', 'Artist', false);
INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "CachePath", "IsCached", "IsDeleted")
VALUES (@FirstFileId, @FirstTrackId, '/library/first.mp3', '/cache/first.mp3', true, false),
       (@SecondFileId, @SecondTrackId, '/library/second.mp3', '/cache/second.mp3', true, false);
INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc")
VALUES (@FirstQueueItemId, @FirstTrackId, 'playlist', 'Queued', 0, @NowUtc),
       (@SecondQueueItemId, @SecondTrackId, 'playlist', 'Queued', 0, @SecondRequestedAtUtc);""",
                        connection
                    )

                for name, value in
                    [ "FirstTrackId", box firstTrackId
                      "SecondTrackId", box secondTrackId
                      "FirstFileId", box firstFileId
                      "SecondFileId", box secondFileId
                      "FirstQueueItemId", box firstQueueItemId
                      "SecondQueueItemId", box secondQueueItemId
                      "NowUtc", box nowUtc
                      "SecondRequestedAtUtc", box (nowUtc.AddSeconds(1.0)) ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore

                let! _ = setup.ExecuteNonQueryAsync()
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let generator = idGenerator ()
                let clock = clockAt nowUtc
                let playback = new PlaybackProgramHostedService(dataSource, generator, clock, NullLogger<PlaybackProgramHostedService>.Instance)
                let lifetime = TestApplicationLifetime()
                let dispatcher =
                    DomainEventDispatcher(
                        dataSource,
                        generator,
                        clock,
                        StreamNodeHeartbeatState(),
                        playback :> IPlaybackQueueWorkflow,
                        NoopLibraryWorkflow() :> ILibraryScanWorkflow,
                        lifetime :> IHostApplicationLifetime,
                        NullLogger<DomainEventDispatcher>.Instance
                    )
                    :> IDomainEventDispatcher

                let relay = new OutboxRelayHostedService(dataSource, dispatcher, clock, generator, NullLogger<OutboxRelayHostedService>.Instance)

                let! firstClaimed = playback.ProcessOneQueueItemAsync(CancellationToken.None)
                assertOkTrue "the first queue claim" firstClaimed

                for transition in [ "claim dispatch"; "start notification dispatch" ] do
                    let! result = relay.ProcessDueEventsOnceAsync(CancellationToken.None)
                    match result with
                    | Ok 1 -> ()
                    | actual -> Assert.Fail(sprintf "Expected %s to dispatch one durable event, but got %A." transition actual)

                use playingCommand =
                    new NpgsqlCommand(
                        """SELECT "Status", "ClaimOwner", "ClaimAttempt"
FROM "PlaybackQueue"
WHERE "Id" = @QueueItemId;""",
                        connection
                    )

                playingCommand.Parameters.AddWithValue("QueueItemId", firstQueueItemId) |> ignore
                let! playingReader = playingCommand.ExecuteReaderAsync()
                use playingReader = playingReader
                let! hasPlayingRow = playingReader.ReadAsync()
                Assert.That(hasPlayingRow, Is.True)
                Assert.That(playingReader.GetString(0), Is.EqualTo("Playing"), "PlaybackStarted must not auto-complete audio before stream-node reports an outcome.")
                let firstOwner = playingReader.GetGuid(1)
                let firstAttempt = playingReader.GetInt32(2)
                do! playingReader.CloseAsync()

                let reporter = PlaybackCompletionReporter(dataSource, generator, clock) :> IPlaybackCompletionReporter
                let! renewed = reporter.RenewLeaseAsync firstQueueItemId firstOwner firstAttempt CancellationToken.None
                assertOkTrue "the active stream-node lease renewal" renewed
                let! staleRenewal = reporter.RenewLeaseAsync firstQueueItemId (newId ()) firstAttempt CancellationToken.None
                match staleRenewal with
                | Ok false -> ()
                | actual -> Assert.Fail(sprintf "Expected stale stream-node lease renewal to be fenced, but got %A." actual)

                let! completed = reporter.ReportAsync firstQueueItemId firstOwner firstAttempt Succeeded CancellationToken.None
                assertOkTrue "the authoritative successful completion callback" completed
                let! endedRelayResult = relay.ProcessDueEventsOnceAsync(CancellationToken.None)
                match endedRelayResult with
                | Ok 1 -> ()
                | actual -> Assert.Fail(sprintf "Expected the successful PlaybackEnded event to relay once, but got %A." actual)

                let! secondClaimed = playback.ProcessOneQueueItemAsync(CancellationToken.None)
                assertOkTrue "the next queue claim after authoritative Played completion" secondClaimed

                use assertion =
                    new NpgsqlCommand(
                        """SELECT
    (SELECT "Status" FROM "PlaybackQueue" WHERE "Id" = @FirstQueueItemId),
    (SELECT "Status" FROM "PlaybackQueue" WHERE "Id" = @SecondQueueItemId),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'PlaybackEnded' AND "Payload"->>'status' = 'played' AND "IsDeleted" = false),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'PlaybackStarted' AND "Status" = 'Processed' AND "IsDeleted" = false);""",
                        connection
                    )

                assertion.Parameters.AddWithValue("FirstQueueItemId", firstQueueItemId) |> ignore
                assertion.Parameters.AddWithValue("SecondQueueItemId", secondQueueItemId) |> ignore
                let! reader = assertion.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Played"))
                Assert.That(reader.GetString(1), Is.EqualTo("Claimed"), "The next ordered item must advance only after authoritative completion.")
                Assert.That(reader.GetInt64(2), Is.EqualTo(1L))
                Assert.That(reader.GetInt64(3), Is.EqualTo(1L))
            })

    [<Test>]
    let ``durable publication stays Pending until one overlapping relay owns its sole dispatch attempt`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let nowUtc = DateTimeOffset(2026, 7, 10, 18, 0, 0, TimeSpan.Zero)
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let generator = idGenerator ()
                let clock = clockAt nowUtc
                let published = envelope generator clock TrackRequested "{\"query\":\"one side effect\"}"
                let publisher = DomainEventPublisher(dataSource) :> IDomainEventPublisher
                let! publication = publisher.PublishDurableAsync published CancellationToken.None

                match publication with
                | Ok () -> ()
                | actual -> Assert.Fail(sprintf "Expected durable publication to append the event, but got %A." actual)

                let blockingDispatcher = BlockingRelayDispatcher()
                Assert.That(blockingDispatcher.Calls, Is.EqualTo(0), "Publishing durably must not invoke a handler before a relay claims the event.")

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use pendingCommand = new NpgsqlCommand("SELECT \"Status\", \"Attempts\" FROM \"OutboxEvents\" WHERE \"Id\" = @EventId;", connection)
                pendingCommand.Parameters.AddWithValue("EventId", published.EventId) |> ignore
                let! pendingReader = pendingCommand.ExecuteReaderAsync()
                use pendingReader = pendingReader
                let! hasPendingRow = pendingReader.ReadAsync()
                Assert.That(hasPendingRow, Is.True)
                Assert.That(pendingReader.GetString(0), Is.EqualTo("Pending"))
                Assert.That(pendingReader.GetInt32(1), Is.EqualTo(0))
                do! pendingReader.CloseAsync()

                let firstRelay = new OutboxRelayHostedService(dataSource, blockingDispatcher :> IDomainEventDispatcher, clock, generator, NullLogger<OutboxRelayHostedService>.Instance)
                let secondRelay = new OutboxRelayHostedService(dataSource, blockingDispatcher :> IDomainEventDispatcher, clock, generator, NullLogger<OutboxRelayHostedService>.Instance)
                let firstAttempt = firstRelay.ProcessDueEventsOnceAsync(CancellationToken.None)
                do! blockingDispatcher.Entered

                let! overlappingAttempt = secondRelay.ProcessDueEventsOnceAsync(CancellationToken.None)
                match overlappingAttempt with
                | Ok 0 -> ()
                | actual -> Assert.Fail(sprintf "The second relay must observe the held dispatch lease instead of invoking the handler, but got %A." actual)
                Assert.That(blockingDispatcher.Calls, Is.EqualTo(1))

                blockingDispatcher.Release()
                let! firstResult = firstAttempt
                match firstResult with
                | Ok 1 -> ()
                | actual -> Assert.Fail(sprintf "Expected the first relay to complete one dispatch, but got %A." actual)
                Assert.That(blockingDispatcher.Calls, Is.EqualTo(1), "A durable handler side effect must be invoked once despite relay overlap.")

                use terminalCommand = new NpgsqlCommand("SELECT \"Status\", \"Attempts\" FROM \"OutboxEvents\" WHERE \"Id\" = @EventId;", connection)
                terminalCommand.Parameters.AddWithValue("EventId", published.EventId) |> ignore
                let! terminalReader = terminalCommand.ExecuteReaderAsync()
                use terminalReader = terminalReader
                let! hasTerminalRow = terminalReader.ReadAsync()
                Assert.That(hasTerminalRow, Is.True)
                Assert.That(terminalReader.GetString(0), Is.EqualTo("Processed"))
                Assert.That(terminalReader.GetInt32(1), Is.EqualTo(1))
            })

    [<Test>]
    let ``row S3 backend overrides Local configuration with exact uncached discoveries and per-page lease renewal`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let nowUtc = DateTimeOffset(2026, 7, 10, 19, 0, 0, TimeSpan.Zero)
                let jobId = newId ()
                let storageBackendId = newId ()
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertJob =
                    new NpgsqlCommand(
                        """INSERT INTO "StorageBackends" ("Id", "Name", "Type", "S3Bucket", "IsEnabled", "IsDeleted")
VALUES (@StorageBackendId, 'row-s3', 'S3', 'row-radio-bucket', true, false);
INSERT INTO "LibraryScanJobs" ("Id", "StorageBackendId", "Status", "RequestedAtUtc", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES (@JobId, @StorageBackendId, 'Queued', @NowUtc, @NowUtc, @NowUtc, false);""",
                        connection
                    )

                insertJob.Parameters.AddWithValue("JobId", jobId) |> ignore
                insertJob.Parameters.AddWithValue("StorageBackendId", storageBackendId) |> ignore
                insertJob.Parameters.AddWithValue("NowUtc", nowUtc) |> ignore
                let! _ = insertJob.ExecuteNonQueryAsync()

                let generator = idGenerator ()
                let clock = SteppingClock(nowUtc) :> IClock
                let enumerator =
                    LeaseObservingS3Enumerator(
                        dataSource,
                        jobId,
                        [ [ { Key = "first/Artist - First.mp3"; SizeBytes = 101L } ]
                          [ { Key = "second/Artist - Second.flac"; SizeBytes = 202L }
                            { Key = "second/ignored.txt"; SizeBytes = 1L } ] ]
                    )

                let scanner =
                    new LibraryScanHostedService(
                        dataSource,
                        generator,
                        clock,
                        testOptions connectionString Local "/unused-local-default" "",
                        enumerator :> IS3ObjectEnumerator,
                        NullLogger<LibraryScanHostedService>.Instance
                    )

                let lifetime = TestApplicationLifetime()
                let dispatcher =
                    DomainEventDispatcher(
                        dataSource,
                        generator,
                        clock,
                        StreamNodeHeartbeatState(),
                        NoopPlaybackWorkflow() :> IPlaybackQueueWorkflow,
                        scanner :> ILibraryScanWorkflow,
                        lifetime :> IHostApplicationLifetime,
                        NullLogger<DomainEventDispatcher>.Instance
                    )
                    :> IDomainEventDispatcher

                let requested = envelope generator clock LibraryScanRequested (sprintf "{\"libraryScanJobId\":\"%O\"}" jobId)
                let! dispatched = dispatcher.DispatchAsync requested CancellationToken.None
                match dispatched with
                | Ok () -> ()
                | actual -> Assert.Fail(sprintf "Expected LibraryScanRequested to invoke the S3 row backend, but got %A." actual)

                Assert.That(enumerator.Calls, Is.EqualTo(2), "The scanner must visit each S3 page.")
                Assert.That(enumerator.ObservedLeaseExpirations, Is.EqualTo(box [ nowUtc.AddMinutes(7.0); nowUtc.AddMinutes(10.0) ]), "The claimed scan lease must be renewed after each page callback before enumeration advances.")

                use jobCommand = new NpgsqlCommand("SELECT \"Status\" FROM \"LibraryScanJobs\" WHERE \"Id\" = @JobId;", connection)
                jobCommand.Parameters.AddWithValue("JobId", jobId) |> ignore
                let! jobReader = jobCommand.ExecuteReaderAsync()
                use jobReader = jobReader
                let! hasJob = jobReader.ReadAsync()
                Assert.That(hasJob, Is.True)
                Assert.That(jobReader.GetString(0), Is.EqualTo("Completed"))
                do! jobReader.CloseAsync()

                use filesCommand =
                    new NpgsqlCommand(
                        """SELECT tf."StoragePath", tf."SizeBytes", tf."CachePath", tf."IsCached", tf."StorageBackendId", tf."TrackId", tf."Id"
FROM "TrackFiles" tf
WHERE tf."IsDeleted" = false
ORDER BY tf."StoragePath";""",
                        connection
                    )
                let! filesReader = filesCommand.ExecuteReaderAsync()
                use filesReader = filesReader
                let files = ResizeArray<string * int64 * string option * bool * Guid * Guid * Guid>()

                while filesReader.Read() do
                    files.Add(
                        ( filesReader.GetString(0),
                          filesReader.GetInt64(1),
                          (if filesReader.IsDBNull(2) then None else Some(filesReader.GetString(2))),
                          filesReader.GetBoolean(3),
                          filesReader.GetGuid(4),
                          filesReader.GetGuid(5),
                          filesReader.GetGuid(6) )
                    )

                let expectedFiles : (string * int64 * string option * bool * Guid) list = [ ("first/Artist - First.mp3", 101L, None, false, storageBackendId); ("second/Artist - Second.flac", 202L, None, false, storageBackendId) ]
                Assert.That(files |> Seq.map (fun (path, size, cachePath, isCached, backendId, _, _) -> path, size, cachePath, isCached, backendId) |> Seq.toList, Is.EqualTo(box expectedFiles))
                do! filesReader.CloseAsync()

                use eventsCommand =
                    new NpgsqlCommand(
                        """SELECT "Payload"::text
FROM "OutboxEvents"
WHERE "EventType" = 'TrackDiscovered' AND "Status" = 'Pending' AND "IsDeleted" = false
ORDER BY "Payload"->>'storagePath';""",
                        connection
                    )
                let! eventsReader = eventsCommand.ExecuteReaderAsync()
                use eventsReader = eventsReader
                let payloads = ResizeArray<string>()

                while eventsReader.Read() do
                    payloads.Add(eventsReader.GetString(0))

                let expectedPayloads =
                    files
                    |> Seq.map (fun (storagePath, _, _, _, _, trackId, trackFileId) -> storagePath, trackId, trackFileId)
                    |> Seq.toList

                Assert.That(payloads.Count, Is.EqualTo(expectedPayloads.Length))

                for payload, (storagePath, trackId, trackFileId) in Seq.zip payloads expectedPayloads do
                    use document = System.Text.Json.JsonDocument.Parse(payload)
                    let root = document.RootElement
                    Assert.That(root.EnumerateObject() |> Seq.map (fun property -> property.Name) |> Set.ofSeq, Is.EqualTo(box (Set.ofList [ "storagePath"; "trackFileId"; "trackId" ])))
                    Assert.That(root.GetProperty("storagePath").GetString(), Is.EqualTo(storagePath))
                    Assert.That(root.GetProperty("trackId").GetGuid(), Is.EqualTo(trackId))
                    Assert.That(root.GetProperty("trackFileId").GetGuid(), Is.EqualTo(trackFileId))
            })

    [<Test>]
    let ``scanner cancellation after claim records retryable failure without late track discovery`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let nowUtc = DateTimeOffset(2026, 7, 10, 20, 0, 0, TimeSpan.Zero)
                let jobId = newId ()
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertJob =
                    new NpgsqlCommand(
                        """INSERT INTO "LibraryScanJobs" ("Id", "Status", "RequestedAtUtc", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES (@JobId, 'Queued', @NowUtc, @NowUtc, @NowUtc, false);""",
                        connection
                    )

                insertJob.Parameters.AddWithValue("JobId", jobId) |> ignore
                insertJob.Parameters.AddWithValue("NowUtc", nowUtc) |> ignore
                let! _ = insertJob.ExecuteNonQueryAsync()

                let enumerator = BlockingS3Enumerator()
                let scanner =
                    new LibraryScanHostedService(
                        dataSource,
                        idGenerator (),
                        clockAt nowUtc,
                        testOptions connectionString S3 "" "radio-test-bucket",
                        enumerator :> IS3ObjectEnumerator,
                        NullLogger<LibraryScanHostedService>.Instance
                    )

                use cancellation = new CancellationTokenSource()
                let processing = scanner.ProcessOneJobAsync(cancellation.Token)
                do! enumerator.Entered
                cancellation.Cancel()
                let! processingResult = processing
                match processingResult with
                | Error(UnexpectedException("LibraryScanHostedService", _)) -> ()
                | actual -> Assert.Fail(sprintf "Expected cancellation to surface as scan processing failure, but got %A." actual)

                use assertion =
                    new NpgsqlCommand(
                        """SELECT
    (SELECT "Status" FROM "LibraryScanJobs" WHERE "Id" = @JobId),
    (SELECT count(*) FROM "Tracks" WHERE "IsDeleted" = false),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'StreamNodeFailureDetected' AND "IsDeleted" = false),
    (SELECT count(*) FROM "StreamNodeHeartbeats" WHERE "Status" = 'Degraded' AND "IsDeleted" = false);""",
                        connection
                    )

                assertion.Parameters.AddWithValue("JobId", jobId) |> ignore
                let! reader = assertion.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Failed"))
                Assert.That(reader.GetInt64(1), Is.EqualTo(0L), "Cancellation must not permit a late discovery write after the claimed workflow terminates.")
                Assert.That(reader.GetInt64(2), Is.EqualTo(0L), "A library scan failure must not emit a stream-node failure event.")
                Assert.That(reader.GetInt64(3), Is.EqualTo(0L), "A library scan failure must not degrade stream-node health.")
            })

    [<Test>]
    let ``ApplicationStopping cancels queued mailbox work and post-stop dispatch cannot execute`` () =
        task {
            let generator = idGenerator ()
            let nowUtc = DateTimeOffset(2026, 7, 10, 21, 0, 0, TimeSpan.Zero)
            let queueWorkflow = BlockingQueueWorkflow()
            let lifetime = TestApplicationLifetime()
            use dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused;Password=unused")
            let dispatcher =
                DomainEventDispatcher(
                    dataSource,
                    generator,
                    clockAt nowUtc,
                    StreamNodeHeartbeatState(),
                    queueWorkflow :> IPlaybackQueueWorkflow,
                    NoopLibraryWorkflow() :> ILibraryScanWorkflow,
                    lifetime :> IHostApplicationLifetime,
                    NullLogger<DomainEventDispatcher>.Instance
                )
                :> IDomainEventDispatcher

            let first = envelope generator (clockAt nowUtc) PlaybackEnded "{}"
            let second = envelope generator (clockAt nowUtc) PlaybackEnded "{}"
            let third = envelope generator (clockAt nowUtc) PlaybackEnded "{}"
            let _ = dispatcher.DispatchAsync first CancellationToken.None
            do! queueWorkflow.Entered.WaitAsync(TimeSpan.FromSeconds(10.0))
            let queuedDispatch = dispatcher.DispatchAsync second CancellationToken.None
            (lifetime :> IHostApplicationLifetime).StopApplication()

            try
                let! _ = queuedDispatch.WaitAsync(TimeSpan.FromSeconds(10.0))
                Assert.Fail("Expected ApplicationStopping to cancel queued dispatch before workflow execution.")
            with :? TaskCanceledException -> ()

            Assert.That(queueWorkflow.Calls, Is.EqualTo(1), "Stopping must cancel queued mailbox work before a second workflow call can begin.")
            queueWorkflow.Release()


            let postStopDispatch = dispatcher.DispatchAsync third CancellationToken.None

            try
                let! _ = postStopDispatch.WaitAsync(TimeSpan.FromSeconds(10.0))
                Assert.Fail("Expected dispatch after ApplicationStopping to be canceled without entering the workflow.")
            with :? TaskCanceledException -> ()

            Assert.That(queueWorkflow.Calls, Is.EqualTo(1), "Stopping must reject post-stop dispatch without late workflow calls.")

        }

    [<Test>]
    let ``S3 object enumerator visits every ListObjectsV2 page and propagates cancellation`` () =
        task {
            use client = new PagedAmazonS3Client()
            let enumerator = new S3ObjectEnumerator(client) :> IS3ObjectEnumerator
            let visited = ResizeArray<S3ObjectDescriptor list>()

            do!
                enumerator.VisitPagesAsync(
                    "test-bucket",
                    (fun page _ ->
                        visited.Add(List.ofSeq page)
                        Task.CompletedTask),
                    CancellationToken.None
                )

            Assert.That(client.ContinuationTokens |> List.length, Is.EqualTo(2))
            Assert.That(client.ContinuationTokens |> List.head, Is.EqualTo(None))
            Assert.That(client.ContinuationTokens |> List.last, Is.EqualTo(Some "page-2"))
            let descriptors = visited |> Seq.collect id |> Seq.toArray
            Assert.That(descriptors.Length, Is.EqualTo(2))
            Assert.That(descriptors[0].Key, Is.EqualTo("first.mp3"))
            Assert.That(descriptors[1].Key, Is.EqualTo("second.flac"))
            Assert.That(descriptors[0].SizeBytes, Is.EqualTo(101L))
            Assert.That(descriptors[1].SizeBytes, Is.EqualTo(202L))

            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()

            try
                do! enumerator.VisitPagesAsync("test-bucket", (fun _ _ -> Task.CompletedTask), cancellation.Token)
                Assert.Fail("Expected a canceled VisitPagesAsync call to propagate cancellation.")
            with :? OperationCanceledException -> ()
        }
