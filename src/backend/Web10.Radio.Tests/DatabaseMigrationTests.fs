namespace Web10.Radio.Tests

open System
open System.Threading
open Npgsql
open NUnit.Framework
open Web10.Radio.Database
open Web10.Radio.Database.Repositories

module DatabaseMigrationTests =
    let private readPublicTables (connection: NpgsqlConnection) =
        task {
            use command =
                new NpgsqlCommand(
                    """SELECT table_name
FROM information_schema.tables
WHERE table_schema = 'public'
ORDER BY table_name;""",
                    connection
                )

            let! reader = command.ExecuteReaderAsync()
            use reader = reader
            let tables = ResizeArray<string>()
            let mutable keepReading = true

            while keepReading do
                let! hasRow = reader.ReadAsync()

                if hasRow then
                    tables.Add(reader.GetString(0))
                else
                    keepReading <- false

            return Set.ofSeq tables
        }

    [<Test>]
    let ``migrator creates all first version tables and FluentMigrator version row`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                let! tables = readPublicTables connection

                let expectedDomainTables =
                    [ "Tracks"
                      "TrackLinks"
                      "TrackFiles"
                      "StorageBackends"
                      "Playlists"
                      "PlaylistItems"
                      "PlaybackQueue"
                      "TrackRequests"
                      "SayMessages"
                      "Payments"
                      "DonationGoals"
                      "SocialLinks"
                      "LibraryScanJobs"
                      "StreamNodeHeartbeats"
                      "OutboxEvents"
                      "TelegramUpdateInbox" ]

                let missingTables = expectedDomainTables |> List.filter (fun tableName -> not (tables.Contains tableName))
                Assert.That(missingTables, Is.Empty)

                use versionCommand =
                    new NpgsqlCommand(
                        """SELECT "Version" FROM "VersionInfo" WHERE "Version" = 202607080001;""",
                        connection
                    )

                let! version = versionCommand.ExecuteScalarAsync()
                Assert.That(version, Is.EqualTo(202607080001L))
            })

    [<Test>]
    let ``normal track reads exclude soft deleted rows`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let activeId = Guid.NewGuid()
                let deletedId = Guid.NewGuid()

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@ActiveId, 'Active title', 'Active artist', false),
       (@DeletedId, 'Deleted title', 'Deleted artist', true);""",
                        connection
                    )

                insertCommand.Parameters.AddWithValue("ActiveId", activeId) |> ignore
                insertCommand.Parameters.AddWithValue("DeletedId", deletedId) |> ignore
                let! _ = insertCommand.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! activeTracks = TrackRepository.listActive dataSource CancellationToken.None

                Assert.That(List.length activeTracks, Is.EqualTo(1))
                let track = List.head activeTracks
                Assert.That(track.Id, Is.EqualTo(activeId))
                Assert.That(track.Title, Is.EqualTo("Active title"))
                Assert.That(track.Artist, Is.EqualTo("Active artist"))
            })
