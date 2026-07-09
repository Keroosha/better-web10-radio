namespace Web10.Radio.Tests

open System
open System.Threading
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Database.Repositories

module PlaybackQueueRepositoryB2Tests =
    let private newId () =
        (UuidV7IdGenerator() :> IIdGenerator).NewId()

    [<Test>]
    let ``detailed claim finds newest cached file and markPlaying persists started state`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let olderTrackFileId = newId ()
                let newestTrackFileId = newId ()
                let queueItemId = newId ()
                let requestedAtUtc = DateTimeOffset(2026, 7, 8, 14, 0, 0, TimeSpan.Zero)
                let olderUpdatedAtUtc = requestedAtUtc.AddMinutes(-10.0)
                let newestUpdatedAtUtc = requestedAtUtc.AddMinutes(-1.0)
                let claimedAtUtc = requestedAtUtc.AddSeconds(30.0)
                let startedAtUtc = requestedAtUtc.AddSeconds(45.0)
                let newestCachePath = "/cache/newest.mp3"

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackId, 'Cached title', 'Cached artist', false);

INSERT INTO "TrackFiles" (
    "Id", "TrackId", "StoragePath", "CachePath", "ContentType", "SizeBytes", "IsCached", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted"
)
VALUES
    (@OlderTrackFileId, @TrackId, '/music/older.mp3', '/cache/older.mp3', 'audio/mpeg', 1000, true, @OlderUpdatedAtUtc, @OlderUpdatedAtUtc, false),
    (@NewestTrackFileId, @TrackId, '/music/newest.mp3', @NewestCachePath, 'audio/mpeg', 1000, true, @NewestUpdatedAtUtc, @NewestUpdatedAtUtc, false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES (@QueueItemId, @TrackId, 'playlist', 'Queued', 10, @RequestedAtUtc, @RequestedAtUtc, @RequestedAtUtc, false);""",
                        connection
                    )

                insertCommand.Parameters.AddWithValue("TrackId", trackId) |> ignore
                insertCommand.Parameters.AddWithValue("OlderTrackFileId", olderTrackFileId) |> ignore
                insertCommand.Parameters.AddWithValue("NewestTrackFileId", newestTrackFileId) |> ignore
                insertCommand.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                insertCommand.Parameters.AddWithValue("OlderUpdatedAtUtc", olderUpdatedAtUtc) |> ignore
                insertCommand.Parameters.AddWithValue("NewestUpdatedAtUtc", newestUpdatedAtUtc) |> ignore
                insertCommand.Parameters.AddWithValue("NewestCachePath", newestCachePath) |> ignore
                insertCommand.Parameters.AddWithValue("RequestedAtUtc", requestedAtUtc) |> ignore
                let! _ = insertCommand.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! claimResult = PlaybackQueueRepository.claimNextDetailed dataSource claimedAtUtc CancellationToken.None
                match claimResult with
                | Ok(Some claimedItem) ->
                    Assert.That(claimedItem.QueueItemId, Is.EqualTo(queueItemId))
                    Assert.That(claimedItem.TrackId, Is.EqualTo(Some trackId))
                | actual -> Assert.Fail(sprintf "Expected queue item to be claimed, but got %A." actual)

                let! cacheResult = PlaybackQueueRepository.findCachedTrackFile dataSource trackId CancellationToken.None
                match cacheResult with
                | Ok(Some cachePath) -> Assert.That(cachePath, Is.EqualTo(newestCachePath))
                | actual -> Assert.Fail(sprintf "Expected newest cache path, but got %A." actual)

                let! markPlayingResult = PlaybackQueueRepository.markPlaying dataSource queueItemId startedAtUtc CancellationToken.None
                match markPlayingResult with
                | Ok true -> ()
                | actual -> Assert.Fail(sprintf "Expected markPlaying to update claimed row, but got %A." actual)

                use command =
                    new NpgsqlCommand(
                        """SELECT "Status", "ClaimedAtUtc", "StartedAtUtc", "UpdatedAtUtc"
FROM "PlaybackQueue"
WHERE "Id" = @QueueItemId;""",
                        connection
                    )

                command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Playing"))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(1), Is.EqualTo(claimedAtUtc))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(2), Is.EqualTo(startedAtUtc))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(3), Is.EqualTo(startedAtUtc))
            })

    [<Test>]
    let ``cache miss markFailed persists failure reason on claimed queue item`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let queueItemId = newId ()
                let requestedAtUtc = DateTimeOffset(2026, 7, 8, 15, 0, 0, TimeSpan.Zero)
                let claimedAtUtc = requestedAtUtc.AddSeconds(30.0)
                let finishedAtUtc = requestedAtUtc.AddSeconds(45.0)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackId, 'Missing cache title', 'Missing cache artist', false);

INSERT INTO "PlaybackQueue" (
    "Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "ClaimedAtUtc", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted"
)
VALUES (@QueueItemId, @TrackId, 'playlist', 'Claimed', 0, @RequestedAtUtc, @ClaimedAtUtc, @RequestedAtUtc, @ClaimedAtUtc, false);""",
                        connection
                    )

                insertCommand.Parameters.AddWithValue("TrackId", trackId) |> ignore
                insertCommand.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                insertCommand.Parameters.AddWithValue("RequestedAtUtc", requestedAtUtc) |> ignore
                insertCommand.Parameters.AddWithValue("ClaimedAtUtc", claimedAtUtc) |> ignore
                let! _ = insertCommand.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! cacheResult = PlaybackQueueRepository.findCachedTrackFile dataSource trackId CancellationToken.None
                match cacheResult with
                | Ok None -> ()
                | actual -> Assert.Fail(sprintf "Expected no cached track file, but got %A." actual)

                let! markFailedResult =
                    PlaybackQueueRepository.markFailed dataSource queueItemId finishedAtUtc "cache path unavailable" CancellationToken.None

                match markFailedResult with
                | Ok true -> ()
                | actual -> Assert.Fail(sprintf "Expected markFailed to update claimed row, but got %A." actual)

                use command =
                    new NpgsqlCommand(
                        """SELECT "Status", "FinishedAtUtc", "FailureReason", "UpdatedAtUtc"
FROM "PlaybackQueue"
WHERE "Id" = @QueueItemId;""",
                        connection
                    )

                command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Failed"))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(1), Is.EqualTo(finishedAtUtc))
                Assert.That(reader.GetString(2), Is.EqualTo("cache path unavailable"))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(3), Is.EqualTo(finishedAtUtc))
            })
