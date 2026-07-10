namespace Web10.Radio.Tests

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open NUnit.Framework
open Web10.Radio.Database.Repositories

module PlaybackQueueRepositoryTests =
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
VALUES (@FirstQueueId, @TrackId, 'playlist', 'Queued', 0, @RequestedAtUtc),
       (@SecondQueueId, @TrackId, 'playlist', 'Queued', 0, @SecondRequestedAtUtc);""",
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
