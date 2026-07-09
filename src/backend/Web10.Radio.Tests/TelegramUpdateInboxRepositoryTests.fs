namespace Web10.Radio.Tests

open System
open System.Threading
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Database.Repositories

module TelegramUpdateInboxRepositoryTests =
    let private newId () =
        (UuidV7IdGenerator() :> IIdGenerator).NewId()

    let private record telegramUpdateId eventType receivedAtUtc =
        { Id = newId ()
          TelegramUpdateId = telegramUpdateId
          EventType = eventType
          ReceivedAtUtc = receivedAtUtc
          CorrelationId = Some(newId ())
          PayloadJson = "{\"message\":\"hello\"}" }

    [<Test>]
    let ``tryRecord suppresses duplicate telegram update and event type pairs`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let receivedAtUtc = DateTimeOffset(2026, 7, 8, 11, 0, 0, TimeSpan.Zero)
                let telegramUpdateId = 123456789L
                let eventType = "SayMessageSubmitted"

                let! firstResult =
                    TelegramUpdateInboxRepository.tryRecord dataSource (record telegramUpdateId eventType receivedAtUtc) CancellationToken.None

                let! secondResult =
                    TelegramUpdateInboxRepository.tryRecord dataSource (record telegramUpdateId eventType receivedAtUtc) CancellationToken.None

                match firstResult with
                | Ok true -> ()
                | actual -> Assert.Fail(sprintf "Expected first telegram update record to insert, but got %A." actual)

                match secondResult with
                | Ok false -> ()
                | actual -> Assert.Fail(sprintf "Expected duplicate telegram update record to be suppressed, but got %A." actual)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use command =
                    new NpgsqlCommand(
                        """SELECT count(*), min("Payload"::text)
FROM "TelegramUpdateInbox"
WHERE "TelegramUpdateId" = @TelegramUpdateId
  AND "EventType" = @EventType
  AND "IsDeleted" = false;""",
                        connection
                    )

                command.Parameters.AddWithValue("TelegramUpdateId", telegramUpdateId) |> ignore
                command.Parameters.AddWithValue("EventType", eventType) |> ignore
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetInt64(0), Is.EqualTo(1L))
                Assert.That(reader.GetString(1), Does.Contain("message"))
            })
