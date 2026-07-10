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
