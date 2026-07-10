namespace Web10.Radio.Tests

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Database
open Web10.Radio.Database.Repositories

module LibraryScanRepositoryTests =
    let private newId () =
        (UuidV7IdGenerator() :> IIdGenerator).NewId()

    let private discoveredTrack storageBackendId storagePath discoveredAtUtc =
        { TrackId = newId ()
          TrackFileId = newId ()
          StorageBackendId = storageBackendId
          StoragePath = storagePath
          CachePath = Some storagePath
          IsCached = true
          Title = "Concurrent discovery"
          Artist = "Scanner"
          ContentType = Some "audio/mpeg"
          SizeBytes = Some 1234L
          DiscoveredAtUtc = discoveredAtUtc }

    let private claim description result =
        match result with
        | Ok(Some job) -> job
        | actual -> Assert.Fail(sprintf "Expected %s to claim the scan job, but got %A." description actual); Unchecked.defaultof<_>

    let private assertOkTrue description result =
        match result with
        | Ok true -> ()
        | actual -> Assert.Fail(sprintf "Expected %s to return Ok true, but got %A." description actual)

    let private assertOkFalse description result =
        match result with
        | Ok false -> ()
        | actual -> Assert.Fail(sprintf "Expected %s to return Ok false, but got %A." description actual)

    [<Test>]
    let ``stale Running scan is reclaimed and only the replacement owner can finish it`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let jobId = newId ()
                let firstOwner = newId ()
                let replacementOwner = newId ()
                let requestedAtUtc = DateTimeOffset(2026, 7, 10, 15, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "LibraryScanJobs" ("Id", "Status", "RequestedAtUtc", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES (@JobId, 'Queued', @RequestedAtUtc, @RequestedAtUtc, @RequestedAtUtc, false);""",
                        connection
                    )

                insertCommand.Parameters.AddWithValue("JobId", jobId) |> ignore
                insertCommand.Parameters.AddWithValue("RequestedAtUtc", requestedAtUtc) |> ignore
                let! _ = insertCommand.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! firstClaim =
                    LibraryScanRepository.claimNextJob dataSource firstOwner requestedAtUtc (requestedAtUtc.AddSeconds(30.0)) CancellationToken.None

                let firstJob = claim "the original scanner" firstClaim

                let! replacementClaim =
                    LibraryScanRepository.claimNextJob
                        dataSource
                        replacementOwner
                        (requestedAtUtc.AddSeconds(31.0))
                        (requestedAtUtc.AddMinutes(1.0))
                        CancellationToken.None

                let replacementJob = claim "the restart scanner" replacementClaim
                Assert.That(replacementJob.Id, Is.EqualTo(jobId))
                Assert.That(replacementJob.ClaimAttempt, Is.EqualTo(firstJob.ClaimAttempt + 1))

                let! staleCompletion =
                    LibraryScanRepository.completeJob dataSource jobId firstJob.ClaimOwner firstJob.ClaimAttempt (requestedAtUtc.AddSeconds(32.0)) CancellationToken.None

                let! staleFailure =
                    LibraryScanRepository.failJob dataSource jobId firstJob.ClaimOwner firstJob.ClaimAttempt (requestedAtUtc.AddSeconds(32.0)) "cancelled predecessor" CancellationToken.None

                assertOkFalse "the predecessor completion fence" staleCompletion
                assertOkFalse "the predecessor failure fence" staleFailure

                let! replacementCompletion =
                    LibraryScanRepository.completeJob
                        dataSource
                        jobId
                        replacementJob.ClaimOwner
                        replacementJob.ClaimAttempt
                        (requestedAtUtc.AddSeconds(33.0))
                        CancellationToken.None

                assertOkTrue "the replacement completion fence" replacementCompletion

                use command =
                    new NpgsqlCommand(
                        """SELECT "Status", "ClaimAttempt", "FinishedAtUtc", "FailureReason"
FROM "LibraryScanJobs"
WHERE "Id" = @JobId;""",
                        connection
                    )

                command.Parameters.AddWithValue("JobId", jobId) |> ignore
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Completed"))
                Assert.That(reader.GetInt32(1), Is.EqualTo(replacementJob.ClaimAttempt))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(2), Is.EqualTo(requestedAtUtc.AddSeconds(33.0)))
                Assert.That(reader.IsDBNull(3), Is.True)
            })

    [<Test>]
    let ``concurrent discoveries retain one active file and track for null and non-null storage backends`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let discoveredAtUtc = DateTimeOffset(2026, 7, 10, 16, 0, 0, TimeSpan.Zero)
                let nullBackendPath = "/library/null-backend.mp3"
                let namedBackendPath = "albums/named-backend.mp3"
                let storageBackendId = newId ()

                use setupConnection = new NpgsqlConnection(connectionString)
                do! setupConnection.OpenAsync()
                use backendCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "StorageBackends" ("Id", "Name", "Type", "S3Bucket", "IsEnabled", "IsDeleted")
VALUES (@StorageBackendId, 'concurrency-s3', 'S3', 'tests', true, false);""",
                        setupConnection
                    )

                backendCommand.Parameters.AddWithValue("StorageBackendId", storageBackendId) |> ignore
                let! _ = backendCommand.ExecuteNonQueryAsync()

                let compete storageBackendId storagePath =
                    task {
                        let start = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                        let first = discoveredTrack storageBackendId storagePath discoveredAtUtc
                        let second = discoveredTrack storageBackendId storagePath (discoveredAtUtc.AddMilliseconds(1.0))

                        let insert candidate =
                            task {
                                do! start.Task
                                return!
                                    DatabaseSession.withTransactionResult
                                        dataSource
                                        (fun connection transaction cancellationToken ->
                                            LibraryScanRepository.insertDiscoveredTrackInTransaction connection transaction candidate cancellationToken)
                                        CancellationToken.None
                            }

                        let inserts = [| insert first; insert second |]
                        start.SetResult(())
                        let! results = Task.WhenAll(inserts)
                        return results
                    }

                let! nullBackendResults = compete None nullBackendPath
                let! namedBackendResults = compete (Some storageBackendId) namedBackendPath

                for scenario, results in [ "NULL backend", nullBackendResults; "named backend", namedBackendResults ] do
                    let inserted =
                        results
                        |> Array.choose (function
                            | Ok value -> Some value
                            | Error error -> Assert.Fail(sprintf "Expected %s concurrent insert to resolve through the uniqueness contract, but got %A." scenario error); None)

                    Assert.That(inserted |> Array.filter id, Has.Length.EqualTo(1), sprintf "%s has exactly one winning TrackFile insert." scenario)
                    Assert.That(inserted |> Array.filter not, Has.Length.EqualTo(1), sprintf "%s suppresses exactly one competing TrackFile insert." scenario)

                use assertionConnection = new NpgsqlConnection(connectionString)
                do! assertionConnection.OpenAsync()
                use command =
                    new NpgsqlCommand(
                        """SELECT
    count(*) FILTER (WHERE "StorageBackendId" IS NULL AND "StoragePath" = @NullBackendPath),
    count(*) FILTER (WHERE "StorageBackendId" = @StorageBackendId AND "StoragePath" = @NamedBackendPath),
    (SELECT count(*) FROM "Tracks" WHERE "IsDeleted" = false)
FROM "TrackFiles"
WHERE "IsDeleted" = false;""",
                        assertionConnection
                    )

                command.Parameters.AddWithValue("NullBackendPath", nullBackendPath) |> ignore
                command.Parameters.AddWithValue("StorageBackendId", storageBackendId) |> ignore
                command.Parameters.AddWithValue("NamedBackendPath", namedBackendPath) |> ignore
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetInt64(0), Is.EqualTo(1L), "PostgreSQL NULL uniqueness must be explicit rather than relying on ordinary unique-index NULL behavior.")
                Assert.That(reader.GetInt64(1), Is.EqualTo(1L))
                Assert.That(reader.GetInt64(2), Is.EqualTo(2L), "The losing transaction must not leave an active orphan Track.")
            })

    let private created description result =
        match result with
        | Ok(Created job) -> job
        | actual -> Assert.Fail(sprintf "Expected %s to create a scan job, but got %A." description actual); Unchecked.defaultof<_>

    let private existing description result =
        match result with
        | Ok(Existing job) -> job
        | actual -> Assert.Fail(sprintf "Expected %s to reuse an active scan job, but got %A." description actual); Unchecked.defaultof<_>

    let private assertStorageBackendNotFound description result =
        match result with
        | Ok StorageBackendNotFound -> ()
        | actual -> Assert.Fail(sprintf "Expected %s to reject the explicit backend without creating a scan job, but got %A." description actual)

    let private status description result =
        match result with
        | Ok(Some job) -> job
        | actual -> Assert.Fail(sprintf "Expected %s to return a scan-job status, but got %A." description actual); Unchecked.defaultof<_>

    [<Test>]
    let ``createOrGetActiveJob creates one default and explicit job then returns their active records on retry`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let requestedAtUtc = DateTimeOffset(2026, 7, 10, 17, 0, 0, TimeSpan.Zero)
                let explicitBackendId = newId ()

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use backendCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "StorageBackends" ("Id", "Name", "Type", "LocalRoot", "IsEnabled", "IsDeleted")
VALUES (@BackendId, 'enabled-local', 'Local', '/library', true, false);""",
                        connection
                    )

                backendCommand.Parameters.AddWithValue("BackendId", explicitBackendId) |> ignore
                let! _ = backendCommand.ExecuteNonQueryAsync()

                let! createdDefault =
                    LibraryScanRepository.createOrGetActiveJob
                        dataSource
                        (newId ())
                        None
                        requestedAtUtc
                        CancellationToken.None

                let defaultJob = created "the first default-backend request" createdDefault
                Assert.That(Option.isNone defaultJob.StorageBackendId, Is.True)
                Assert.That(defaultJob.Status, Is.EqualTo("Queued"))
                Assert.That(defaultJob.RequestedAtUtc, Is.EqualTo(requestedAtUtc))
                Assert.That(Option.isNone defaultJob.StartedAtUtc, Is.True)
                Assert.That(Option.isNone defaultJob.FinishedAtUtc, Is.True)
                Assert.That(Option.isNone defaultJob.FailureReason, Is.True)
                Assert.That(defaultJob.DiscoveredCount, Is.Zero)

                let! retriedDefault =
                    LibraryScanRepository.createOrGetActiveJob
                        dataSource
                        (newId ())
                        None
                        (requestedAtUtc.AddSeconds(1.0))
                        CancellationToken.None

                let existingDefault = existing "a default-backend retry" retriedDefault
                Assert.That(existingDefault.Id, Is.EqualTo(defaultJob.Id), "A retry must return the queued job rather than enqueueing a second default scan.")

                let! createdExplicit =
                    LibraryScanRepository.createOrGetActiveJob
                        dataSource
                        (newId ())
                        (Some explicitBackendId)
                        (requestedAtUtc.AddSeconds(2.0))
                        CancellationToken.None

                let explicitJob = created "the first explicit-backend request" createdExplicit
                Assert.That(explicitJob.StorageBackendId, Is.EqualTo(Some explicitBackendId))

                let! retriedExplicit =
                    LibraryScanRepository.createOrGetActiveJob
                        dataSource
                        (newId ())
                        (Some explicitBackendId)
                        (requestedAtUtc.AddSeconds(3.0))
                        CancellationToken.None

                let existingExplicit = existing "an explicit-backend retry" retriedExplicit
                Assert.That(existingExplicit.Id, Is.EqualTo(explicitJob.Id), "A retry must preserve the explicit backend's original queued job.")

                use activeCountCommand =
                    new NpgsqlCommand(
                        """SELECT
    count(*) FILTER (WHERE "StorageBackendId" IS NULL),
    count(*) FILTER (WHERE "StorageBackendId" = @BackendId)
FROM "LibraryScanJobs"
WHERE "IsDeleted" = false
  AND "Status" IN ('Queued', 'Running');""",
                        connection
                    )

                activeCountCommand.Parameters.AddWithValue("BackendId", explicitBackendId) |> ignore
                let! reader = activeCountCommand.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetInt64(0), Is.EqualTo(1L))
                Assert.That(reader.GetInt64(1), Is.EqualTo(1L))
            })

    [<Test>]
    let ``createOrGetActiveJob distinguishes missing and disabled explicit storage backends from idempotent acceptance`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let requestedAtUtc = DateTimeOffset(2026, 7, 10, 17, 10, 0, TimeSpan.Zero)
                let disabledBackendId = newId ()
                let missingBackendId = newId ()

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use backendCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "StorageBackends" ("Id", "Name", "Type", "LocalRoot", "IsEnabled", "IsDeleted")
VALUES (@BackendId, 'disabled-local', 'Local', '/disabled-library', false, false);""",
                        connection
                    )

                backendCommand.Parameters.AddWithValue("BackendId", disabledBackendId) |> ignore
                let! _ = backendCommand.ExecuteNonQueryAsync()

                let! disabledResult =
                    LibraryScanRepository.createOrGetActiveJob
                        dataSource
                        (newId ())
                        (Some disabledBackendId)
                        requestedAtUtc
                        CancellationToken.None

                let! missingResult =
                    LibraryScanRepository.createOrGetActiveJob
                        dataSource
                        (newId ())
                        (Some missingBackendId)
                        (requestedAtUtc.AddSeconds(1.0))
                        CancellationToken.None

                assertStorageBackendNotFound "a disabled storage backend" disabledResult
                assertStorageBackendNotFound "a missing storage backend" missingResult

                use command =
                    new NpgsqlCommand(
                        """SELECT count(*)
FROM "LibraryScanJobs"
WHERE "StorageBackendId" IN (@DisabledBackendId, @MissingBackendId);""",
                        connection
                    )

                command.Parameters.AddWithValue("DisabledBackendId", disabledBackendId) |> ignore
                command.Parameters.AddWithValue("MissingBackendId", missingBackendId) |> ignore
                let! createdCount = command.ExecuteScalarAsync()
                Assert.That(Convert.ToInt64(createdCount), Is.Zero, "Unavailable explicit backends must not leave a queue row that a worker could claim.")
            })

    [<Test>]
    let ``concurrent createOrGetActiveJob calls elect one creator and return its job for default and explicit backends`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let requestedAtUtc = DateTimeOffset(2026, 7, 10, 17, 20, 0, TimeSpan.Zero)
                let explicitBackendId = newId ()

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use backendCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "StorageBackends" ("Id", "Name", "Type", "S3Bucket", "IsEnabled", "IsDeleted")
VALUES (@BackendId, 'concurrent-s3', 'S3', 'library', true, false);""",
                        connection
                    )

                backendCommand.Parameters.AddWithValue("BackendId", explicitBackendId) |> ignore
                let! _ = backendCommand.ExecuteNonQueryAsync()

                let compete scenario storageBackendId =
                    task {
                        let start = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                        let create () =
                            task {
                                do! start.Task
                                return!
                                    LibraryScanRepository.createOrGetActiveJob
                                        dataSource
                                        (newId ())
                                        storageBackendId
                                        requestedAtUtc
                                        CancellationToken.None
                            }

                        let requests = [| create (); create () |]
                        start.SetResult(())
                        let! results = Task.WhenAll(requests)

                        let jobs =
                            results
                            |> Array.map (function
                                | Ok(Created job)
                                | Ok(Existing job) -> job
                                | actual -> Assert.Fail(sprintf "Expected %s concurrent requests to resolve to a scan job, but got %A." scenario actual); Unchecked.defaultof<_>)

                        let creators =
                            results
                            |> Array.filter (function
                                | Ok(Created _) -> true
                                | _ -> false)

                        let existingJobs =
                            results
                            |> Array.filter (function
                                | Ok(Existing _) -> true
                                | _ -> false)

                        Assert.That(creators, Has.Length.EqualTo(1), sprintf "%s must have exactly one accepted creator." scenario)
                        Assert.That(existingJobs, Has.Length.EqualTo(1), sprintf "%s must turn the competing request into an idempotent retry." scenario)
                        Assert.That(jobs |> Array.map _.Id |> Array.distinct, Has.Length.EqualTo(1), sprintf "%s callers must receive the same scan job id." scenario)
                    }

                do! compete "the default backend" None
                do! compete "an explicit backend" (Some explicitBackendId)

                use countCommand =
                    new NpgsqlCommand(
                        """SELECT
    count(*) FILTER (WHERE "StorageBackendId" IS NULL),
    count(*) FILTER (WHERE "StorageBackendId" = @BackendId)
FROM "LibraryScanJobs"
WHERE "IsDeleted" = false
  AND "Status" IN ('Queued', 'Running');""",
                        connection
                    )

                countCommand.Parameters.AddWithValue("BackendId", explicitBackendId) |> ignore
                let! reader = countCommand.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetInt64(0), Is.EqualTo(1L), "The partial uniqueness index must protect concurrent default scans.")
                Assert.That(reader.GetInt64(1), Is.EqualTo(1L), "The partial uniqueness index must protect concurrent explicit scans.")
            })

    [<Test>]
    let ``getJobStatus projects scan lifecycle fields and hides soft-deleted jobs`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let jobId = newId ()
                let deletedJobId = newId ()
                let requestedAtUtc = DateTimeOffset(2026, 7, 10, 17, 30, 0, TimeSpan.Zero)
                let startedAtUtc = requestedAtUtc.AddSeconds(5.0)
                let finishedAtUtc = startedAtUtc.AddSeconds(7.0)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "LibraryScanJobs" (
    "Id", "Status", "DiscoveredCount", "RequestedAtUtc", "StartedAtUtc", "FinishedAtUtc", "FailureReason", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES
    (@JobId, 'Failed', 17, @RequestedAtUtc, @StartedAtUtc, @FinishedAtUtc, 'scanner lost storage', false, @RequestedAtUtc, @FinishedAtUtc),
    (@DeletedJobId, 'Completed', 99, @RequestedAtUtc, NULL, @FinishedAtUtc, NULL, true, @RequestedAtUtc, @FinishedAtUtc);""",
                        connection
                    )

                insertCommand.Parameters.AddWithValue("JobId", jobId) |> ignore
                insertCommand.Parameters.AddWithValue("DeletedJobId", deletedJobId) |> ignore
                insertCommand.Parameters.AddWithValue("RequestedAtUtc", requestedAtUtc) |> ignore
                insertCommand.Parameters.AddWithValue("StartedAtUtc", startedAtUtc) |> ignore
                insertCommand.Parameters.AddWithValue("FinishedAtUtc", finishedAtUtc) |> ignore
                let! _ = insertCommand.ExecuteNonQueryAsync()

                let! statusResult = LibraryScanRepository.getJobStatus dataSource jobId CancellationToken.None
                let projected = status "the persisted failed job" statusResult
                Assert.That(projected.Id, Is.EqualTo(jobId))
                Assert.That(Option.isNone projected.StorageBackendId, Is.True)
                Assert.That(projected.Status, Is.EqualTo("Failed"))
                Assert.That(projected.DiscoveredCount, Is.EqualTo(17))
                Assert.That(projected.RequestedAtUtc, Is.EqualTo(requestedAtUtc))
                Assert.That(projected.StartedAtUtc, Is.EqualTo(Some startedAtUtc))
                Assert.That(projected.FinishedAtUtc, Is.EqualTo(Some finishedAtUtc))
                Assert.That(projected.FailureReason, Is.EqualTo(Some "scanner lost storage"))

                let! deletedStatus = LibraryScanRepository.getJobStatus dataSource deletedJobId CancellationToken.None
                match deletedStatus with
                | Ok None -> ()
                | actual -> Assert.Fail(sprintf "Normal scan-status reads must not expose soft-deleted jobs, but got %A." actual)
            })

    [<Test>]
    let ``fenced discovery count follows a unique TrackFile insert and rejects stale or completed claims`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let jobId = newId ()
                let owner = newId ()
                let staleOwner = newId ()
                let requestedAtUtc = DateTimeOffset(2026, 7, 10, 17, 40, 0, TimeSpan.Zero)
                let firstTrack = discoveredTrack None "/library/count-once.mp3" requestedAtUtc
                let duplicateTrack = discoveredTrack None "/library/count-once.mp3" (requestedAtUtc.AddSeconds(1.0))

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertJobCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "LibraryScanJobs" (
    "Id", "Status", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc", "DiscoveredCount", "RequestedAtUtc", "StartedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (@JobId, 'Running', @Owner, 3, @LeaseExpiresAtUtc, 0, @RequestedAtUtc, @RequestedAtUtc, false, @RequestedAtUtc, @RequestedAtUtc);""",
                        connection
                    )

                insertJobCommand.Parameters.AddWithValue("JobId", jobId) |> ignore
                insertJobCommand.Parameters.AddWithValue("Owner", owner) |> ignore
                insertJobCommand.Parameters.AddWithValue("LeaseExpiresAtUtc", requestedAtUtc.AddMinutes(1.0)) |> ignore
                insertJobCommand.Parameters.AddWithValue("RequestedAtUtc", requestedAtUtc) |> ignore
                let! _ = insertJobCommand.ExecuteNonQueryAsync()

                let discoverAndCount candidate =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun transactionConnection transaction cancellationToken ->
                            task {
                                let! inserted =
                                    LibraryScanRepository.insertDiscoveredTrackInTransaction
                                        transactionConnection
                                        transaction
                                        candidate
                                        cancellationToken

                                match inserted with
                                | Error error -> return Error error
                                | Ok false -> return Ok false
                                | Ok true ->
                                    return!
                                        LibraryScanRepository.incrementDiscoveredCountInTransaction
                                            transactionConnection
                                            transaction
                                            jobId
                                            owner
                                            3
                                            candidate.DiscoveredAtUtc
                                            cancellationToken
                            })
                        CancellationToken.None

                let! firstDiscovery = discoverAndCount firstTrack
                assertOkTrue "the owned running claim after its unique TrackFile insert" firstDiscovery

                let! duplicateDiscovery = discoverAndCount duplicateTrack
                assertOkFalse "the duplicate TrackFile path without a new discovery" duplicateDiscovery

                let! staleOwnerIncrement =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun transactionConnection transaction cancellationToken ->
                            LibraryScanRepository.incrementDiscoveredCountInTransaction
                                transactionConnection
                                transaction
                                jobId
                                staleOwner
                                3
                                (requestedAtUtc.AddSeconds(2.0))
                                cancellationToken)
                        CancellationToken.None

                let! staleAttemptIncrement =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun transactionConnection transaction cancellationToken ->
                            LibraryScanRepository.incrementDiscoveredCountInTransaction
                                transactionConnection
                                transaction
                                jobId
                                owner
                                2
                                (requestedAtUtc.AddSeconds(3.0))
                                cancellationToken)
                        CancellationToken.None

                assertOkFalse "a different scanner owner" staleOwnerIncrement
                assertOkFalse "a stale scanner attempt" staleAttemptIncrement

                use completeCommand =
                    new NpgsqlCommand(
                        """UPDATE "LibraryScanJobs"
SET "Status" = 'Completed', "ClaimOwner" = NULL, "UpdatedAtUtc" = @FinishedAtUtc
WHERE "Id" = @JobId;""",
                        connection
                    )

                completeCommand.Parameters.AddWithValue("FinishedAtUtc", requestedAtUtc.AddSeconds(4.0)) |> ignore
                completeCommand.Parameters.AddWithValue("JobId", jobId) |> ignore
                let! _ = completeCommand.ExecuteNonQueryAsync()

                let! completedIncrement =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun transactionConnection transaction cancellationToken ->
                            LibraryScanRepository.incrementDiscoveredCountInTransaction
                                transactionConnection
                                transaction
                                jobId
                                owner
                                3
                                (requestedAtUtc.AddSeconds(5.0))
                                cancellationToken)
                        CancellationToken.None

                assertOkFalse "a completed scan job" completedIncrement

                use verificationCommand =
                    new NpgsqlCommand(
                        """SELECT
    (SELECT "DiscoveredCount" FROM "LibraryScanJobs" WHERE "Id" = @JobId),
    count(*) FROM "TrackFiles" WHERE "IsDeleted" = false AND "StoragePath" = @StoragePath;""",
                        connection
                    )

                verificationCommand.Parameters.AddWithValue("JobId", jobId) |> ignore
                verificationCommand.Parameters.AddWithValue("StoragePath", firstTrack.StoragePath) |> ignore
                let! reader = verificationCommand.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetInt32(0), Is.EqualTo(1), "Only the unique TrackFile insert may advance the scan count.")
                Assert.That(reader.GetInt64(1), Is.EqualTo(1L), "Duplicate discovery must not retain another active TrackFile.")
            })
