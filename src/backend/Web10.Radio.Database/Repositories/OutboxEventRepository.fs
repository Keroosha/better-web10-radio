namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Npgsql
open NpgsqlTypes
open Web10.Radio.Database

[<RequireQualifiedAccess>]
type OutboxAudience =
    | Api
    | Telegram
    member this.DbLiteral =
        match this with
        | OutboxAudience.Api -> "Api"
        | OutboxAudience.Telegram -> "Telegram"

type OutboxEventToAppend =
    { Id: Guid
      EventType: string
      Audience: OutboxAudience
      OccurredAtUtc: DateTimeOffset
      Producer: string
      CorrelationId: Guid option
      CausationId: Guid option
      PayloadJson: string }

type OutboxEventRecord =
    { Id: Guid
      EventType: string
      Audience: OutboxAudience
      OccurredAtUtc: DateTimeOffset
      Producer: string
      CorrelationId: Guid option
      CausationId: Guid option
      PayloadJson: string
      ClaimOwner: Guid
      ClaimAttempt: int
      LeaseExpiresAtUtc: DateTimeOffset }

type OutboxDispatchLease internal (connection: NpgsqlConnection, lockName: string, records: OutboxEventRecord list) =
    let mutable disposed = 0

    member _.Records = records

    member private _.Release() =
        if Interlocked.Exchange(&disposed, 1) = 0 then
            try
                use command =
                    new NpgsqlCommand(
                        $"SELECT pg_advisory_unlock(hashtext('{lockName}'));",
                        connection
                    )

                command.ExecuteScalar() |> ignore
            with _ ->
                ()

            connection.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Release()

module OutboxEventRepository =
    let private processingLease = TimeSpan.FromSeconds(30.0)

    [<Literal>]
    let private appendSql = """INSERT INTO "OutboxEvents" (
    "Id", "EventType", "Audience", "OccurredAtUtc", "Producer", "CorrelationId", "CausationId", "Payload",
    "Status", "Attempts", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (
    @Id, @EventType, @Audience, @OccurredAtUtc, @Producer, @CorrelationId, @CausationId, @Payload::jsonb,
    'Pending', 0, false, @OccurredAtUtc, @OccurredAtUtc
);"""

    let private dispatchLockName audience =
        match audience with
        | OutboxAudience.Api -> "web10.radio.outbox-dispatch-api"
        | OutboxAudience.Telegram -> "web10.radio.outbox-dispatch-telegram"

    let private tryDispatchLockSql audience =
        $"SELECT pg_try_advisory_lock(hashtext('{dispatchLockName audience}'));"

    let private releaseDispatchLockSql lockName =
        $"SELECT pg_advisory_unlock(hashtext('{lockName}'));"

    [<Literal>]
    let private deadLetterAttempts = 20

    [<Literal>]
    let private claimEarliestDueSql = """WITH first_unfinished AS (
    SELECT "Id"
    FROM "OutboxEvents"
    WHERE "IsDeleted" = false
      AND "Status" <> 'Processed'
      AND "Audience" = @Audience
      AND NOT ("Status" = 'Failed' AND "Attempts" >= @MaxAttempts)
    ORDER BY "OccurredAtUtc" ASC, "CreatedAtUtc" ASC, "Id" ASC
    FOR UPDATE
    LIMIT 1
), due_event AS (
    SELECT e."Id"
    FROM "OutboxEvents" e
    INNER JOIN first_unfinished first ON first."Id" = e."Id"
    WHERE e."Audience" = @Audience
      AND (
        (e."Status" IN ('Pending', 'Failed') AND (e."NextAttemptAtUtc" IS NULL OR e."NextAttemptAtUtc" <= @NowUtc))
        OR (e."Status" = 'Processing' AND (e."ClaimLeaseExpiresAtUtc" IS NULL OR e."ClaimLeaseExpiresAtUtc" <= @NowUtc))
    )
)
UPDATE "OutboxEvents" AS e
SET "Status" = 'Processing',
    "Attempts" = e."Attempts" + 1,
    "ClaimOwner" = @ClaimOwner,
    "ClaimLeaseExpiresAtUtc" = @LeaseExpiresAtUtc,
    "UpdatedAtUtc" = @NowUtc
FROM due_event
WHERE e."Id" = due_event."Id"
RETURNING e."Id", e."EventType", e."Audience", e."OccurredAtUtc", e."Producer", e."CorrelationId", e."CausationId",
          e."Payload"::text, e."ClaimOwner", e."Attempts", e."ClaimLeaseExpiresAtUtc";"""

    [<Literal>]
    let private markProcessedSql = """UPDATE "OutboxEvents"
SET "Status" = 'Processed',
    "ProcessedAtUtc" = @ProcessedAtUtc,
    "ClaimOwner" = NULL,
    "ClaimLeaseExpiresAtUtc" = NULL,
    "UpdatedAtUtc" = @ProcessedAtUtc
WHERE "Id" = @EventId
  AND "IsDeleted" = false
  AND "Status" = 'Processing'
  AND "ClaimOwner" = @ClaimOwner
  AND "Attempts" = @ClaimAttempt;"""

    [<Literal>]
    let private markFailedSql = """UPDATE "OutboxEvents"
SET "Status" = 'Failed',
    "NextAttemptAtUtc" = @NextAttemptAtUtc,
    "ClaimOwner" = NULL,
    "ClaimLeaseExpiresAtUtc" = NULL,
    "UpdatedAtUtc" = @FailedAtUtc
WHERE "Id" = @EventId
  AND "IsDeleted" = false
  AND "Status" = 'Processing'
  AND "ClaimOwner" = @ClaimOwner
  AND "Attempts" = @ClaimAttempt;"""

    let private databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)

    let private addNullableUuid (command: NpgsqlCommand) name value =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Uuid)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value
        parameter |> ignore

    let private readNullableGuid (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetGuid(ordinal))

    let private readAudience (reader: NpgsqlDataReader) ordinal =
        match reader.GetString(ordinal) with
        | "Api" -> OutboxAudience.Api
        | "Telegram" -> OutboxAudience.Telegram
        | value -> invalidArg "Audience" $"Unknown outbox audience '{value}'."

    let private readRecord (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          EventType = reader.GetString(1)
          Audience = readAudience reader 2
          OccurredAtUtc = reader.GetFieldValue<DateTimeOffset>(3)
          Producer = reader.GetString(4)
          CorrelationId = readNullableGuid reader 5
          CausationId = readNullableGuid reader 6
          PayloadJson = reader.GetString(7)
          ClaimOwner = reader.GetGuid(8)
          ClaimAttempt = reader.GetInt32(9)
          LeaseExpiresAtUtc = reader.GetFieldValue<DateTimeOffset>(10) }


    let appendInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (event: OutboxEventToAppend)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(appendSql, connection, transaction)
                command.Parameters.AddWithValue("Id", event.Id) |> ignore
                command.Parameters.AddWithValue("EventType", event.EventType) |> ignore
                command.Parameters.AddWithValue("Audience", event.Audience.DbLiteral) |> ignore
                command.Parameters.AddWithValue("OccurredAtUtc", event.OccurredAtUtc) |> ignore
                command.Parameters.AddWithValue("Producer", event.Producer) |> ignore
                addNullableUuid command "CorrelationId" event.CorrelationId
                addNullableUuid command "CausationId" event.CausationId
                command.Parameters.AddWithValue("Payload", event.PayloadJson) |> ignore
                let! _ = command.ExecuteNonQueryAsync(cancellationToken)
                return ()
            with ex ->
                return! Error(databaseError "OutboxEventRepository.appendInTransaction" ex)
        }

    let append
        (dataSource: NpgsqlDataSource)
        (event: OutboxEventToAppend)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken -> appendInTransaction connection transaction event cancellationToken)
            cancellationToken

    let tryClaimDueOrdered
        (dataSource: NpgsqlDataSource)
        (audience: OutboxAudience)
        (claimOwner: Guid)
        (nowUtc: DateTimeOffset)
        (batchSize: int)
        (cancellationToken: CancellationToken)
        : Task<Result<OutboxDispatchLease option, RepositoryError>> =
        taskResult {
            do! (batchSize > 0) |> Result.requireTrue (InvalidBatchSize batchSize)

            let lockName = dispatchLockName audience
            let mutable connection: NpgsqlConnection = null
            let mutable lockHeld = false

            try
                let! openedConnection = dataSource.OpenConnectionAsync(cancellationToken)
                connection <- openedConnection
                use lockCommand = new NpgsqlCommand(tryDispatchLockSql audience, connection)
                let! acquired = lockCommand.ExecuteScalarAsync(cancellationToken)
                lockHeld <- Convert.ToBoolean(acquired)

                if not lockHeld then
                    connection.Dispose()
                    connection <- null
                    return None
                else
                    use! transaction = connection.BeginTransactionAsync(cancellationToken)
                    use command = new NpgsqlCommand(claimEarliestDueSql, connection, transaction)
                    command.Parameters.AddWithValue("Audience", audience.DbLiteral) |> ignore
                    command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                    command.Parameters.AddWithValue("NowUtc", nowUtc) |> ignore
                    command.Parameters.AddWithValue("LeaseExpiresAtUtc", nowUtc + processingLease) |> ignore
                    command.Parameters.AddWithValue("MaxAttempts", deadLetterAttempts) |> ignore
                    let! reader = command.ExecuteReaderAsync(cancellationToken)
                    use reader = reader
                    let! hasRow = reader.ReadAsync(cancellationToken)
                    let records = if hasRow then [ readRecord reader ] else []
                    do! reader.CloseAsync()
                    do! transaction.CommitAsync(cancellationToken)
                    let lease = new OutboxDispatchLease(connection, lockName, records)
                    connection <- null
                    lockHeld <- false
                    return Some lease
            with ex ->
                if not (isNull connection) then
                    if lockHeld then
                        try
                            use unlockCommand = new NpgsqlCommand(releaseDispatchLockSql lockName, connection)
                            unlockCommand.ExecuteScalar() |> ignore
                        with _ ->
                            ()

                    connection.Dispose()

                return! Error(databaseError "OutboxEventRepository.tryClaimDueOrdered" ex)
        }

    let markProcessed
        (dataSource: NpgsqlDataSource)
        (eventId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (processedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(markProcessedSql, connection)
                command.Parameters.AddWithValue("EventId", eventId) |> ignore
                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                command.Parameters.AddWithValue("ClaimAttempt", claimAttempt) |> ignore
                command.Parameters.AddWithValue("ProcessedAtUtc", processedAtUtc) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "OutboxEventRepository.markProcessed" ex)
        }

    let markFailed
        (dataSource: NpgsqlDataSource)
        (eventId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (nextAttemptAtUtc: DateTimeOffset)
        (failedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(markFailedSql, connection)
                command.Parameters.AddWithValue("EventId", eventId) |> ignore
                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                command.Parameters.AddWithValue("ClaimAttempt", claimAttempt) |> ignore
                command.Parameters.AddWithValue("NextAttemptAtUtc", nextAttemptAtUtc) |> ignore
                command.Parameters.AddWithValue("FailedAtUtc", failedAtUtc) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "OutboxEventRepository.markFailed" ex)
        }
