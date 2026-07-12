namespace Web10.Radio.Tests

open System
open System.Threading
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Dodo.Primitives
open Web10.Radio.Database.Repositories

module PlaybackQueueRepositoryB2Tests =
    let private newId () = Uuid.CreateVersion7().ToGuidBigEndian()

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
VALUES (@FirstQueueItemId, @FirstTrackId, 'fallback', 'Queued', 0, @RequestedAtUtc),
       (@SecondQueueItemId, @SecondTrackId, 'fallback', 'Queued', 0, @SecondRequestedAtUtc);""",
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
VALUES (@RecoverableQueueItemId, @RecoverableTrackId, 'fallback', 'Queued', 0, @RequestedAtUtc),
       (@NextQueueItemId, @NextTrackId, 'fallback', 'Queued', 0, @NextRequestedAtUtc);""",
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

    let private assertInserted description result =
        match result with
        | Ok(Some _) -> ()
        | actual -> Assert.Fail(sprintf "Expected %s to insert one playlist queue item, but got %A." description actual)

    let private assertNoop description result =
        match result with
        | Ok None -> ()
        | actual -> Assert.Fail(sprintf "Expected %s to leave the queue unchanged, but got %A." description actual)

    [<Test>]
    let ``idle playlist refill continues after the most recent item and wraps to the first position`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let firstTrackId = newId ()
                let middleTrackId = newId ()
                let lastTrackId = newId ()
                let playlistId = newId ()
                let firstPlaylistItemId = newId ()
                let middlePlaylistItemId = newId ()
                let lastPlaylistItemId = newId ()
                let firstTrackFileId = newId ()
                let middleTrackFileId = newId ()
                let lastTrackFileId = newId ()
                let priorQueueItemId = newId ()
                let nextQueueItemId = newId ()
                let wrappedQueueItemId = newId ()
                let enqueuedAtUtc = DateTimeOffset(2026, 7, 10, 21, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@FirstTrackId, 'First', 'Playlist', false),
       (@MiddleTrackId, 'Middle', 'Playlist', false),
       (@LastTrackId, 'Last', 'Playlist', false);

INSERT INTO "Playlists" ("Id", "Name", "IsActive", "IsDeleted")
VALUES (@PlaylistId, 'Deterministic loop', true, false);

INSERT INTO "PlaylistItems" ("Id", "PlaylistId", "TrackId", "Position", "IsDeleted")
VALUES (@FirstPlaylistItemId, @PlaylistId, @FirstTrackId, 0, false),
       (@MiddlePlaylistItemId, @PlaylistId, @MiddleTrackId, 4, false),
       (@LastPlaylistItemId, @PlaylistId, @LastTrackId, 9, false);

INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "CachePath", "IsCached", "IsDeleted")
VALUES (@FirstTrackFileId, @FirstTrackId, '/library/first.mp3', '/cache/first.mp3', true, false),
       (@MiddleTrackFileId, @MiddleTrackId, '/library/middle.mp3', '/cache/middle.mp3', true, false),
       (@LastTrackFileId, @LastTrackId, '/library/last.mp3', '/cache/last.mp3', true, false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "PlaylistItemId", "PlaylistId", "Source", "Status", "Priority", "RequestedAtUtc", "FinishedAtUtc")
VALUES (@PriorQueueItemId, @MiddleTrackId, @MiddlePlaylistItemId, @PlaylistId, 'playlist', 'Played', 0, @PriorRequestedAtUtc, @PriorFinishedAtUtc);""",
                        connection
                    )

                for name, value in
                    [ "FirstTrackId", box firstTrackId
                      "MiddleTrackId", box middleTrackId
                      "FirstTrackFileId", box firstTrackFileId
                      "MiddleTrackFileId", box middleTrackFileId
                      "LastTrackFileId", box lastTrackFileId
                      "LastTrackId", box lastTrackId
                      "PlaylistId", box playlistId
                      "FirstPlaylistItemId", box firstPlaylistItemId
                      "MiddlePlaylistItemId", box middlePlaylistItemId
                      "LastPlaylistItemId", box lastPlaylistItemId
                      "PriorQueueItemId", box priorQueueItemId
                      "PriorRequestedAtUtc", box enqueuedAtUtc
                      "PriorFinishedAtUtc", box (enqueuedAtUtc.AddSeconds(1.0)) ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore

                let! _ = setup.ExecuteNonQueryAsync()
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! next =
                    PlaybackQueueRepository.enqueueNextActivePlaylistItemIfIdle
                        dataSource
                        nextQueueItemId
                        (enqueuedAtUtc.AddSeconds(10.0))
                        CancellationToken.None

                assertInserted "the item after the last completed playlist item" next

                use firstRead =
                    new NpgsqlCommand(
                        """SELECT "TrackId", "PlaylistItemId", "Source", "Status", "RequestedAtUtc"
FROM "PlaybackQueue"
WHERE "Id" = @QueueItemId;""",
                        connection
                    )

                firstRead.Parameters.AddWithValue("QueueItemId", nextQueueItemId) |> ignore
                let! firstReader = firstRead.ExecuteReaderAsync()
                use firstReader = firstReader
                let! hasFirstRow = firstReader.ReadAsync()
                Assert.That(hasFirstRow, Is.True)
                Assert.Multiple(fun () ->
                    Assert.That(firstReader.GetGuid(0), Is.EqualTo(lastTrackId))
                    Assert.That(firstReader.GetGuid(1), Is.EqualTo(lastPlaylistItemId))
                    Assert.That(firstReader.GetString(2), Is.EqualTo("playlist"))
                    Assert.That(firstReader.GetString(3), Is.EqualTo("Queued"))
                    Assert.That(firstReader.GetFieldValue<DateTimeOffset>(4), Is.EqualTo(enqueuedAtUtc.AddSeconds(10.0))))

                do! firstReader.CloseAsync()
                use finish =
                    new NpgsqlCommand(
                        """UPDATE "PlaybackQueue"
SET "Status" = 'Played', "FinishedAtUtc" = @FinishedAtUtc
WHERE "Id" = @QueueItemId;""",
                        connection
                    )

                finish.Parameters.AddWithValue("QueueItemId", nextQueueItemId) |> ignore
                finish.Parameters.AddWithValue("FinishedAtUtc", enqueuedAtUtc.AddSeconds(11.0)) |> ignore
                let! _ = finish.ExecuteNonQueryAsync()

                let! wrapped =
                    PlaybackQueueRepository.enqueueNextActivePlaylistItemIfIdle
                        dataSource
                        wrappedQueueItemId
                        (enqueuedAtUtc.AddSeconds(20.0))
                        CancellationToken.None

                assertInserted "the wrap from the largest playlist position" wrapped

                use wrapRead =
                    new NpgsqlCommand(
                        """SELECT "TrackId", "PlaylistItemId", "Source", "Status"
FROM "PlaybackQueue"
WHERE "Id" = @QueueItemId;""",
                        connection
                    )

                wrapRead.Parameters.AddWithValue("QueueItemId", wrappedQueueItemId) |> ignore
                let! wrapReader = wrapRead.ExecuteReaderAsync()
                use wrapReader = wrapReader
                let! hasWrappedRow = wrapReader.ReadAsync()
                Assert.That(hasWrappedRow, Is.True)
                Assert.Multiple(fun () ->
                    Assert.That(wrapReader.GetGuid(0), Is.EqualTo(firstTrackId))
                    Assert.That(wrapReader.GetGuid(1), Is.EqualTo(firstPlaylistItemId))
                    Assert.That(wrapReader.GetString(2), Is.EqualTo("playlist"))
                    Assert.That(wrapReader.GetString(3), Is.EqualTo("Queued")))
            })

    [<Test>]
    let ``idle playlist refill is a clean no-op without an active playlist or active items`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let firstCandidateId = newId ()
                let secondCandidateId = newId ()
                let emptyPlaylistId = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 10, 21, 15, 0, TimeSpan.Zero)
                use dataSource = NpgsqlDataSource.Create(connectionString)

                let! absentPlaylist =
                    PlaybackQueueRepository.enqueueNextActivePlaylistItemIfIdle
                        dataSource
                        firstCandidateId
                        nowUtc
                        CancellationToken.None

                assertNoop "a database with no active playlist" absentPlaylist

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use createEmptyPlaylist =
                    new NpgsqlCommand(
                        """INSERT INTO "Playlists" ("Id", "Name", "IsActive", "IsDeleted")
VALUES (@PlaylistId, 'Empty active playlist', true, false);""",
                        connection
                    )

                createEmptyPlaylist.Parameters.AddWithValue("PlaylistId", emptyPlaylistId) |> ignore
                let! _ = createEmptyPlaylist.ExecuteNonQueryAsync()
                let! emptyPlaylist =
                    PlaybackQueueRepository.enqueueNextActivePlaylistItemIfIdle
                        dataSource
                        secondCandidateId
                        (nowUtc.AddSeconds(1.0))
                        CancellationToken.None

                assertNoop "an active playlist with no active items" emptyPlaylist

                use count = new NpgsqlCommand("SELECT count(*) FROM \"PlaybackQueue\" WHERE \"IsDeleted\" = false;", connection)
                let! total = count.ExecuteScalarAsync()
                Assert.That(total, Is.EqualTo(box 0L))
            })

    [<Test>]
    let ``idle playlist refill never creates a playlist entry while any active queue state exists`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let playlistId = newId ()
                let playlistItemId = newId ()
                let activeQueueItemId = newId ()
                let claimOwner = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 10, 21, 30, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackId, 'Active work wins', 'Queue', false);
INSERT INTO "Playlists" ("Id", "Name", "IsActive", "IsDeleted")
VALUES (@PlaylistId, 'Would refill', true, false);
INSERT INTO "PlaylistItems" ("Id", "PlaylistId", "TrackId", "Position", "IsDeleted")
VALUES (@PlaylistItemId, @PlaylistId, @TrackId, 0, false);
INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc")
VALUES (@ActiveQueueItemId, @TrackId, 'admin', 'Queued', 100, @NowUtc);""",
                        connection
                    )

                for name, value in
                    [ "TrackId", box trackId
                      "PlaylistId", box playlistId
                      "PlaylistItemId", box playlistItemId
                      "ActiveQueueItemId", box activeQueueItemId
                      "NowUtc", box nowUtc ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore

                let! _ = setup.ExecuteNonQueryAsync()
                use dataSource = NpgsqlDataSource.Create(connectionString)

                for status in [ "Queued"; "Claimed"; "Playing" ] do
                    use setStatus =
                        new NpgsqlCommand(
                            """UPDATE "PlaybackQueue"
SET "Status" = @Status,
    "ClaimOwner" = CASE WHEN @Status = 'Queued' THEN NULL ELSE @ClaimOwner END,
    "ClaimAttempt" = CASE WHEN @Status = 'Queued' THEN 0 ELSE 1 END,
    "ClaimLeaseExpiresAtUtc" = CASE WHEN @Status = 'Queued' THEN NULL ELSE @LeaseExpiresAtUtc END
WHERE "Id" = @QueueItemId;""",
                            connection
                        )

                    setStatus.Parameters.AddWithValue("Status", status) |> ignore
                    setStatus.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                    setStatus.Parameters.AddWithValue("LeaseExpiresAtUtc", nowUtc.AddMinutes(1.0)) |> ignore
                    setStatus.Parameters.AddWithValue("QueueItemId", activeQueueItemId) |> ignore
                    let! _ = setStatus.ExecuteNonQueryAsync()
                    let! refill =
                        PlaybackQueueRepository.enqueueNextActivePlaylistItemIfIdle
                            dataSource
                            (newId ())
                            (nowUtc.AddSeconds(10.0))
                            CancellationToken.None

                    assertNoop (sprintf "an existing %s queue row" status) refill
                    use count = new NpgsqlCommand("SELECT count(*) FROM \"PlaybackQueue\" WHERE \"IsDeleted\" = false;", connection)
                    let! total = count.ExecuteScalarAsync()
                    Assert.That(total, Is.EqualTo(box 1L), sprintf "A %s row must prevent insertion of a playlist refill." status)
            })

    [<Test>]
    let ``idle playlist refill preserves queued request and admin priority over generated playlist work`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let playlistId = newId ()
                let playlistItemId = newId ()
                let adminQueueItemId = newId ()
                let requestQueueItemId = newId ()
                let claimOwner = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 10, 21, 45, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackId, 'Priority track', 'Queue', false);
INSERT INTO "Playlists" ("Id", "Name", "IsActive", "IsDeleted")
VALUES (@PlaylistId, 'Would be lower priority', true, false);
INSERT INTO "PlaylistItems" ("Id", "PlaylistId", "TrackId", "Position", "IsDeleted")
VALUES (@PlaylistItemId, @PlaylistId, @TrackId, 0, false);
INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc")
VALUES (@AdminQueueItemId, @TrackId, 'admin', 'Queued', 100, @AdminRequestedAtUtc),
       (@RequestQueueItemId, @TrackId, 'request', 'Queued', 200, @RequestRequestedAtUtc);""",
                        connection
                    )

                for name, value in
                    [ "TrackId", box trackId
                      "PlaylistId", box playlistId
                      "PlaylistItemId", box playlistItemId
                      "AdminQueueItemId", box adminQueueItemId
                      "RequestQueueItemId", box requestQueueItemId
                      "AdminRequestedAtUtc", box nowUtc
                      "RequestRequestedAtUtc", box (nowUtc.AddSeconds(1.0)) ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore

                let! _ = setup.ExecuteNonQueryAsync()
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! refill =
                    PlaybackQueueRepository.enqueueNextActivePlaylistItemIfIdle
                        dataSource
                        (newId ())
                        (nowUtc.AddSeconds(2.0))
                        CancellationToken.None

                assertNoop "queued request and admin work" refill

                let! claimed =
                    PlaybackQueueRepository.claimNextDetailed
                        dataSource
                        claimOwner
                        (nowUtc.AddSeconds(3.0))
                        (nowUtc.AddSeconds(33.0))
                        CancellationToken.None

                let claimedItem = claim "the higher-priority existing request instead of a generated playlist item" claimed
                Assert.That(claimedItem.QueueItemId, Is.EqualTo(requestQueueItemId))

                use countPlaylistRows =
                    new NpgsqlCommand(
                        """SELECT count(*)
FROM "PlaybackQueue"
WHERE "IsDeleted" = false
  AND "Source" = 'playlist';""",
                        connection
                    )

                let! playlistRows = countPlaylistRows.ExecuteScalarAsync()
                Assert.That(playlistRows, Is.EqualTo(box 0L))
            })
