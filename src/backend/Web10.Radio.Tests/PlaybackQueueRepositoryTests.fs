namespace Web10.Radio.Tests

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open NUnit.Framework
open Web10.Radio.Database.Repositories

module PlaybackQueueRepositoryTests =
    [<Test>]
    let ``concurrent queue claims never return the same row`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = Guid.NewGuid()
                let firstQueueId = Guid.NewGuid()
                let secondQueueId = Guid.NewGuid()
                let firstRequestedAt = DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero)
                let secondRequestedAt = firstRequestedAt.AddMinutes(1.0)
                let fixedClaimedAt = DateTimeOffset(2026, 7, 8, 0, 1, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackId, 'Queue title', 'Queue artist', false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc")
VALUES (@FirstQueueId, @TrackId, 'playlist', 'Queued', 0, @FirstRequestedAt),
       (@SecondQueueId, @TrackId, 'playlist', 'Queued', 0, @SecondRequestedAt);""",
                        connection
                    )

                insertCommand.Parameters.AddWithValue("TrackId", trackId) |> ignore
                insertCommand.Parameters.AddWithValue("FirstQueueId", firstQueueId) |> ignore
                insertCommand.Parameters.AddWithValue("SecondQueueId", secondQueueId) |> ignore
                insertCommand.Parameters.AddWithValue("FirstRequestedAt", firstRequestedAt) |> ignore
                insertCommand.Parameters.AddWithValue("SecondRequestedAt", secondRequestedAt) |> ignore
                let! _ = insertCommand.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)

                let claimTasks =
                    [| for _ in 1..4 ->
                           PlaybackQueueRepository.claimNext dataSource fixedClaimedAt CancellationToken.None |]

                let! results = Task.WhenAll(claimTasks)
                let claimedIds = results |> Array.choose id
                let noneCount = results |> Array.filter Option.isNone |> Array.length

                Assert.That(Array.length claimedIds, Is.EqualTo(2))
                Assert.That(noneCount, Is.EqualTo(2))
                Assert.That(claimedIds |> Array.distinct |> Array.length, Is.EqualTo(2))

                use claimedCountCommand =
                    new NpgsqlCommand(
                        """SELECT count(*)
FROM "PlaybackQueue"
WHERE "Status" = 'Claimed'
  AND "ClaimedAtUtc" = @ClaimedAtUtc;""",
                        connection
                    )

                claimedCountCommand.Parameters.AddWithValue("ClaimedAtUtc", fixedClaimedAt) |> ignore
                let! claimedCount = claimedCountCommand.ExecuteScalarAsync()
                Assert.That(Convert.ToInt32(claimedCount), Is.EqualTo(2))
            })
