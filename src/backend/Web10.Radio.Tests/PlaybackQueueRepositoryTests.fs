namespace Web10.Radio.Tests

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Dodo.Primitives
open NUnit.Framework
open Web10.Radio.Database.Repositories

module PlaybackQueueRepositoryTests =
    let private newId () =
        Uuid.CreateVersion7().ToGuidBigEndian()
    [<Test>]
    let ``simultaneous queue claims create exactly one active owner`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = Guid.NewGuid()
                let firstQueueId = Guid.NewGuid()
                let secondQueueId = Guid.NewGuid()
                let requestedAtUtc = DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero)
                let claimedAtUtc = requestedAtUtc.AddMinutes(1.0)
                let leaseExpiresAtUtc = claimedAtUtc.AddSeconds(30.0)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackId, 'Queue title', 'Queue artist', false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc")
VALUES (@FirstQueueId, @TrackId, 'fallback', 'Queued', 0, @RequestedAtUtc),
       (@SecondQueueId, @TrackId, 'fallback', 'Queued', 0, @SecondRequestedAtUtc);""",
                        connection
                    )

                insertCommand.Parameters.AddWithValue("TrackId", trackId) |> ignore
                insertCommand.Parameters.AddWithValue("FirstQueueId", firstQueueId) |> ignore
                insertCommand.Parameters.AddWithValue("SecondQueueId", secondQueueId) |> ignore
                insertCommand.Parameters.AddWithValue("RequestedAtUtc", requestedAtUtc) |> ignore
                insertCommand.Parameters.AddWithValue("SecondRequestedAtUtc", requestedAtUtc.AddMilliseconds(1.0)) |> ignore
                let! _ = insertCommand.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let start = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let owners = [| Guid.NewGuid(); Guid.NewGuid(); Guid.NewGuid() |]

                let claim owner =
                    task {
                        do! start.Task
                        return! PlaybackQueueRepository.claimNextDetailed dataSource owner claimedAtUtc leaseExpiresAtUtc CancellationToken.None
                    }

                let claimTasks = owners |> Array.map claim
                start.SetResult(())
                let! results = Task.WhenAll(claimTasks)

                let claimed =
                    results
                    |> Array.choose (function
                        | Ok(Some item) -> Some item
                        | Ok None -> None
                        | Error error -> Assert.Fail(sprintf "Expected claim to complete without repository error, but got %A." error); None)

                Assert.That(claimed, Has.Length.EqualTo(1), "The one-active-item invariant must hold even when all callers are released together.")
                Assert.That(claimed[0].QueueItemId, Is.EqualTo(firstQueueId), "The ordered head is the only item eligible for the winning claim.")

                use stateCommand =
                    new NpgsqlCommand(
                        """SELECT
    count(*) FILTER (WHERE "Status" IN ('Claimed', 'Playing')),
    count(*) FILTER (WHERE "Status" = 'Queued')
FROM "PlaybackQueue"
WHERE "IsDeleted" = false;""",
                        connection
                    )

                let! reader = stateCommand.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetInt64(0), Is.EqualTo(1L))
                Assert.That(reader.GetInt64(1), Is.EqualTo(1L))
            })

    [<Test>]
    let ``current assignment is absent when no fenced Playing row exists`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let queuedItemId = newId ()
                let claimedItemId = newId ()
                let claimOwner = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 10, 20, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackId, 'Queued but not current', 'Regression', false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc")
VALUES (@QueuedItemId, @TrackId, 'fallback', 'Queued', 0, @NowUtc, NULL, 0, NULL),
       (@ClaimedItemId, @TrackId, 'fallback', 'Claimed', 0, @NowUtc, @ClaimOwner, 1, @LeaseExpiresAtUtc);""",
                        connection
                    )

                for name, value in
                    [ "TrackId", box trackId
                      "QueuedItemId", box queuedItemId
                      "ClaimedItemId", box claimedItemId
                      "ClaimOwner", box claimOwner
                      "NowUtc", box nowUtc
                      "LeaseExpiresAtUtc", box (nowUtc.AddSeconds(30.0)) ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore

                let! _ = setup.ExecuteNonQueryAsync()
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! result = PlaybackQueueRepository.getCurrentAssignment dataSource CancellationToken.None

                match result with
                | Ok None -> ()
                | actual ->
                    Assert.Fail(
                        sprintf
                            "Expected no current assignment when the queue has only Queued or Claimed rows, but got %A."
                            actual
                    )
            })

    [<Test>]
    let ``current assignment joins a fenced Playing row and canonicalizes nullable playback metadata`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let trackFileId = newId ()
                let queueItemId = newId ()
                let claimOwner = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 10, 20, 5, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """ALTER TABLE "Tracks" ALTER COLUMN "Title" DROP NOT NULL;
ALTER TABLE "Tracks" ALTER COLUMN "Artist" DROP NOT NULL;

INSERT INTO "Tracks" ("Id", "Title", "Artist", "DurationMs", "IsDeleted")
VALUES (@TrackId, NULL, NULL, NULL, false);

INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "CachePath", "ContentType", "IsCached", "IsDeleted")
VALUES (@TrackFileId, @TrackId, '/library/current.ogg', '/cache/current.ogg', NULL, true, false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc")
VALUES (@QueueItemId, @TrackId, 'fallback', 'Playing', 0, @NowUtc, @ClaimOwner, 7, @LeaseExpiresAtUtc);""",
                        connection
                    )

                for name, value in
                    [ "TrackId", box trackId
                      "TrackFileId", box trackFileId
                      "QueueItemId", box queueItemId
                      "ClaimOwner", box claimOwner
                      "NowUtc", box nowUtc
                      "LeaseExpiresAtUtc", box (nowUtc.AddSeconds(30.0)) ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore

                let! _ = setup.ExecuteNonQueryAsync()
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! result = PlaybackQueueRepository.getCurrentAssignment dataSource CancellationToken.None

                match result with
                | Ok(Some (assignment: CurrentPlaybackAssignment)) ->
                    Assert.Multiple(fun () ->
                        Assert.That(assignment.QueueItemId, Is.EqualTo(queueItemId))
                        Assert.That(assignment.ClaimOwner, Is.EqualTo(claimOwner))
                        Assert.That(assignment.ClaimAttempt, Is.EqualTo(7))
                        Assert.That(assignment.TrackId, Is.EqualTo(trackId))
                        Assert.That(assignment.CachePath, Is.EqualTo("/cache/current.ogg"))
                        Assert.That(assignment.ContentType, Is.EqualTo("audio/mpeg"))
                        Assert.That(assignment.Title, Is.EqualTo(String.Empty))
                        Assert.That(assignment.Artist, Is.EqualTo(String.Empty))
                        Assert.That(assignment.DurationMs, Is.EqualTo(0)))
                | actual ->
                    Assert.Fail(sprintf "Expected the fenced Playing row to project a complete stream assignment, but got %A." actual)
            })
