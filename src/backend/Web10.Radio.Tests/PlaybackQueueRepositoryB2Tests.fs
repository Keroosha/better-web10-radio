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

    let private claim description result =
        match result with
        | Ok(Some item) -> item
        | actual -> Assert.Fail(sprintf "Expected %s to claim a queue item, but got %A." description actual); Unchecked.defaultof<_>

    let private assertOkTrue description result =
        match result with
        | Ok true -> ()
        | actual -> Assert.Fail(sprintf "Expected %s to return Ok true, but got %A." description actual)

    let private assertOkFalse description result =
        match result with
        | Ok false -> ()
        | actual -> Assert.Fail(sprintf "Expected %s to return Ok false, but got %A." description actual)

    [<Test>]
    let ``Playing to Played releases the active queue slot for the next ordered item`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let firstTrackId = newId ()
                let secondTrackId = newId ()
                let firstQueueItemId = newId ()
                let secondQueueItemId = newId ()
                let firstOwner = newId ()
                let secondOwner = newId ()
                let requestedAtUtc = DateTimeOffset(2026, 7, 10, 13, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@FirstTrackId, 'First title', 'Artist', false),
       (@SecondTrackId, 'Second title', 'Artist', false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc")
VALUES (@FirstQueueItemId, @FirstTrackId, 'playlist', 'Queued', 0, @RequestedAtUtc),
       (@SecondQueueItemId, @SecondTrackId, 'playlist', 'Queued', 0, @SecondRequestedAtUtc);""",
                        connection
                    )

                insertCommand.Parameters.AddWithValue("FirstTrackId", firstTrackId) |> ignore
                insertCommand.Parameters.AddWithValue("SecondTrackId", secondTrackId) |> ignore
                insertCommand.Parameters.AddWithValue("FirstQueueItemId", firstQueueItemId) |> ignore
                insertCommand.Parameters.AddWithValue("SecondQueueItemId", secondQueueItemId) |> ignore
                insertCommand.Parameters.AddWithValue("RequestedAtUtc", requestedAtUtc) |> ignore
                insertCommand.Parameters.AddWithValue("SecondRequestedAtUtc", requestedAtUtc.AddSeconds(1.0)) |> ignore
                let! _ = insertCommand.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let firstClaimedAtUtc = requestedAtUtc.AddSeconds(10.0)
                let firstClaim =
                    PlaybackQueueRepository.claimNextDetailed dataSource firstOwner firstClaimedAtUtc (firstClaimedAtUtc.AddSeconds(30.0)) CancellationToken.None

                let! firstClaim = firstClaim
                let firstItem = claim "the first item" firstClaim
                Assert.That(firstItem.QueueItemId, Is.EqualTo(firstQueueItemId))

                let! started =
                    PlaybackQueueRepository.markPlaying
                        dataSource
                        firstItem.QueueItemId
                        firstItem.ClaimOwner
                        firstItem.ClaimAttempt
                        (requestedAtUtc.AddSeconds(11.0))
                        (requestedAtUtc.AddSeconds(41.0))
                        CancellationToken.None

                assertOkTrue "the owned Playing transition" started

                let! played =
                    PlaybackQueueRepository.markPlayed
                        dataSource
                        firstItem.QueueItemId
                        firstItem.ClaimOwner
                        firstItem.ClaimAttempt
                        (requestedAtUtc.AddSeconds(12.0))
                        CancellationToken.None

                assertOkTrue "the owned Played completion" played

                let! secondClaim =
                    PlaybackQueueRepository.claimNextDetailed
                        dataSource
                        secondOwner
                        (requestedAtUtc.AddSeconds(13.0))
                        (requestedAtUtc.AddSeconds(43.0))
                        CancellationToken.None

                let secondItem = claim "the next item after a successful completion" secondClaim
                Assert.That(secondItem.QueueItemId, Is.EqualTo(secondQueueItemId))

                use command =
                    new NpgsqlCommand(
                        """SELECT "Status", "FinishedAtUtc"
FROM "PlaybackQueue"
WHERE "Id" = @QueueItemId;""",
                        connection
                    )

                command.Parameters.AddWithValue("QueueItemId", firstQueueItemId) |> ignore
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Played"))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(1), Is.EqualTo(requestedAtUtc.AddSeconds(12.0)))
            })

    [<Test>]
    let ``expired pre-start claim is reclaimed, stale owner cannot start, and authoritative failure releases next item`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let recoverableTrackId = newId ()
                let nextTrackId = newId ()
                let recoverableQueueItemId = newId ()
                let nextQueueItemId = newId ()
                let firstOwner = newId ()
                let replacementOwner = newId ()
                let nextOwner = newId ()
                let requestedAtUtc = DateTimeOffset(2026, 7, 10, 14, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@RecoverableTrackId, 'Recoverable title', 'Artist', false),
       (@NextTrackId, 'Next title', 'Artist', false);
INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc")
VALUES (@RecoverableQueueItemId, @RecoverableTrackId, 'playlist', 'Queued', 0, @RequestedAtUtc),
       (@NextQueueItemId, @NextTrackId, 'playlist', 'Queued', 0, @NextRequestedAtUtc);""",
                        connection
                    )

                for name, value in
                    [ "RecoverableTrackId", box recoverableTrackId
                      "NextTrackId", box nextTrackId
                      "RecoverableQueueItemId", box recoverableQueueItemId
                      "NextQueueItemId", box nextQueueItemId
                      "RequestedAtUtc", box requestedAtUtc
                      "NextRequestedAtUtc", box (requestedAtUtc.AddSeconds(1.0)) ] do
                    insertCommand.Parameters.AddWithValue(name, value) |> ignore

                let! _ = insertCommand.ExecuteNonQueryAsync()
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! firstClaim =
                    PlaybackQueueRepository.claimNextDetailed dataSource firstOwner requestedAtUtc (requestedAtUtc.AddSeconds(30.0)) CancellationToken.None

                let firstItem = claim "the original pre-start worker" firstClaim
                Assert.That(firstItem.QueueItemId, Is.EqualTo(recoverableQueueItemId))

                let! replacementClaim =
                    PlaybackQueueRepository.claimNextDetailed
                        dataSource
                        replacementOwner
                        (requestedAtUtc.AddSeconds(31.0))
                        (requestedAtUtc.AddMinutes(1.0))
                        CancellationToken.None

                let replacementItem = claim "the worker reclaiming an expired pre-start claim" replacementClaim
                Assert.That(replacementItem.QueueItemId, Is.EqualTo(recoverableQueueItemId))
                Assert.That(replacementItem.ClaimAttempt, Is.EqualTo(firstItem.ClaimAttempt + 1))

                let! staleStart =
                    PlaybackQueueRepository.markPlaying
                        dataSource
                        recoverableQueueItemId
                        firstItem.ClaimOwner
                        firstItem.ClaimAttempt
                        (requestedAtUtc.AddSeconds(32.0))
                        (requestedAtUtc.AddMinutes(1.0))
                        CancellationToken.None

                assertOkFalse "the stale pre-start owner's Playing transition" staleStart

                let! staleFailure =
                    PlaybackQueueRepository.markFailed
                        dataSource
                        recoverableQueueItemId
                        firstItem.ClaimOwner
                        firstItem.ClaimAttempt
                        (requestedAtUtc.AddSeconds(32.0))
                        "old worker failed"
                        CancellationToken.None

                assertOkFalse "the stale pre-start owner's Failed transition" staleFailure

                let! replacementFailed =
                    PlaybackQueueRepository.markFailed
                        dataSource
                        recoverableQueueItemId
                        replacementItem.ClaimOwner
                        replacementItem.ClaimAttempt
                        (requestedAtUtc.AddSeconds(33.0))
                        "replacement cache failure"
                        CancellationToken.None

                assertOkTrue "the authoritative replacement failure transition" replacementFailed

                let! nextClaim =
                    PlaybackQueueRepository.claimNextDetailed
                        dataSource
                        nextOwner
                        (requestedAtUtc.AddSeconds(34.0))
                        (requestedAtUtc.AddMinutes(2.0))
                        CancellationToken.None

                let nextItem = claim "the ordered item released by authoritative failure" nextClaim
                Assert.That(nextItem.QueueItemId, Is.EqualTo(nextQueueItemId))
                Assert.That(nextItem.ClaimAttempt, Is.EqualTo(1))

                use command =
                    new NpgsqlCommand(
                        """SELECT "Status", "ClaimAttempt", "FailureReason", "FinishedAtUtc"
FROM "PlaybackQueue"
WHERE "Id" = @QueueItemId;""",
                        connection
                    )

                command.Parameters.AddWithValue("QueueItemId", recoverableQueueItemId) |> ignore
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Failed"))
                Assert.That(reader.GetInt32(1), Is.EqualTo(replacementItem.ClaimAttempt))
                Assert.That(reader.GetString(2), Is.EqualTo("replacement cache failure"))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(3), Is.EqualTo(requestedAtUtc.AddSeconds(33.0)))
            })

    [<Test>]
    let ``cached file whose parent Track is soft deleted is not playable`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use command = new NpgsqlCommand("""INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted") VALUES (@TrackId, 'Deleted parent', 'Regression', true); INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "CachePath", "IsCached", "IsDeleted") VALUES (@TrackFileId, @TrackId, '/library/deleted.mp3', '/cache/deleted.mp3', true, false);""", connection)
                command.Parameters.AddWithValue("TrackId", trackId) |> ignore
                command.Parameters.AddWithValue("TrackFileId", newId ()) |> ignore
                let! _ = command.ExecuteNonQueryAsync()
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! result = PlaybackQueueRepository.findCachedTrackFile dataSource trackId CancellationToken.None
                match result with
                | Ok None -> ()
                | actual -> Assert.Fail(sprintf "Expected an active file with a soft-deleted parent Track to be excluded, but got %A." actual)
            })
