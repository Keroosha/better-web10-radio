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
                let! result = PlaybackQueueRepository.getCurrentAssignment dataSource false CancellationToken.None

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
                let! result = PlaybackQueueRepository.getCurrentAssignment dataSource false CancellationToken.None

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

    [<Test>]
    let ``getPlaybackMediaFile returns the cached file for an active Playing item`` () =
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
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "DurationMs", "IsDeleted")
VALUES (@TrackId, 'Active', 'Artist', 1000, false);

INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "CachePath", "ContentType", "IsCached", "IsDeleted")
VALUES (@TrackFileId, @TrackId, '/library/current.ogg', '/cache/current.ogg', 'audio/ogg', true, false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc")
VALUES (@QueueItemId, @TrackId, 'fallback', 'Playing', 0, @NowUtc, @ClaimOwner, 3, @LeaseExpiresAtUtc);""",
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
                let! result = PlaybackQueueRepository.getPlaybackMediaFile dataSource queueItemId false CancellationToken.None

                match result with
                | Ok(Some media) ->
                    Assert.Multiple(fun () ->
                        Assert.That(media.CachePath, Is.EqualTo(Some "/cache/current.ogg"))
                        Assert.That(media.ContentType, Is.EqualTo("audio/ogg"))
                        Assert.That(media.IsDefaultS3, Is.False))
                | actual ->
                    Assert.Fail(sprintf "Expected the active item to expose its cached media file, but got %A." actual)
            })

    [<Test>]
    let ``getPlaybackMediaFile ignores a non-active queued item`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let trackFileId = newId ()
                let queueItemId = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 10, 20, 5, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "DurationMs", "IsDeleted")
VALUES (@TrackId, 'Queued', 'Artist', 1000, false);

INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "CachePath", "ContentType", "IsCached", "IsDeleted")
VALUES (@TrackFileId, @TrackId, '/library/queued.ogg', '/cache/queued.ogg', 'audio/ogg', true, false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc")
VALUES (@QueueItemId, @TrackId, 'fallback', 'Queued', 0, @NowUtc);""",
                        connection
                    )

                for name, value in
                    [ "TrackId", box trackId
                      "TrackFileId", box trackFileId
                      "QueueItemId", box queueItemId
                      "NowUtc", box nowUtc ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore

                let! _ = setup.ExecuteNonQueryAsync()
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! result = PlaybackQueueRepository.getPlaybackMediaFile dataSource queueItemId false CancellationToken.None

                match result with
                | Ok None -> ()
                | actual -> Assert.Fail(sprintf "Expected no media file for a non-active queue item, but got %A." actual)
            })

    [<Test>]
    let ``removing a queued item soft-deletes it`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let queueItemId = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insert =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted") VALUES (@TrackId, 'T', 'A', false);
INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc")
VALUES (@QueueItemId, @TrackId, 'admin', 'Queued', 0, @RequestedAtUtc);""",
                        connection
                    )
                insert.Parameters.AddWithValue("TrackId", trackId) |> ignore
                insert.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                insert.Parameters.AddWithValue("RequestedAtUtc", nowUtc) |> ignore
                let! _ = insert.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! result = PlaybackQueueRepository.removeQueuedItem dataSource queueItemId nowUtc CancellationToken.None
                match result with
                | Ok(PlaybackControlOutcome.Applied()) -> ()
                | actual -> Assert.Fail(sprintf "Expected Applied, got %A." actual)

                use check = new NpgsqlCommand("""SELECT "IsDeleted" FROM "PlaybackQueue" WHERE "Id" = @Id;""", connection)
                check.Parameters.AddWithValue("Id", queueItemId) |> ignore
                let! deleted = check.ExecuteScalarAsync()
                Assert.That(deleted :?> bool, Is.True, "The queued item must be soft-deleted.")

                let! missing = PlaybackQueueRepository.removeQueuedItem dataSource (newId ()) nowUtc CancellationToken.None
                match missing with
                | Ok PlaybackControlOutcome.NotFound -> ()
                | actual -> Assert.Fail(sprintf "Expected NotFound for an unknown item, got %A." actual)
            })

    [<Test>]
    let ``removing a playing item conflicts`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let queueItemId = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insert =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted") VALUES (@TrackId, 'T', 'A', false);
INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "StartedAtUtc")
VALUES (@QueueItemId, @TrackId, 'admin', 'Playing', 0, @RequestedAtUtc, @RequestedAtUtc);""",
                        connection
                    )
                insert.Parameters.AddWithValue("TrackId", trackId) |> ignore
                insert.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                insert.Parameters.AddWithValue("RequestedAtUtc", nowUtc) |> ignore
                let! _ = insert.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! result = PlaybackQueueRepository.removeQueuedItem dataSource queueItemId nowUtc CancellationToken.None
                match result with
                | Ok PlaybackControlOutcome.Conflict -> ()
                | actual -> Assert.Fail(sprintf "Expected Conflict for a playing item, got %A." actual)
            })

    let private activePlaybackCounts (connection: NpgsqlConnection) =
        task {
            use command =
                new NpgsqlCommand(
                    """SELECT count(*) FILTER (WHERE "Status" = 'Playing'), count(*) FILTER (WHERE "Status" = 'Claimed')
FROM "PlaybackQueue" WHERE "IsDeleted" = false;""",
                    connection
                )
            let! reader = command.ExecuteReaderAsync()
            use reader = reader
            let! _ = reader.ReadAsync()
            return (reader.GetInt64(0), reader.GetInt64(1))
        }

    let private statusOf (connection: NpgsqlConnection) (queueItemId: Guid) =
        task {
            use command = new NpgsqlCommand("""SELECT "Status" FROM "PlaybackQueue" WHERE "Id" = @Id AND "IsDeleted" = false;""", connection)
            command.Parameters.AddWithValue("Id", queueItemId) |> ignore
            let! value = command.ExecuteScalarAsync()
            return (if isNull value then "<missing>" else value :?> string)
        }

    [<Test>]
    let ``claimNextDetailed reserves a second Claimed on-deck while one item is Playing`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackA = newId ()
                let trackB = newId ()
                let playingId = newId ()
                let queuedId = newId ()
                let playingOwner = newId ()
                let onDeckOwner = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero)
                let leaseExpiresAtUtc = nowUtc.AddSeconds(30.0)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackA, 'A', 'A', false), (@TrackB, 'B', 'B', false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "StartedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc")
VALUES (@PlayingId, @TrackA, 'fallback', 'Playing', 0, @NowUtc, @NowUtc, @PlayingOwner, 1, @LeaseExpiresAtUtc);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc")
VALUES (@QueuedId, @TrackB, 'fallback', 'Queued', 0, @NowUtc);""",
                        connection
                    )
                for name, value in
                    [ "TrackA", box trackA; "TrackB", box trackB
                      "PlayingId", box playingId; "QueuedId", box queuedId
                      "PlayingOwner", box playingOwner
                      "NowUtc", box nowUtc; "LeaseExpiresAtUtc", box leaseExpiresAtUtc ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore
                let! _ = setup.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! claimed = PlaybackQueueRepository.claimNextDetailed dataSource onDeckOwner nowUtc leaseExpiresAtUtc CancellationToken.None
                match claimed with
                | Ok(Some item) -> Assert.That(item.QueueItemId, Is.EqualTo(queuedId), "The Queued item is reserved on-deck while another plays.")
                | actual -> Assert.Fail(sprintf "Expected the Queued item to be reserved on-deck, got %A." actual)

                let! playing, claimedCount = activePlaybackCounts connection
                Assert.Multiple(fun () ->
                    Assert.That(playing, Is.EqualTo(1L), "Exactly one Playing.")
                    Assert.That(claimedCount, Is.EqualTo(1L), "Exactly one Claimed on-deck."))
            })

    [<Test>]
    let ``claimNextDetailed does not reserve a third active item`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackA = newId ()
                let trackB = newId ()
                let trackC = newId ()
                let playingId = newId ()
                let claimedId = newId ()
                let queuedId = newId ()
                let playingOwner = newId ()
                let onDeckOwner = newId ()
                let thirdOwner = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero)
                let leaseExpiresAtUtc = nowUtc.AddSeconds(30.0)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackA, 'A', 'A', false), (@TrackB, 'B', 'B', false), (@TrackC, 'C', 'C', false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "StartedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc")
VALUES (@PlayingId, @TrackA, 'fallback', 'Playing', 0, @NowUtc, @NowUtc, @PlayingOwner, 1, @LeaseExpiresAtUtc),
       (@ClaimedId, @TrackB, 'fallback', 'Claimed', 0, @NowUtc, NULL, @OnDeckOwner, 1, @LeaseExpiresAtUtc);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc")
VALUES (@QueuedId, @TrackC, 'fallback', 'Queued', 0, @NowUtc);""",
                        connection
                    )
                for name, value in
                    [ "TrackA", box trackA; "TrackB", box trackB; "TrackC", box trackC
                      "PlayingId", box playingId; "ClaimedId", box claimedId; "QueuedId", box queuedId
                      "PlayingOwner", box playingOwner; "OnDeckOwner", box onDeckOwner
                      "NowUtc", box nowUtc; "LeaseExpiresAtUtc", box leaseExpiresAtUtc ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore
                let! _ = setup.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! claimed = PlaybackQueueRepository.claimNextDetailed dataSource thirdOwner nowUtc leaseExpiresAtUtc CancellationToken.None
                match claimed with
                | Ok None -> ()
                | actual -> Assert.Fail(sprintf "Expected no third reservation while one Playing and one Claimed exist, got %A." actual)

                let! status = statusOf connection queuedId
                Assert.That(status, Is.EqualTo("Queued"), "The extra item stays Queued; the lookahead is capped at one on-deck.")
            })

    [<Test>]
    let ``tryGetPromotableOnDeck promotes only when nothing is Playing`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackA = newId ()
                let trackB = newId ()
                let playingId = newId ()
                let claimedId = newId ()
                let playingOwner = newId ()
                let onDeckOwner = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero)
                let leaseExpiresAtUtc = nowUtc.AddSeconds(30.0)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackA, 'A', 'A', false), (@TrackB, 'B', 'B', false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "StartedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc")
VALUES (@PlayingId, @TrackA, 'fallback', 'Playing', 0, @NowUtc, @NowUtc, @PlayingOwner, 1, @LeaseExpiresAtUtc),
       (@ClaimedId, @TrackB, 'fallback', 'Claimed', 0, @NowUtc, NULL, @OnDeckOwner, 2, @LeaseExpiresAtUtc);""",
                        connection
                    )
                for name, value in
                    [ "TrackA", box trackA; "TrackB", box trackB
                      "PlayingId", box playingId; "ClaimedId", box claimedId
                      "PlayingOwner", box playingOwner; "OnDeckOwner", box onDeckOwner
                      "NowUtc", box nowUtc; "LeaseExpiresAtUtc", box leaseExpiresAtUtc ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore
                let! _ = setup.ExecuteNonQueryAsync()

                use blockedTx = connection.BeginTransaction()
                let! blocked = PlaybackQueueRepository.tryGetPromotableOnDeckInTransaction connection blockedTx CancellationToken.None
                do! blockedTx.RollbackAsync()
                match blocked with
                | Ok None -> ()
                | actual -> Assert.Fail(sprintf "On-deck must not be promotable while an item is Playing, got %A." actual)

                use retire = new NpgsqlCommand("""UPDATE "PlaybackQueue" SET "Status" = 'Played', "ClaimOwner" = NULL, "ClaimLeaseExpiresAtUtc" = NULL WHERE "Id" = @Id;""", connection)
                retire.Parameters.AddWithValue("Id", playingId) |> ignore
                let! _ = retire.ExecuteNonQueryAsync()

                use readyTx = connection.BeginTransaction()
                let! ready = PlaybackQueueRepository.tryGetPromotableOnDeckInTransaction connection readyTx CancellationToken.None
                do! readyTx.RollbackAsync()
                match ready with
                | Ok(Some claim) ->
                    Assert.Multiple(fun () ->
                        Assert.That(claim.QueueItemId, Is.EqualTo(claimedId))
                        Assert.That(claim.ClaimOwner, Is.EqualTo(onDeckOwner))
                        Assert.That(claim.ClaimAttempt, Is.EqualTo(2))
                        Assert.That(claim.TrackId, Is.EqualTo(Some trackB)))
                | actual -> Assert.Fail(sprintf "Expected the lone Claimed on-deck to be promotable once nothing is Playing, got %A." actual)
            })

    [<Test>]
    let ``getUpcomingAssignments returns the Playing current and the Claimed on-deck`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackA = newId ()
                let trackB = newId ()
                let fileA = newId ()
                let fileB = newId ()
                let playingId = newId ()
                let claimedId = newId ()
                let playingOwner = newId ()
                let onDeckOwner = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero)
                let leaseExpiresAtUtc = nowUtc.AddSeconds(30.0)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "DurationMs", "IsDeleted")
VALUES (@TrackA, 'A', 'AA', 1000, false), (@TrackB, 'B', 'BB', 2000, false);

INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "CachePath", "ContentType", "IsCached", "IsDeleted")
VALUES (@FileA, @TrackA, '/lib/a.mp3', '/cache/a.mp3', 'audio/mpeg', true, false),
       (@FileB, @TrackB, '/lib/b.ogg', '/cache/b.ogg', 'audio/ogg', true, false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "StartedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc")
VALUES (@PlayingId, @TrackA, 'fallback', 'Playing', 0, @NowUtc, @NowUtc, @PlayingOwner, 4, @LeaseExpiresAtUtc),
       (@ClaimedId, @TrackB, 'fallback', 'Claimed', 0, @NowUtc, NULL, @OnDeckOwner, 5, @LeaseExpiresAtUtc);""",
                        connection
                    )
                for name, value in
                    [ "TrackA", box trackA; "TrackB", box trackB
                      "FileA", box fileA; "FileB", box fileB
                      "PlayingId", box playingId; "ClaimedId", box claimedId
                      "PlayingOwner", box playingOwner; "OnDeckOwner", box onDeckOwner
                      "NowUtc", box nowUtc; "LeaseExpiresAtUtc", box leaseExpiresAtUtc ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore
                let! _ = setup.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! result = PlaybackQueueRepository.getUpcomingAssignments dataSource false CancellationToken.None
                match result with
                | Ok upcoming ->
                    match upcoming.Current, upcoming.Next with
                    | Some current, Some next ->
                        Assert.Multiple(fun () ->
                            Assert.That(current.QueueItemId, Is.EqualTo(playingId))
                            Assert.That(current.ClaimAttempt, Is.EqualTo(4))
                            Assert.That(current.ContentType, Is.EqualTo("audio/mpeg"))
                            Assert.That(next.QueueItemId, Is.EqualTo(claimedId))
                            Assert.That(next.ClaimOwner, Is.EqualTo(onDeckOwner))
                            Assert.That(next.ClaimAttempt, Is.EqualTo(5))
                            Assert.That(next.ContentType, Is.EqualTo("audio/ogg"))
                            Assert.That(next.TrackId, Is.EqualTo(trackB)))
                    | actual -> Assert.Fail(sprintf "Expected both current and on-deck assignments, got %A." actual)
                | actual -> Assert.Fail(sprintf "Expected the upcoming lookahead, got %A." actual)
            })

    [<Test>]
    let ``renewPlayingLease renews a Claimed on-deck reservation`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackB = newId ()
                let claimedId = newId ()
                let onDeckOwner = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero)
                let leaseExpiresAtUtc = nowUtc.AddSeconds(30.0)
                let renewedLease = nowUtc.AddSeconds(90.0)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted") VALUES (@TrackB, 'B', 'B', false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc")
VALUES (@ClaimedId, @TrackB, 'fallback', 'Claimed', 0, @NowUtc, @OnDeckOwner, 2, @LeaseExpiresAtUtc);""",
                        connection
                    )
                for name, value in
                    [ "TrackB", box trackB; "ClaimedId", box claimedId; "OnDeckOwner", box onDeckOwner
                      "NowUtc", box nowUtc; "LeaseExpiresAtUtc", box leaseExpiresAtUtc ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore
                let! _ = setup.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! renewed = PlaybackQueueRepository.renewPlayingLease dataSource claimedId onDeckOwner 2 nowUtc renewedLease CancellationToken.None
                match renewed with
                | Ok true -> ()
                | actual -> Assert.Fail(sprintf "Expected the Claimed on-deck lease to renew, got %A." actual)

                use check = new NpgsqlCommand("""SELECT "ClaimLeaseExpiresAtUtc" FROM "PlaybackQueue" WHERE "Id" = @Id;""", connection)
                check.Parameters.AddWithValue("Id", claimedId) |> ignore
                let! reader = check.ExecuteReaderAsync()
                use reader = reader
                let! _ = reader.ReadAsync()
                Assert.That(reader.GetFieldValue<DateTimeOffset>(0), Is.EqualTo(renewedLease), "The on-deck lease is extended.")
            })

    [<Test>]
    let ``forcePlayNow retires the on-deck reservation`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackA = newId ()
                let trackB = newId ()
                let trackC = newId ()
                let fileC = newId ()
                let playingId = newId ()
                let onDeckId = newId ()
                let adminQueueId = newId ()
                let playingOwner = newId ()
                let onDeckOwner = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero)
                let leaseExpiresAtUtc = nowUtc.AddSeconds(30.0)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackA, 'A', 'A', false), (@TrackB, 'B', 'B', false), (@TrackC, 'C', 'C', false);

INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "CachePath", "ContentType", "IsCached", "IsDeleted")
VALUES (@FileC, @TrackC, '/lib/c.mp3', '/cache/c.mp3', 'audio/mpeg', true, false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "StartedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc")
VALUES (@PlayingId, @TrackA, 'fallback', 'Playing', 0, @NowUtc, @NowUtc, @PlayingOwner, 1, @LeaseExpiresAtUtc),
       (@OnDeckId, @TrackB, 'fallback', 'Claimed', 0, @NowUtc, NULL, @OnDeckOwner, 1, @LeaseExpiresAtUtc);""",
                        connection
                    )
                for name, value in
                    [ "TrackA", box trackA; "TrackB", box trackB; "TrackC", box trackC; "FileC", box fileC
                      "PlayingId", box playingId; "OnDeckId", box onDeckId
                      "PlayingOwner", box playingOwner; "OnDeckOwner", box onDeckOwner
                      "NowUtc", box nowUtc; "LeaseExpiresAtUtc", box leaseExpiresAtUtc ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore
                let! _ = setup.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! result = PlaybackQueueRepository.forcePlayNow dataSource adminQueueId trackC false nowUtc CancellationToken.None
                match result with
                | Ok(PlaybackControlOutcome.Applied _) -> ()
                | actual -> Assert.Fail(sprintf "Expected force-play-now to apply, got %A." actual)

                let! playingStatus = statusOf connection playingId
                let! onDeckStatus = statusOf connection onDeckId
                let! adminStatus = statusOf connection adminQueueId
                Assert.Multiple(fun () ->
                    Assert.That(playingStatus, Is.EqualTo("Played"), "The current is interrupted.")
                    Assert.That(onDeckStatus, Is.EqualTo("Played"), "The dangling on-deck reservation is retired.")
                    Assert.That(adminStatus, Is.EqualTo("Queued"), "The forced admin track is queued."))
            })

    [<Test>]
    let ``skipCurrentTrack evicts an on-deck referencing a deleted track`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackA = newId ()
                let trackB = newId ()
                let playingId = newId ()
                let onDeckId = newId ()
                let playingOwner = newId ()
                let onDeckOwner = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero)
                let leaseExpiresAtUtc = nowUtc.AddSeconds(30.0)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackA, 'A', 'A', false), (@TrackB, 'B', 'B', false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "StartedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc")
VALUES (@PlayingId, @TrackA, 'fallback', 'Playing', 0, @NowUtc, @NowUtc, @PlayingOwner, 1, @LeaseExpiresAtUtc),
       (@OnDeckId, @TrackB, 'fallback', 'Claimed', 0, @NowUtc, NULL, @OnDeckOwner, 1, @LeaseExpiresAtUtc);""",
                        connection
                    )
                for name, value in
                    [ "TrackA", box trackA; "TrackB", box trackB
                      "PlayingId", box playingId; "OnDeckId", box onDeckId
                      "PlayingOwner", box playingOwner; "OnDeckOwner", box onDeckOwner
                      "NowUtc", box nowUtc; "LeaseExpiresAtUtc", box leaseExpiresAtUtc ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore
                let! _ = setup.ExecuteNonQueryAsync()

                use transaction = connection.BeginTransaction()
                let! result = PlaybackQueueRepository.skipCurrentTrackInTransaction connection transaction [ trackB ] nowUtc CancellationToken.None
                do! transaction.CommitAsync()
                match result with
                | Ok None -> ()
                | actual -> Assert.Fail(sprintf "The currently Playing track is not being deleted, so no current skip is reported, got %A." actual)

                let! playingStatus = statusOf connection playingId
                let! onDeckStatus = statusOf connection onDeckId
                Assert.Multiple(fun () ->
                    Assert.That(playingStatus, Is.EqualTo("Playing"), "The unaffected current keeps playing.")
                    Assert.That(onDeckStatus, Is.EqualTo("Played"), "The on-deck referencing the deleted track is evicted."))
            })

    let private seedUncachedS3Playing (connection: NpgsqlConnection) (trackId: Guid) (queueItemId: Guid) (nowUtc: DateTimeOffset) =
        task {
            use setup =
                new NpgsqlCommand(
                    """INSERT INTO "Tracks" ("Id", "Title", "Artist", "DurationMs", "IsDeleted")
VALUES (@TrackId, 'S3', 'Cloud', 1000, false);

INSERT INTO "TrackFiles" ("Id", "TrackId", "StorageBackendId", "StoragePath", "CachePath", "ContentType", "IsCached", "IsDeleted")
VALUES (@TrackFileId, @TrackId, NULL, 'library/cloud.mp3', NULL, 'audio/mpeg', false, false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "StartedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc")
VALUES (@QueueItemId, @TrackId, 'fallback', 'Playing', 0, @NowUtc, @NowUtc, @ClaimOwner, 2, @LeaseExpiresAtUtc);""",
                    connection
                )
            for name, value in
                [ "TrackId", box trackId
                  "TrackFileId", box (Uuid.CreateVersion7().ToGuidBigEndian())
                  "QueueItemId", box queueItemId
                  "ClaimOwner", box (Uuid.CreateVersion7().ToGuidBigEndian())
                  "NowUtc", box nowUtc
                  "LeaseExpiresAtUtc", box (nowUtc.AddSeconds(30.0)) ] do
                setup.Parameters.AddWithValue(name, value) |> ignore
            let! _ = setup.ExecuteNonQueryAsync()
            return ()
        }

    [<Test>]
    let ``getPlaybackMediaFile surfaces the S3 key for an uncached default-backend item in S3 mode`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let queueItemId = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 13, 20, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                do! seedUncachedS3Playing connection trackId queueItemId nowUtc

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! s3Result = PlaybackQueueRepository.getPlaybackMediaFile dataSource queueItemId true CancellationToken.None
                match s3Result with
                | Ok(Some media) ->
                    Assert.Multiple(fun () ->
                        Assert.That(media.IsDefaultS3, Is.True, "The default-backend item is presign-eligible in S3 mode.")
                        Assert.That(media.CachePath, Is.EqualTo(None), "There is no local cache copy to stream.")
                        Assert.That(media.StorageKey, Is.EqualTo("library/cloud.mp3"), "The S3 object key drives the presigned URL.")
                        Assert.That(media.ContentType, Is.EqualTo("audio/mpeg")))
                | actual -> Assert.Fail(sprintf "Expected an uncached S3 item to be servable by key in S3 mode, got %A." actual)

                let! localResult = PlaybackQueueRepository.getPlaybackMediaFile dataSource queueItemId false CancellationToken.None
                match localResult with
                | Ok None -> ()
                | actual -> Assert.Fail(sprintf "In Local mode an uncached item exposes no servable media, got %A." actual)
            })

    [<Test>]
    let ``getCurrentAssignment keeps an uncached S3 item eligible in S3 mode`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let queueItemId = newId ()
                let nowUtc = DateTimeOffset(2026, 7, 13, 20, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                do! seedUncachedS3Playing connection trackId queueItemId nowUtc

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! s3Result = PlaybackQueueRepository.getCurrentAssignment dataSource true CancellationToken.None
                match s3Result with
                | Ok(Some assignment) ->
                    Assert.Multiple(fun () ->
                        Assert.That(assignment.TrackId, Is.EqualTo(trackId))
                        Assert.That(assignment.ContentType, Is.EqualTo("audio/mpeg")))
                | actual -> Assert.Fail(sprintf "An uncached S3 item must remain playable in S3 mode, got %A." actual)

                let! localResult = PlaybackQueueRepository.getCurrentAssignment dataSource false CancellationToken.None
                match localResult with
                | Ok None -> ()
                | actual -> Assert.Fail(sprintf "The same item is not playable in Local mode without a cache, got %A." actual)
            })

    [<Test>]
    let ``current assignment exposes CUE decoder timing`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = newId ()
                let fileId = newId ()
                let queueItemId = newId ()
                let owner = newId ()
                let now = DateTimeOffset(2026, 7, 13, 21, 0, 0, TimeSpan.Zero)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "DurationMs", "MetadataSource", "IsDeleted")
VALUES (@TrackId, 'Segment', 'Artist', 3000, 'Cue', false);
INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "CachePath", "ContentType", "IsCached", "CueSheetPath", "CueTrackNumber", "CueStartMs", "CueDurationMs", "IsDeleted")
VALUES (@FileId, @TrackId, 'album.flac', '/cache/album.flac', 'audio/flac', true, 'album.cue', 2, 3000, 3000, false);
INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "StartedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc", "IsDeleted")
VALUES (@QueueItemId, @TrackId, 'fallback', 'Playing', 0, @Now, @Now, @Owner, 1, @Lease, false);""",
                        connection
                    )
                for name, value in
                    [ "TrackId", box trackId; "FileId", box fileId; "QueueItemId", box queueItemId
                      "Owner", box owner; "Now", box now; "Lease", box (now.AddSeconds 30.0) ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore
                let! _ = setup.ExecuteNonQueryAsync()
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! current = PlaybackQueueRepository.getCurrentAssignment dataSource false CancellationToken.None
                match current with
                | Ok(Some assignment) ->
                    Assert.Multiple(fun () ->
                        Assert.That(assignment.QueueItemId, Is.EqualTo(queueItemId))
                        Assert.That(assignment.ContentType, Is.EqualTo("audio/flac"))
                        Assert.That(assignment.CueStartMs, Is.EqualTo(Some 3000))
                        Assert.That(assignment.CueDurationMs, Is.EqualTo(Some 3000)))
                | actual -> Assert.Fail(sprintf "Expected timed CUE assignment, got %A." actual)
            })
