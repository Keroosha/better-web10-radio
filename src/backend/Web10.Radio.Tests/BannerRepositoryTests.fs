namespace Web10.Radio.Tests

open System
open System.Threading
open Npgsql
open Dodo.Primitives
open NUnit.Framework
open Web10.Radio.Database.Repositories

module BannerRepositoryTests =
    let private newId () = Uuid.CreateVersion7().ToGuidBigEndian()

    let private banner id bannerType title =
        { Id = id
          Type = bannerType
          Title = title
          Subtitle = None
          Text = None
          Style = "aero"
          ScreenPosition = "top-left"
          Accent = Some "#2ecc71"
          Enabled = true
          RotationSeconds = None }


    [<Test>]
    let ``migrated superchat banner seed is active and type constraint rejects unknown values`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! listed = AdminContentRepository.listBanners dataSource CancellationToken.None

                match listed with
                | Ok banners ->
                    let superChats = banners |> List.filter (fun banner -> banner.Type = "superchat")
                    Assert.That(superChats |> List.length, Is.EqualTo(1), "The migration must seed exactly one active superchat banner.")
                    let superChat = superChats |> List.head
                    Assert.That(superChat.Title, Is.EqualTo("SUPER CHAT"))
                    Assert.That(superChat.Enabled, Is.True)
                    Assert.That(superChat.ScreenPosition, Is.EqualTo("bottom-left"))
                    Assert.That(superChat.RotationSeconds, Is.EqualTo(0))
                | actual -> Assert.Fail(sprintf "Expected the seeded banner list, got %A." actual)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use invalidInsert =
                    new NpgsqlCommand(
                        """INSERT INTO "Banners" ("Id", "Type", "Title", "Style", "ScreenPosition")
VALUES (gen_random_uuid(), 'invalid', 'INVALID', 'aero', 'top-left');""",
                        connection
                    )

                let! failure =
                    task {
                        try
                            let! _ = invalidInsert.ExecuteNonQueryAsync()
                            return None
                        with error ->
                            return Some error
                    }

                match failure with
                | Some (:? PostgresException as error) ->
                    Assert.That(error.SqlState, Is.EqualTo("23514"))
                    Assert.That(error.ConstraintName, Is.EqualTo("Banners_Type_check"))
                | _ -> Assert.Fail("Banners_Type_check must reject unknown banner types.")
            })
    [<Test>]
    let ``replacing banners upserts and soft-deletes omitted rows`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let nowUtc = DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero)
                use dataSource = NpgsqlDataSource.Create(connectionString)

                let firstId = newId ()
                let secondId = newId ()
                let! replaced =
                    AdminContentRepository.replaceBanners
                        dataSource
                        [ banner firstId "nowplaying" "NOW PLAYING"; banner secondId "custom" "CUSTOM" ]
                        nowUtc
                        CancellationToken.None

                match replaced with
                | Ok(AdminContentMutation.Applied banners) ->
                    Assert.That(banners |> List.length, Is.EqualTo(2), "The seed banners must be replaced by the two provided rows.")
                    Assert.That(banners |> List.map _.Title |> String.concat ",", Is.EqualTo("NOW PLAYING,CUSTOM"))
                | actual -> Assert.Fail(sprintf "Expected Applied, got %A." actual)

                let! afterDrop =
                    AdminContentRepository.replaceBanners
                        dataSource
                        [ banner firstId "nowplaying" "ONLY NOW PLAYING" ]
                        (nowUtc.AddMinutes(1.0))
                        CancellationToken.None

                match afterDrop with
                | Ok(AdminContentMutation.Applied banners) ->
                    Assert.That(banners |> List.length, Is.EqualTo(1), "The omitted banner must be soft-deleted.")
                    Assert.That((banners |> List.head).Title, Is.EqualTo("ONLY NOW PLAYING"), "The retained banner must reflect the update.")
                | actual -> Assert.Fail(sprintf "Expected Applied, got %A." actual)

                let! listed = AdminContentRepository.listBanners dataSource CancellationToken.None
                match listed with
                | Ok banners -> Assert.That(banners |> List.length, Is.EqualTo(1))
                | actual -> Assert.Fail(sprintf "Expected the active banner list, got %A." actual)
            })

    [<Test>]
    let ``stream node control persists the paused desired state`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let nowUtc = DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero)
                use dataSource = NpgsqlDataSource.Create(connectionString)

                let! _ = StreamNodeControlRepository.getOrCreate dataSource (newId ()) nowUtc CancellationToken.None
                let! paused = StreamNodeControlRepository.setDesiredState dataSource StreamNodeDesiredState.Paused (nowUtc.AddMinutes(1.0)) CancellationToken.None
                match paused with
                | Ok state -> Assert.That(state.DesiredState, Is.EqualTo(StreamNodeDesiredState.Paused))
                | actual -> Assert.Fail(sprintf "Expected the paused state, got %A." actual)

                let! reread = StreamNodeControlRepository.getOrCreate dataSource (newId ()) (nowUtc.AddMinutes(2.0)) CancellationToken.None
                match reread with
                | Ok state -> Assert.That(state.DesiredState, Is.EqualTo(StreamNodeDesiredState.Paused), "The paused desired state must survive a re-read.")
                | actual -> Assert.Fail(sprintf "Expected the persisted paused state, got %A." actual)
            })
