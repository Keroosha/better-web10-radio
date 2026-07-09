namespace Web10.Radio.Tests

open System
open System.Threading
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Database
open Web10.Radio.Database.Repositories

module LibraryScanRepositoryTests =
    let private newId () =
        (UuidV7IdGenerator() :> IIdGenerator).NewId()

    let private discoveredTrack trackId trackFileId storagePath discoveredAtUtc =
        { TrackId = trackId
          TrackFileId = trackFileId
          StorageBackendId = None
          StoragePath = storagePath
          CachePath = Some storagePath
          IsCached = true
          Title = "Duplicate title"
          Artist = "Duplicate artist"
          ContentType = Some "audio/mpeg"
          SizeBytes = Some 1234L
          DiscoveredAtUtc = discoveredAtUtc }

    [<Test>]
    let ``claimNextJob and completeJob persist running then completed state`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let jobId = newId ()
                let requestedAtUtc = DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero)
                let startedAtUtc = requestedAtUtc.AddMinutes(1.0)
                let finishedAtUtc = requestedAtUtc.AddMinutes(2.0)

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
                let! claimResult = LibraryScanRepository.claimNextJob dataSource startedAtUtc CancellationToken.None
                match claimResult with
                | Ok(Some claimedJob) ->
                    Assert.That(claimedJob.Id, Is.EqualTo(jobId))
                    Assert.That(claimedJob.StorageBackendId, Is.EqualTo(None))
                | actual -> Assert.Fail(sprintf "Expected queued job to be claimed, but got %A." actual)

                use runningCommand =
                    new NpgsqlCommand(
                        """SELECT "Status", "StartedAtUtc", "UpdatedAtUtc"
FROM "LibraryScanJobs"
WHERE "Id" = @JobId;""",
                        connection
                    )

                runningCommand.Parameters.AddWithValue("JobId", jobId) |> ignore
                let! runningReader = runningCommand.ExecuteReaderAsync()
                use runningReader = runningReader
                let! hasRunningRow = runningReader.ReadAsync()
                Assert.That(hasRunningRow, Is.True)
                Assert.That(runningReader.GetString(0), Is.EqualTo("Running"))
                Assert.That(runningReader.GetFieldValue<DateTimeOffset>(1), Is.EqualTo(startedAtUtc))
                Assert.That(runningReader.GetFieldValue<DateTimeOffset>(2), Is.EqualTo(startedAtUtc))
                do! runningReader.CloseAsync()

                let! completeResult = LibraryScanRepository.completeJob dataSource jobId finishedAtUtc CancellationToken.None
                match completeResult with
                | Ok () -> ()
                | actual -> Assert.Fail(sprintf "Expected job completion to succeed, but got %A." actual)

                use completedCommand =
                    new NpgsqlCommand(
                        """SELECT "Status", "FinishedAtUtc", "UpdatedAtUtc"
FROM "LibraryScanJobs"
WHERE "Id" = @JobId;""",
                        connection
                    )

                completedCommand.Parameters.AddWithValue("JobId", jobId) |> ignore
                let! completedReader = completedCommand.ExecuteReaderAsync()
                use completedReader = completedReader
                let! hasCompletedRow = completedReader.ReadAsync()
                Assert.That(hasCompletedRow, Is.True)
                Assert.That(completedReader.GetString(0), Is.EqualTo("Completed"))
                Assert.That(completedReader.GetFieldValue<DateTimeOffset>(1), Is.EqualTo(finishedAtUtc))
                Assert.That(completedReader.GetFieldValue<DateTimeOffset>(2), Is.EqualTo(finishedAtUtc))
            })

    [<Test>]
    let ``insertDiscoveredTrackInTransaction inserts first file and suppresses duplicate storage path`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let storagePath = "/music/Duplicate artist - Duplicate title.mp3"
                let discoveredAtUtc = DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero)

                let firstTrack = discoveredTrack (newId ()) (newId ()) storagePath discoveredAtUtc
                let duplicateTrack = discoveredTrack (newId ()) (newId ()) storagePath (discoveredAtUtc.AddMinutes(1.0))

                let! firstResult =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction cancellationToken ->
                            LibraryScanRepository.insertDiscoveredTrackInTransaction connection transaction firstTrack cancellationToken)
                        CancellationToken.None

                let! duplicateResult =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction cancellationToken ->
                            LibraryScanRepository.insertDiscoveredTrackInTransaction connection transaction duplicateTrack cancellationToken)
                        CancellationToken.None

                match firstResult with
                | Ok true -> ()
                | actual -> Assert.Fail(sprintf "Expected first discovered track insert to succeed, but got %A." actual)

                match duplicateResult with
                | Ok false -> ()
                | actual -> Assert.Fail(sprintf "Expected duplicate discovered track insert to be suppressed, but got %A." actual)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use command =
                    new NpgsqlCommand(
                        """SELECT
    (SELECT count(*) FROM "Tracks" WHERE "Title" = @Title AND "Artist" = @Artist AND "IsDeleted" = false),
    (SELECT count(*) FROM "TrackFiles" WHERE "StorageBackendId" IS NULL AND "StoragePath" = @StoragePath AND "IsDeleted" = false),
    (SELECT "TrackId" FROM "TrackFiles" WHERE "StorageBackendId" IS NULL AND "StoragePath" = @StoragePath AND "IsDeleted" = false LIMIT 1),
    (SELECT "CachePath" FROM "TrackFiles" WHERE "StorageBackendId" IS NULL AND "StoragePath" = @StoragePath AND "IsDeleted" = false LIMIT 1);""",
                        connection
                    )

                command.Parameters.AddWithValue("Title", firstTrack.Title) |> ignore
                command.Parameters.AddWithValue("Artist", firstTrack.Artist) |> ignore
                command.Parameters.AddWithValue("StoragePath", storagePath) |> ignore
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetInt64(0), Is.EqualTo(1L))
                Assert.That(reader.GetInt64(1), Is.EqualTo(1L))
                Assert.That(reader.GetGuid(2), Is.EqualTo(firstTrack.TrackId))
                Assert.That(reader.GetString(3), Is.EqualTo(storagePath))
            })
