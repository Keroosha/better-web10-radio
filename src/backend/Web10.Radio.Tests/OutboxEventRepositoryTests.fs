namespace Web10.Radio.Tests

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Database.Repositories

module OutboxEventRepositoryTests =
    let private newId () =
        (UuidV7IdGenerator() :> IIdGenerator).NewId()

    let private eventToAppend eventId occurredAtUtc =
        { Id = eventId
          EventType = "PlaybackStarted"
          OccurredAtUtc = occurredAtUtc
          Producer = "web10.radio.tests"
          CorrelationId = Some(newId ())
          CausationId = None
          PayloadJson = "{\"queueItemId\":\"018f12f0-4d20-7000-8000-000000000001\"}" }

    [<Test>]
    let ``append claimDue concurrency invalid batch and markProcessed persist expected outbox state`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let eventId = newId ()
                let occurredAtUtc = DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero)
                let claimedAtUtc = occurredAtUtc.AddSeconds(5.0)
                let processedAtUtc = occurredAtUtc.AddSeconds(10.0)

                let! appendResult = OutboxEventRepository.append dataSource (eventToAppend eventId occurredAtUtc) CancellationToken.None
                match appendResult with
                | Ok () -> ()
                | actual -> Assert.Fail(sprintf "Expected append to succeed, but got %A." actual)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                use pendingCommand =
                    new NpgsqlCommand(
                        """SELECT "Status", "Attempts", "Payload"::text
FROM "OutboxEvents"
WHERE "Id" = @EventId;""",
                        connection
                    )

                pendingCommand.Parameters.AddWithValue("EventId", eventId) |> ignore
                let! pendingReader = pendingCommand.ExecuteReaderAsync()
                use pendingReader = pendingReader
                let! hasPendingRow = pendingReader.ReadAsync()
                Assert.That(hasPendingRow, Is.True)
                Assert.That(pendingReader.GetString(0), Is.EqualTo("Pending"))
                Assert.That(pendingReader.GetInt32(1), Is.EqualTo(0))
                Assert.That(pendingReader.GetString(2), Does.Contain("queueItemId"))
                do! pendingReader.CloseAsync()

                let claimTasks =
                    [| for _ in 1..4 ->
                           OutboxEventRepository.claimDue dataSource claimedAtUtc 1 CancellationToken.None |]

                let! claimResults = Task.WhenAll(claimTasks)
                let claimedLists =
                    claimResults
                    |> Array.map (function
                        | Ok records -> records
                        | Error error -> Assert.Fail(sprintf "claimDue should not fail, but got %A." error); [])

                let claimedRecords = claimedLists |> Array.collect List.toArray
                Assert.That(claimedRecords.Length, Is.EqualTo(1))
                Assert.That(claimedRecords[0].Id, Is.EqualTo(eventId))
                Assert.That(claimedRecords[0].Attempts, Is.EqualTo(1))
                Assert.That(claimedLists |> Array.filter List.isEmpty |> Array.length, Is.EqualTo(3))

                use processingCommand =
                    new NpgsqlCommand(
                        """SELECT "Status", "Attempts", "UpdatedAtUtc"
FROM "OutboxEvents"
WHERE "Id" = @EventId;""",
                        connection
                    )

                processingCommand.Parameters.AddWithValue("EventId", eventId) |> ignore
                let! processingReader = processingCommand.ExecuteReaderAsync()
                use processingReader = processingReader
                let! hasProcessingRow = processingReader.ReadAsync()
                Assert.That(hasProcessingRow, Is.True)
                Assert.That(processingReader.GetString(0), Is.EqualTo("Processing"))
                Assert.That(processingReader.GetInt32(1), Is.EqualTo(1))
                Assert.That(processingReader.GetFieldValue<DateTimeOffset>(2), Is.EqualTo(claimedAtUtc))
                do! processingReader.CloseAsync()

                let! invalidBatchResult = OutboxEventRepository.claimDue dataSource claimedAtUtc 0 CancellationToken.None
                match invalidBatchResult with
                | Error(InvalidBatchSize 0) -> ()
                | actual -> Assert.Fail(sprintf "Expected invalid batch size error, but got %A." actual)

                let! markProcessedResult = OutboxEventRepository.markProcessed dataSource eventId processedAtUtc CancellationToken.None
                match markProcessedResult with
                | Ok () -> ()
                | actual -> Assert.Fail(sprintf "Expected markProcessed to succeed, but got %A." actual)

                use processedCommand =
                    new NpgsqlCommand(
                        """SELECT "Status", "ProcessedAtUtc", "UpdatedAtUtc"
FROM "OutboxEvents"
WHERE "Id" = @EventId;""",
                        connection
                    )

                processedCommand.Parameters.AddWithValue("EventId", eventId) |> ignore
                let! processedReader = processedCommand.ExecuteReaderAsync()
                use processedReader = processedReader
                let! hasProcessedRow = processedReader.ReadAsync()
                Assert.That(hasProcessedRow, Is.True)
                Assert.That(processedReader.GetString(0), Is.EqualTo("Processed"))
                Assert.That(processedReader.GetFieldValue<DateTimeOffset>(1), Is.EqualTo(processedAtUtc))
                Assert.That(processedReader.GetFieldValue<DateTimeOffset>(2), Is.EqualTo(processedAtUtc))
            })

    [<Test>]
    let ``claimDue only reclaims Processing rows after processing lease expires`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let eventId = newId ()
                let occurredAtUtc = DateTimeOffset(2026, 7, 8, 11, 0, 0, TimeSpan.Zero)
                let claimedAtUtc = occurredAtUtc.AddSeconds(5.0)

                let! appendResult = OutboxEventRepository.append dataSource (eventToAppend eventId occurredAtUtc) CancellationToken.None
                match appendResult with
                | Ok () -> ()
                | actual -> Assert.Fail(sprintf "Expected append to succeed, but got %A." actual)

                let! firstClaim = OutboxEventRepository.claimDue dataSource claimedAtUtc 1 CancellationToken.None
                match firstClaim with
                | Ok [ record ] ->
                    Assert.That(record.Id, Is.EqualTo(eventId))
                    Assert.That(record.Attempts, Is.EqualTo(1))
                | actual -> Assert.Fail(sprintf "Expected first claim to return the outbox event once, but got %A." actual)

                let! notExpiredClaim = OutboxEventRepository.claimDue dataSource (claimedAtUtc.AddSeconds(29.0)) 1 CancellationToken.None
                match notExpiredClaim with
                | Ok [] -> ()
                | actual -> Assert.Fail(sprintf "Expected processing lease to block reclaim at T+29s, but got %A." actual)

                let! expiredClaim = OutboxEventRepository.claimDue dataSource (claimedAtUtc.AddSeconds(31.0)) 1 CancellationToken.None
                match expiredClaim with
                | Ok [ record ] ->
                    Assert.That(record.Id, Is.EqualTo(eventId))
                    Assert.That(record.Attempts, Is.EqualTo(2))
                | actual -> Assert.Fail(sprintf "Expected processing lease to allow reclaim at T+31s, but got %A." actual)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use command =
                    new NpgsqlCommand(
                        """SELECT "Status", "Attempts", "UpdatedAtUtc"
FROM "OutboxEvents"
WHERE "Id" = @EventId
  AND "IsDeleted" = false;""",
                        connection
                    )

                command.Parameters.AddWithValue("EventId", eventId) |> ignore
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Processing"))
                Assert.That(reader.GetInt32(1), Is.EqualTo(2))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(2), Is.EqualTo(claimedAtUtc.AddSeconds(31.0)))
            })
