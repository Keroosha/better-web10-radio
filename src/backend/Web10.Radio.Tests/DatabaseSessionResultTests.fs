namespace Web10.Radio.Tests

open System
open System.Threading
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Database

module DatabaseSessionResultTests =
    let private newId () =
        (UuidV7IdGenerator() :> IIdGenerator).NewId()

    let private countTrack connection trackId =
        task {
            use command =
                new NpgsqlCommand(
                    """SELECT count(*) FROM "Tracks" WHERE "Id" = @TrackId;""",
                    connection
                )

            command.Parameters.AddWithValue("TrackId", trackId) |> ignore
            let! count = command.ExecuteScalarAsync()
            return Convert.ToInt32(count)
        }

    [<Test>]
    let ``withTransactionResult rolls back writes when work returns Error`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let trackId = newId ()

                let! result =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction cancellationToken ->
                            task {
                                use command =
                                    new NpgsqlCommand(
                                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackId, 'Rollback title', 'Rollback artist', false);""",
                                        connection,
                                        transaction
                                    )

                                command.Parameters.AddWithValue("TrackId", trackId) |> ignore
                                let! _ = command.ExecuteNonQueryAsync(cancellationToken)
                                return Error "rollback"
                            })
                        CancellationToken.None

                match result with
                | Error "rollback" -> ()
                | actual -> Assert.Fail(sprintf "Expected rollback error, but got %A." actual)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                let! rowCount = countTrack connection trackId
                Assert.That(rowCount, Is.EqualTo(0))
            })

    [<Test>]
    let ``withTransactionResult commits writes when work returns Ok`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let trackId = newId ()

                let! result =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction cancellationToken ->
                            task {
                                use command =
                                    new NpgsqlCommand(
                                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackId, 'Commit title', 'Commit artist', false);""",
                                        connection,
                                        transaction
                                    )

                                command.Parameters.AddWithValue("TrackId", trackId) |> ignore
                                let! _ = command.ExecuteNonQueryAsync(cancellationToken)
                                return Ok "committed"
                            })
                        CancellationToken.None

                match result with
                | Ok "committed" -> ()
                | actual -> Assert.Fail(sprintf "Expected committed ok result, but got %A." actual)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                let! rowCount = countTrack connection trackId
                Assert.That(rowCount, Is.EqualTo(1))
            })
