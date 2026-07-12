namespace Web10.Radio.Tests

open System
open System.Threading
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Dodo.Primitives
open Web10.Radio.Database.Repositories

module OutboxEventRepositoryTests =
    let private newId () = Uuid.CreateVersion7().ToGuidBigEndian()

    let private eventToAppend eventId occurredAtUtc =
        { Id = eventId
          EventType = "PlaybackStarted"
          Audience = OutboxAudience.Api
          OccurredAtUtc = occurredAtUtc
          Producer = "web10.radio.tests"
          CorrelationId = Some(newId ())
          CausationId = None
          PayloadJson = "{\"queueItemId\":\"018f12f0-4d20-7000-8000-000000000001\"}" }

    let private claim description result =
        match result with
        | Ok(Some lease) -> lease
        | actual -> Assert.Fail(sprintf "Expected %s to acquire the global outbox dispatch lease, but got %A." description actual); Unchecked.defaultof<_>

    let private assertOkTrue description result =
        match result with
        | Ok true -> ()
        | actual -> Assert.Fail(sprintf "Expected %s to return Ok true, but got %A." description actual)

    let private assertOkFalse description result =
        match result with
        | Ok false -> ()
        | actual -> Assert.Fail(sprintf "Expected %s to return Ok false, but got %A." description actual)

    [<Test>]
    let ``global outbox lease excludes overlapping relays and does not skip the earliest unfinished event`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let occurredAtUtc = DateTimeOffset(2026, 7, 10, 10, 0, 0, TimeSpan.Zero)
                let firstEventId = newId ()
                let secondEventId = newId ()
                let firstOwner = newId ()
                let secondOwner = newId ()

                for event in [ eventToAppend firstEventId occurredAtUtc; eventToAppend secondEventId (occurredAtUtc.AddMilliseconds(1.0)) ] do
                    let! appended = OutboxEventRepository.append dataSource event CancellationToken.None
                    match appended with
                    | Ok () -> ()
                    | actual -> Assert.Fail(sprintf "Expected append to succeed, but got %A." actual)

                let! firstLeaseResult =
                    OutboxEventRepository.tryClaimDueOrdered dataSource OutboxAudience.Api firstOwner occurredAtUtc 1 CancellationToken.None

                let firstLease = claim "the first relay" firstLeaseResult
                Assert.That(firstLease.Records |> List.length, Is.EqualTo(1))
                Assert.That((firstLease.Records |> List.exactlyOne).Id, Is.EqualTo(firstEventId))

                let! overlappingClaim =
                    OutboxEventRepository.tryClaimDueOrdered dataSource OutboxAudience.Api secondOwner occurredAtUtc 1 CancellationToken.None

                match overlappingClaim with
                | Ok None -> ()
                | actual -> Assert.Fail(sprintf "A direct publisher or second relay must not dispatch while the first relay owns the session lease, but got %A." actual)
                (firstLease :> IDisposable).Dispose()

                let! blockedByEarlierProcessing =
                    OutboxEventRepository.tryClaimDueOrdered dataSource OutboxAudience.Api secondOwner (occurredAtUtc.AddSeconds(1.0)) 1 CancellationToken.None

                let emptyLease = claim "the relay after the first lease released" blockedByEarlierProcessing
                Assert.That(emptyLease.Records, Is.Empty, "The second event must not overtake the earlier Processing event.")
                (emptyLease :> IDisposable).Dispose()

                let! firstProcessed =
                    OutboxEventRepository.markProcessed dataSource firstEventId firstOwner 1 (occurredAtUtc.AddSeconds(2.0)) CancellationToken.None

                assertOkTrue "the first owner terminal update" firstProcessed

                let! nextLeaseResult =
                    OutboxEventRepository.tryClaimDueOrdered dataSource OutboxAudience.Api secondOwner (occurredAtUtc.AddSeconds(3.0)) 1 CancellationToken.None

                let nextLease = claim "the second event after the first completed" nextLeaseResult
                Assert.That(nextLease.Records |> List.length, Is.EqualTo(1))
                Assert.That((nextLease.Records |> List.exactlyOne).Id, Is.EqualTo(secondEventId))
                (nextLease :> IDisposable).Dispose()
            })

    [<Test>]
    let ``expired outbox attempt cannot overwrite the replacement owner terminal state`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let occurredAtUtc = DateTimeOffset(2026, 7, 10, 11, 0, 0, TimeSpan.Zero)
                let eventId = newId ()
                let firstOwner = newId ()
                let replacementOwner = newId ()
                let! appended = OutboxEventRepository.append dataSource (eventToAppend eventId occurredAtUtc) CancellationToken.None
                match appended with
                | Ok () -> ()
                | actual -> Assert.Fail(sprintf "Expected append to succeed, but got %A." actual)

                let! firstLeaseResult =
                    OutboxEventRepository.tryClaimDueOrdered dataSource OutboxAudience.Api firstOwner occurredAtUtc 1 CancellationToken.None

                let firstLease = claim "the original attempt" firstLeaseResult
                let firstRecord = firstLease.Records |> List.exactlyOne
                (firstLease :> IDisposable).Dispose()

                let! replacementLeaseResult =
                    OutboxEventRepository.tryClaimDueOrdered dataSource OutboxAudience.Api replacementOwner (occurredAtUtc.AddSeconds(31.0)) 1 CancellationToken.None

                let replacementLease = claim "the lease-expired replacement attempt" replacementLeaseResult
                let replacementRecord = replacementLease.Records |> List.exactlyOne
                Assert.That(replacementRecord.Id, Is.EqualTo(eventId))
                Assert.That(replacementRecord.ClaimAttempt, Is.EqualTo(firstRecord.ClaimAttempt + 1))

                let! staleSuccess =
                    OutboxEventRepository.markProcessed dataSource eventId firstRecord.ClaimOwner firstRecord.ClaimAttempt (occurredAtUtc.AddSeconds(32.0)) CancellationToken.None

                let! staleFailure =
                    OutboxEventRepository.markFailed dataSource eventId firstRecord.ClaimOwner firstRecord.ClaimAttempt (occurredAtUtc.AddMinutes(1.0)) (occurredAtUtc.AddSeconds(32.0)) CancellationToken.None

                assertOkFalse "the stale owner success fence" staleSuccess
                assertOkFalse "the stale owner failure fence" staleFailure

                let! replacementSuccess =
                    OutboxEventRepository.markProcessed dataSource eventId replacementRecord.ClaimOwner replacementRecord.ClaimAttempt (occurredAtUtc.AddSeconds(33.0)) CancellationToken.None

                assertOkTrue "the replacement owner success fence" replacementSuccess
                (replacementLease :> IDisposable).Dispose()

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use command =
                    new NpgsqlCommand(
                        """SELECT "Status", "Attempts", "ProcessedAtUtc", "NextAttemptAtUtc"
FROM "OutboxEvents"
WHERE "Id" = @EventId;""",
                        connection
                    )

                command.Parameters.AddWithValue("EventId", eventId) |> ignore
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Processed"))
                Assert.That(reader.GetInt32(1), Is.EqualTo(2))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(2), Is.EqualTo(occurredAtUtc.AddSeconds(33.0)))
                Assert.That(reader.IsDBNull(3), Is.True, "The stale failure must not install a retry schedule on the replacement attempt.")
            })
