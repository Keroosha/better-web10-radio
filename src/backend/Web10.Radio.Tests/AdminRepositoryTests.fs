namespace Web10.Radio.Tests

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Database
open Web10.Radio.Database.Repositories

module AdminRepositoryTests =
    let private newId () =
        (UuidV7IdGenerator() :> IIdGenerator).NewId()

    let private atUtc = DateTimeOffset(2026, 7, 10, 18, 0, 0, TimeSpan.Zero)

    let private expectOk description = function
        | Ok value -> value
        | Error error -> Assert.Fail(sprintf "Expected %s to succeed, but got %A." description error); Unchecked.defaultof<_>
    let private taskMap mapper (source: Task<'T>) : Task<'U> =
        task {
            let! value = source
            return mapper value
        }


    let private execute (connection: NpgsqlConnection) sql configure =
        task {
            use command = new NpgsqlCommand(sql, connection)
            configure command
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    let private seedTrack connection id title artist album durationMs isDeleted createdAtUtc =
        execute
            connection
            """INSERT INTO "Tracks" ("Id", "Title", "Artist", "Album", "DurationMs", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @Title, @Artist, @Album, @DurationMs, @IsDeleted, @At, @At);"""
            (fun command ->
                command.Parameters.AddWithValue("Id", id) |> ignore
                command.Parameters.AddWithValue("Title", title) |> ignore
                command.Parameters.AddWithValue("Artist", artist) |> ignore
                let albumParameter = command.Parameters.Add("Album", NpgsqlDbType.Text)
                albumParameter.Value <- album |> Option.map box |> Option.defaultValue DBNull.Value
                let durationParameter = command.Parameters.Add("DurationMs", NpgsqlDbType.Integer)
                durationParameter.Value <- durationMs |> Option.map box |> Option.defaultValue DBNull.Value
                command.Parameters.AddWithValue("IsDeleted", isDeleted) |> ignore
                command.Parameters.AddWithValue("At", createdAtUtc) |> ignore)

    let private seedCachedTrackFile connection id trackId cachePath =
        execute
            connection
            """INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "CachePath", "IsCached", "IsDeleted")
VALUES (@Id, @TrackId, @StoragePath, @CachePath, true, false);"""
            (fun command ->
                command.Parameters.AddWithValue("Id", id) |> ignore
                command.Parameters.AddWithValue("TrackId", trackId) |> ignore
                command.Parameters.AddWithValue("StoragePath", cachePath) |> ignore
                command.Parameters.AddWithValue("CachePath", cachePath) |> ignore)

    [<Test>]
    let ``identity upsert keeps one active normalized user updates its password hash and soft deletes every other admin`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let originalId = newId ()
                let competingId = newId ()
                let otherId = newId ()

                let original : AdminUserToUpsert =
                    { Id = originalId
                      Username = "ConsoleAdmin"
                      NormalizedUsername = "CONSOLEADMIN"
                      PasswordHash = "hash-v1"
                      UpdatedAtUtc = atUtc }

                let! first =
                    AdminIdentityRepository.upsertActiveUser dataSource original CancellationToken.None
                    |> taskMap (expectOk "the initial active admin upsert")

                Assert.That(first.Id, Is.EqualTo(originalId))
                Assert.That(first.PasswordHash, Is.EqualTo("hash-v1"))

                let authoritativeBootstrap : AdminUserToUpsert =
                    { original with
                        Id = competingId
                        Username = "console-admin"
                        PasswordHash = "hash-v2"
                        UpdatedAtUtc = atUtc.AddMinutes(1.0) }

                let! updated =
                    AdminIdentityRepository.upsertActiveUser dataSource authoritativeBootstrap CancellationToken.None
                    |> taskMap (expectOk "the authoritative bootstrap upsert")

                Assert.Multiple(fun () ->
                    Assert.That(updated.Id, Is.EqualTo(originalId), "An upsert must update the existing active normalized user rather than create a second account.")
                    Assert.That(updated.Username, Is.EqualTo("console-admin"))
                    Assert.That(updated.PasswordHash, Is.EqualTo("hash-v2")))

                let! hashUpdated =
                    AdminIdentityRepository.updatePasswordHash
                        dataSource
                        originalId
                        "hash-v3"
                        (atUtc.AddMinutes(2.0))
                        CancellationToken.None
                    |> taskMap (expectOk "the active password-hash update")

                match hashUpdated with
                | Some user -> Assert.That(user.PasswordHash, Is.EqualTo("hash-v3"))
                | None -> Assert.Fail("An active admin must be found for a password-hash update.")

                let other : AdminUserToUpsert =
                    { Id = otherId
                      Username = "OtherAdmin"
                      NormalizedUsername = "OTHERADMIN"
                      PasswordHash = "other-hash"
                      UpdatedAtUtc = atUtc }

                let! _ =
                    AdminIdentityRepository.upsertActiveUser dataSource other CancellationToken.None
                    |> taskMap (expectOk "the competing active admin upsert")

                let! _ =
                    AdminIdentityRepository.softDeleteOtherActiveUsers
                        dataSource
                        originalId
                        (atUtc.AddMinutes(3.0))
                        CancellationToken.None
                    |> taskMap (expectOk "soft deletion of non-authoritative admins")

                let! retained =
                    AdminIdentityRepository.lookupActiveUserByNormalizedUsername
                        dataSource
                        "CONSOLEADMIN"
                        CancellationToken.None
                    |> taskMap (expectOk "lookup of the retained admin")

                let! removed =
                    AdminIdentityRepository.lookupActiveUserByNormalizedUsername
                        dataSource
                        "OTHERADMIN"
                        CancellationToken.None
                    |> taskMap (expectOk "lookup of the removed admin")

                Assert.Multiple(fun () ->
                    Assert.That(retained |> Option.map _.Id, Is.EqualTo(Some originalId))
                    Assert.That(removed, Is.EqualTo(None), "Soft-deleted admins must not remain visible to normal active-user lookup."))
            })

    [<Test>]
    let ``session lookup accepts only an unexpired unrevoked token hash and token hash conflicts are typed`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let userId = newId ()
                let user : AdminUserToUpsert =
                    { Id = userId
                      Username = "SessionAdmin"
                      NormalizedUsername = "SESSIONADMIN"
                      PasswordHash = "password-hash"
                      UpdatedAtUtc = atUtc }

                let! _ =
                    AdminIdentityRepository.upsertActiveUser dataSource user CancellationToken.None
                    |> taskMap (expectOk "the session owner upsert")

                let tokenHash = [| 1uy; 2uy; 3uy; 4uy |]
                let session : AdminSessionToCreate =
                    { Id = newId ()
                      UserId = userId
                      TokenHash = tokenHash
                      CsrfToken = "csrf-token"
                      ExpiresAtUtc = atUtc.AddHours(8.0)
                      CreatedAtUtc = atUtc }

                let! created =
                    AdminIdentityRepository.createSession dataSource session CancellationToken.None
                    |> taskMap (expectOk "the initial session creation")

                let createdSession =
                    match created with
                    | AdminSessionCreateOutcome.Created value -> value
                    | actual -> Assert.Fail(sprintf "Expected session creation to create a new session, but got %A." actual); Unchecked.defaultof<_>

                let! loaded =
                    AdminIdentityRepository.lookupActiveSessionByTokenHash
                        dataSource
                        tokenHash
                        (atUtc.AddMinutes(1.0))
                        CancellationToken.None
                    |> taskMap (expectOk "lookup of the active token hash")

                Assert.Multiple(fun () ->
                    Assert.That(loaded |> Option.map _.Id, Is.EqualTo(Some createdSession.Id))
                    Assert.That(loaded |> Option.map _.CsrfToken, Is.EqualTo(Some "csrf-token")))

                let duplicate : AdminSessionToCreate =
                    { session with
                        Id = newId ()
                        CsrfToken = "different-csrf-token" }

                let! duplicateResult =
                    AdminIdentityRepository.createSession dataSource duplicate CancellationToken.None
                    |> taskMap (expectOk "the duplicate token-hash attempt")

                Assert.That(duplicateResult, Is.EqualTo(AdminSessionCreateOutcome.Conflict), "An active token hash must not be silently shared by two sessions.")

                let expired : AdminSessionToCreate =
                    { Id = newId ()
                      UserId = userId
                      TokenHash = [| 9uy; 8uy; 7uy |]
                      CsrfToken = "expired-csrf"
                      ExpiresAtUtc = atUtc
                      CreatedAtUtc = atUtc.AddHours(-8.0) }

                let! _ =
                    AdminIdentityRepository.createSession dataSource expired CancellationToken.None
                    |> taskMap (expectOk "creation of the expired session fixture")

                let! expiredLookup =
                    AdminIdentityRepository.lookupActiveSessionByTokenHash
                        dataSource
                        expired.TokenHash
                        atUtc
                        CancellationToken.None
                    |> taskMap (expectOk "expired-session lookup")

                Assert.That(expiredLookup, Is.EqualTo(None), "A session at its absolute expiry boundary must no longer authenticate.")

                let! revoked =
                    AdminIdentityRepository.revokeSession
                        dataSource
                        createdSession.Id
                        (atUtc.AddMinutes(2.0))
                        CancellationToken.None
                    |> taskMap (expectOk "revocation of the active session")

                Assert.That(revoked, Is.EqualTo(AdminSessionRevokeOutcome.Revoked))

                let! revokedLookup =
                    AdminIdentityRepository.lookupActiveSessionByTokenHash
                        dataSource
                        tokenHash
                        (atUtc.AddMinutes(3.0))
                        CancellationToken.None
                    |> taskMap (expectOk "revoked-session lookup")

                Assert.That(revokedLookup, Is.EqualTo(None), "Revoked sessions must not authenticate even before their absolute expiry.")
            })

    [<Test>]
    let ``stream control lazily creates one running singleton and restart forces running while advancing generation`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let firstCandidateId = newId ()

                let! created =
                    StreamNodeControlRepository.getOrCreate dataSource firstCandidateId atUtc CancellationToken.None
                    |> taskMap (expectOk "lazy creation of stream-node control")

                let! repeated =
                    StreamNodeControlRepository.getOrCreate dataSource (newId ()) (atUtc.AddMinutes(1.0)) CancellationToken.None
                    |> taskMap (expectOk "reading the existing stream-node control")

                Assert.Multiple(fun () ->
                    Assert.That(created.Id, Is.EqualTo(firstCandidateId))
                    Assert.That(created.DesiredState, Is.EqualTo(StreamNodeDesiredState.Running))
                    Assert.That(created.RestartGeneration, Is.EqualTo(0))
                    Assert.That(repeated.Id, Is.EqualTo(firstCandidateId), "A later lazy-create candidate must not introduce a second active control row."))

                let! stopped =
                    StreamNodeControlRepository.setDesiredState
                        dataSource
                        StreamNodeDesiredState.Stopped
                        (atUtc.AddMinutes(2.0))
                        CancellationToken.None
                    |> taskMap (expectOk "stopping the stream-node control")

                let! restarted =
                    StreamNodeControlRepository.restart dataSource (atUtc.AddMinutes(3.0)) CancellationToken.None
                    |> taskMap (expectOk "restarting the stream-node control")

                Assert.Multiple(fun () ->
                    Assert.That(stopped.DesiredState, Is.EqualTo(StreamNodeDesiredState.Stopped))
                    Assert.That(stopped.RestartGeneration, Is.EqualTo(0))
                    Assert.That(restarted.DesiredState, Is.EqualTo(StreamNodeDesiredState.Running))
                    Assert.That(restarted.RestartGeneration, Is.EqualTo(1)))
            })

    [<Test>]
    let ``donation-goal upsert creates a zero-raised goal and preserves paid progress across a goal change`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let goalId = newId ()
                let initial : DonationGoalToUpsert =
                    { Id = goalId
                      Title = "First target"
                      GoalStars = 500
                      UpdatedAtUtc = atUtc }

                let! created =
                    AdminContentRepository.upsertDonationGoal dataSource initial CancellationToken.None
                    |> taskMap (expectOk "initial donation-goal upsert")

                Assert.That(created.RaisedStars, Is.EqualTo(0), "A newly created goal begins with no paid donations.")

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                do!
                    execute
                        connection
                        """UPDATE "DonationGoals" SET "RaisedStars" = 173 WHERE "Id" = @Id;"""
                        (fun command -> command.Parameters.AddWithValue("Id", goalId) |> ignore)

                let revised : DonationGoalToUpsert =
                    { initial with
                        Title = "Revised target"
                        GoalStars = 900
                        UpdatedAtUtc = atUtc.AddMinutes(1.0) }

                let! updated =
                    AdminContentRepository.upsertDonationGoal dataSource revised CancellationToken.None
                    |> taskMap (expectOk "goal revision after paid donations")

                Assert.Multiple(fun () ->
                    Assert.That(updated.Id, Is.EqualTo(goalId))
                    Assert.That(updated.Title, Is.EqualTo("Revised target"))
                    Assert.That(updated.GoalStars, Is.EqualTo(900))
                    Assert.That(updated.RaisedStars, Is.EqualTo(173), "Editing target metadata must never erase paid donation progress."))
            })

    [<Test>]
    let ``social replace preserves request order soft deletes omissions and rolls back a conflicting batch`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let firstId = newId ()
                let secondId = newId ()
                let first : SocialLinkReplacement =
                    { Id = firstId
                      Kind = "telegram"
                      Name = "Radio"
                      Handle = Some "@radio"
                      Url = "https://t.me/radio"
                      Glyph = None
                      Color = None
                      QrImageUrl = None
                      IsFeatured = true }

                let second : SocialLinkReplacement =
                    { Id = secondId
                      Kind = "youtube"
                      Name = "Archive"
                      Handle = None
                      Url = "https://youtube.example/radio"
                      Glyph = Some "play"
                      Color = Some "#112233"
                      QrImageUrl = None
                      IsFeatured = false }

                let! initial =
                    AdminContentRepository.replaceSocialLinks dataSource [ first; second ] atUtc CancellationToken.None
                    |> taskMap (expectOk "initial social replacement")

                let initialLinks =
                    match initial with
                    | AdminContentMutation.Applied values -> values
                    | actual -> Assert.Fail(sprintf "Expected social replacement to apply, but got %A." actual); []

                Assert.Multiple(fun () ->
                    Assert.That(initialLinks |> List.map _.Id, Is.EqualTo(box ([ firstId; secondId ] : Guid list)))
                    Assert.That(initialLinks |> List.map _.Position, Is.EqualTo(box ([ 0; 1 ] : int list)))
                    Assert.That(initialLinks[1].Handle, Is.EqualTo(""), "Nullable social values must project canonically for consumers."))

                let! omitted =
                    AdminContentRepository.replaceSocialLinks dataSource [ first ] (atUtc.AddMinutes(1.0)) CancellationToken.None
                    |> taskMap (expectOk "replacement omitting an old social")

                match omitted with
                | AdminContentMutation.Applied values -> Assert.That(values |> List.map _.Id, Is.EqualTo(box ([ firstId ] : Guid list)))
                | actual -> Assert.Fail(sprintf "Expected omission replacement to apply, but got %A." actual)

                let! deletedIdReuse =
                    AdminContentRepository.replaceSocialLinks dataSource [ second ] (atUtc.AddMinutes(2.0)) CancellationToken.None
                    |> taskMap (expectOk "reuse of a soft-deleted social id")

                match deletedIdReuse with
                | AdminContentMutation.NotFound -> ()
                | actual -> Assert.Fail(sprintf "Expected soft-deleted social reuse to return NotFound, but got %A." actual)

                let duplicateIdBatch = [ first; { first with Name = "Duplicate row" } ]
                let! conflict =
                    AdminContentRepository.replaceSocialLinks dataSource duplicateIdBatch (atUtc.AddMinutes(3.0)) CancellationToken.None
                    |> taskMap (expectOk "duplicate social-id batch")

                match conflict with
                | AdminContentMutation.Conflict -> ()
                | actual -> Assert.Fail(sprintf "Expected repeated social IDs to return Conflict, but got %A." actual)

                let! afterConflict =
                    AdminContentRepository.listSocialLinks dataSource CancellationToken.None
                    |> taskMap (expectOk "social list after the rejected batch")

                Assert.That(afterConflict |> List.map _.Id, Is.EqualTo(box ([ firstId ] : Guid list)), "A rejected replacement batch must leave the prior canonical set intact.")
            })

    [<Test>]
    let ``active-track listing projects nullable metadata ignores deleted rows and admin queue accepts only a playable track`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let cachedTrackId = newId ()
                let uncachedTrackId = newId ()
                let deletedTrackId = newId ()
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                do! seedTrack connection cachedTrackId "Cached track" "Artist" None None false atUtc
                do! seedTrack connection uncachedTrackId "Uncached track" "Artist 2" (Some "Album") (Some 1234) false (atUtc.AddMinutes(1.0))
                do! seedTrack connection deletedTrackId "Deleted track" "Ghost" None None true (atUtc.AddMinutes(2.0))
                do! seedCachedTrackFile connection (newId ()) cachedTrackId "/cache/cached.mp3"

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! tracks =
                    AdminContentRepository.listActiveTracks dataSource "" 10 CancellationToken.None
                    |> taskMap (expectOk "active admin track listing")

                let cached = tracks |> List.find (fun track -> track.Id = cachedTrackId)
                let uncached = tracks |> List.find (fun track -> track.Id = uncachedTrackId)

                Assert.Multiple(fun () ->
                    Assert.That(tracks |> List.map _.Id, Does.Not.Contain(deletedTrackId))
                    Assert.That(cached.Album, Is.EqualTo(""))
                    Assert.That(cached.DurationMs, Is.EqualTo(0))
                    Assert.That(cached.HasCachedFile, Is.True)
                    Assert.That(uncached.HasCachedFile, Is.False))

                let request : AdminQueueToCreate =
                    { Id = newId ()
                      TrackId = cachedTrackId
                      RequestedAtUtc = atUtc.AddMinutes(3.0) }

                let! queued =
                    AdminContentRepository.enqueueAdminTrack dataSource request CancellationToken.None
                    |> taskMap (expectOk "queueing the cached admin-selected track")

                match queued with
                | AdminContentMutation.Applied item ->
                    Assert.Multiple(fun () ->
                        Assert.That(item.Id, Is.EqualTo(request.Id))
                        Assert.That(item.TrackId, Is.EqualTo(cachedTrackId))
                        Assert.That(item.Source, Is.EqualTo("admin"))
                        Assert.That(item.Status, Is.EqualTo("Queued")))
                | actual -> Assert.Fail(sprintf "Expected admin queue insertion to apply, but got %A." actual)

                let missing : AdminQueueToCreate =
                    { request with
                        Id = newId ()
                        TrackId = deletedTrackId }

                let! missingResult =
                    AdminContentRepository.enqueueAdminTrack dataSource missing CancellationToken.None
                    |> taskMap (expectOk "queueing a deleted track")

                match missingResult with
                | AdminContentMutation.NotFound -> ()
                | actual -> Assert.Fail(sprintf "Expected a deleted queue target to return NotFound, but got %A." actual)
            })

    [<Test>]
    let ``playlist creation activation and item replacement enforce one active playlist and hide omitted items`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackOneId = newId ()
                let trackTwoId = newId ()
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                do! seedTrack connection trackOneId "First playlist track" "Artist" None None false atUtc
                do! seedTrack connection trackTwoId "Second playlist track" "Artist" None None false atUtc

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let firstPlaylist : PlaylistToCreate =
                    { Id = newId ()
                      Name = "Morning"
                      Description = Some "First active playlist"
                      IsActive = true
                      CreatedAtUtc = atUtc
                      UpdatedAtUtc = atUtc }

                let secondPlaylist : PlaylistToCreate =
                    { Id = newId ()
                      Name = "Evening"
                      Description = None
                      IsActive = true
                      CreatedAtUtc = atUtc.AddMinutes(1.0)
                      UpdatedAtUtc = atUtc.AddMinutes(1.0) }

                let! _ =
                    AdminContentRepository.createPlaylist dataSource firstPlaylist CancellationToken.None
                    |> taskMap (expectOk "creation of the first active playlist")

                let! _ =
                    AdminContentRepository.createPlaylist dataSource secondPlaylist CancellationToken.None
                    |> taskMap (expectOk "creation of the replacement active playlist")

                let! afterSecondCreate =
                    AdminContentRepository.listPlaylists dataSource CancellationToken.None
                    |> taskMap (expectOk "playlist list after second activation")

                Assert.That(afterSecondCreate |> List.filter _.IsActive |> List.map _.Id, Is.EqualTo(box ([ secondPlaylist.Id ] : Guid list)), "Creating an active playlist must transactionally deactivate the previous active playlist.")

                let firstItem : PlaylistItemToCreate =
                    { Id = newId ()
                      TrackId = trackOneId
                      CreatedAtUtc = atUtc.AddMinutes(2.0) }

                let! createdItem =
                    AdminContentRepository.createPlaylistItem dataSource firstPlaylist.Id firstItem CancellationToken.None
                    |> taskMap (expectOk "creation of the first playlist item")

                let createdItem =
                    match createdItem with
                    | AdminContentMutation.Applied value -> value
                    | actual -> Assert.Fail(sprintf "Expected playlist item creation to apply, but got %A." actual); Unchecked.defaultof<_>

                let replacement : PlaylistItemReplacement =
                    { Id = newId ()
                      TrackId = trackTwoId }

                let! replaced =
                    AdminContentRepository.replacePlaylistItems
                        dataSource
                        firstPlaylist.Id
                        [ replacement ]
                        (atUtc.AddMinutes(3.0))
                        CancellationToken.None
                    |> taskMap (expectOk "replacement of playlist items")

                match replaced with
                | AdminContentMutation.Applied items ->
                    Assert.Multiple(fun () ->
                        Assert.That(items |> List.map _.Id, Is.EqualTo(box ([ replacement.Id ] : Guid list)))
                        Assert.That(items |> List.map _.Position, Is.EqualTo(box ([ 0 ] : int list))))
                | actual -> Assert.Fail(sprintf "Expected playlist item replacement to apply, but got %A." actual)

                let! omittedItemReuse =
                    AdminContentRepository.replacePlaylistItems
                        dataSource
                        firstPlaylist.Id
                        [ { Id = createdItem.Id; TrackId = trackOneId } ]
                        (atUtc.AddMinutes(4.0))
                        CancellationToken.None
                    |> taskMap (expectOk "reuse of an omitted playlist item")

                match omittedItemReuse with
                | AdminContentMutation.NotFound -> ()
                | actual -> Assert.Fail(sprintf "Expected soft-deleted playlist-item reuse to return NotFound, but got %A." actual)

                let reactivateFirst : PlaylistUpdate =
                    { Name = "Morning"
                      Description = Some "Reactivated"
                      IsActive = true
                      UpdatedAtUtc = atUtc.AddMinutes(5.0) }

                let! activated =
                    AdminContentRepository.updatePlaylist dataSource firstPlaylist.Id reactivateFirst CancellationToken.None
                    |> taskMap (expectOk "reactivating the first playlist")

                match activated with
                | AdminContentMutation.Applied _ -> ()
                | actual -> Assert.Fail(sprintf "Expected playlist reactivation to apply, but got %A." actual)

                let! afterReactivation =
                    AdminContentRepository.listPlaylists dataSource CancellationToken.None
                    |> taskMap (expectOk "playlist list after reactivation")

                Assert.That(afterReactivation |> List.filter _.IsActive |> List.map _.Id, Is.EqualTo(box ([ firstPlaylist.Id ] : Guid list)), "Updating a playlist to active must retain the singleton-active invariant.")

                let! missingPlaylist =
                    AdminContentRepository.updatePlaylist dataSource (newId ()) reactivateFirst CancellationToken.None
                    |> taskMap (expectOk "update of a missing playlist")

                match missingPlaylist with
                | AdminContentMutation.NotFound -> ()
                | actual -> Assert.Fail(sprintf "Expected a missing playlist update to return NotFound, but got %A." actual)
            })

    [<Test>]
    let ``additional-storage replacement hides omissions and leaves the active set unchanged on a duplicate-id conflict`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let local : AdditionalStorageBackendReplacement =
                    { Id = newId ()
                      Name = "Local music"
                      Type = "Local"
                      LocalRoot = Some "/storage/music"
                      S3Bucket = None
                      IsEnabled = true }

                let s3 : AdditionalStorageBackendReplacement =
                    { Id = newId ()
                      Name = "Archive"
                      Type = "S3"
                      LocalRoot = None
                      S3Bucket = Some "radio-archive"
                      IsEnabled = false }

                let! initial =
                    AdminContentRepository.replaceAdditionalStorageBackends dataSource [ local; s3 ] atUtc CancellationToken.None
                    |> taskMap (expectOk "initial additional-storage replacement")

                match initial with
                | AdminContentMutation.Applied rows -> Assert.That(rows |> List.map _.Id, Is.EqualTo(box ([ local.Id; s3.Id ] : Guid list)))
                | actual -> Assert.Fail(sprintf "Expected additional-storage replacement to apply, but got %A." actual)

                let! omitted =
                    AdminContentRepository.replaceAdditionalStorageBackends dataSource [ local ] (atUtc.AddMinutes(1.0)) CancellationToken.None
                    |> taskMap (expectOk "additional-storage replacement omitting S3")

                match omitted with
                | AdminContentMutation.Applied rows -> Assert.That(rows |> List.map _.Id, Is.EqualTo(box ([ local.Id ] : Guid list)))
                | actual -> Assert.Fail(sprintf "Expected omission replacement to apply, but got %A." actual)

                let duplicate = [ local; { local with Name = "Duplicate local" } ]
                let! conflict =
                    AdminContentRepository.replaceAdditionalStorageBackends dataSource duplicate (atUtc.AddMinutes(2.0)) CancellationToken.None
                    |> taskMap (expectOk "duplicate additional-storage batch")

                match conflict with
                | AdminContentMutation.Conflict -> ()
                | actual -> Assert.Fail(sprintf "Expected repeated additional-storage IDs to return Conflict, but got %A." actual)

                let! afterConflict =
                    AdminContentRepository.listAdditionalStorageBackends dataSource CancellationToken.None
                    |> taskMap (expectOk "additional-storage list after rejected replacement")

                Assert.That(afterConflict |> List.map _.Id, Is.EqualTo(box ([ local.Id ] : Guid list)), "A rejected additional-storage replacement must roll back rather than partially retain a duplicate row.")
            })
