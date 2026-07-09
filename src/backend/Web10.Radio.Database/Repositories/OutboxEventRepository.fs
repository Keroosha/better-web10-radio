namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Npgsql
open NpgsqlTypes
open Web10.Radio.Database

type OutboxEventToAppend =
    { Id: Guid
      EventType: string
      OccurredAtUtc: DateTimeOffset
      Producer: string
      CorrelationId: Guid option
      CausationId: Guid option
      PayloadJson: string }

type OutboxEventRecord =
    { Id: Guid
      EventType: string
      OccurredAtUtc: DateTimeOffset
      Producer: string
      CorrelationId: Guid option
      CausationId: Guid option
      PayloadJson: string
      Attempts: int }

module OutboxEventRepository =
    let private processingLease = TimeSpan.FromSeconds(30.0)

    [<Literal>]
    let private appendSql = """INSERT INTO "OutboxEvents" (
    "Id", "EventType", "OccurredAtUtc", "Producer", "CorrelationId", "CausationId", "Payload",
    "Status", "Attempts", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (
    @Id, @EventType, @OccurredAtUtc, @Producer, @CorrelationId, @CausationId, @Payload::jsonb,
    'Pending', 0, false, @OccurredAtUtc, @OccurredAtUtc
);"""

    [<Literal>]
    let private claimDueSql = """WITH due_events AS (
    SELECT "Id"
    FROM "OutboxEvents"
    WHERE "IsDeleted" = false
      AND (
          ("Status" IN ('Pending', 'Failed') AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc" <= @NowUtc))
          OR ("Status" = 'Processing' AND "UpdatedAtUtc" <= @ProcessingLeaseExpiredBeforeUtc)
      )
    ORDER BY "OccurredAtUtc" ASC
    FOR UPDATE SKIP LOCKED
    LIMIT @BatchSize
)
UPDATE "OutboxEvents" AS e
SET "Status" = 'Processing',
    "Attempts" = e."Attempts" + 1,
    "UpdatedAtUtc" = @NowUtc
FROM due_events
WHERE e."Id" = due_events."Id"
RETURNING e."Id", e."EventType", e."OccurredAtUtc", e."Producer", e."CorrelationId", e."CausationId", e."Payload"::text, e."Attempts";"""

    [<Literal>]
    let private markProcessedSql = """UPDATE "OutboxEvents"
SET "Status" = 'Processed',
    "ProcessedAtUtc" = @ProcessedAtUtc,
    "UpdatedAtUtc" = @ProcessedAtUtc
WHERE "Id" = @EventId
  AND "IsDeleted" = false;"""

    [<Literal>]
    let private markFailedSql = """UPDATE "OutboxEvents"
SET "Status" = 'Failed',
    "NextAttemptAtUtc" = @NextAttemptAtUtc,
    "UpdatedAtUtc" = @FailedAtUtc
WHERE "Id" = @EventId
  AND "IsDeleted" = false;"""

    let private databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)

    let private addNullableUuid (command: NpgsqlCommand) name value =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Uuid)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value
        parameter |> ignore

    let private readNullableGuid (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetGuid(ordinal))

    let private readRecord (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          EventType = reader.GetString(1)
          OccurredAtUtc = reader.GetFieldValue<DateTimeOffset>(2)
          Producer = reader.GetString(3)
          CorrelationId = readNullableGuid reader 4
          CausationId = readNullableGuid reader 5
          PayloadJson = reader.GetString(6)
          Attempts = reader.GetInt32(7) }

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

    let claimDue
        (dataSource: NpgsqlDataSource)
        (nowUtc: DateTimeOffset)
        (batchSize: int)
        (cancellationToken: CancellationToken)
        : Task<Result<OutboxEventRecord list, RepositoryError>> =
        taskResult {
            do! (batchSize > 0) |> Result.requireTrue (InvalidBatchSize batchSize)

            try
                return!
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction cancellationToken ->
                            taskResult {
                                use command = new NpgsqlCommand(claimDueSql, connection, transaction)
                                command.Parameters.AddWithValue("NowUtc", nowUtc) |> ignore
                                command.Parameters.AddWithValue("ProcessingLeaseExpiredBeforeUtc", nowUtc - processingLease) |> ignore
                                command.Parameters.AddWithValue("BatchSize", batchSize) |> ignore
                                let! reader = command.ExecuteReaderAsync(cancellationToken)
                                use reader = reader
                                let records = ResizeArray<OutboxEventRecord>()
                                let mutable keepReading = true

                                while keepReading do
                                    let! hasRow = reader.ReadAsync(cancellationToken)

                                    if hasRow then
                                        records.Add(readRecord reader)
                                    else
                                        keepReading <- false

                                return List.ofSeq records
                            })
                        cancellationToken
            with ex ->
                return! Error(databaseError "OutboxEventRepository.claimDue" ex)
        }

    let markProcessed
        (dataSource: NpgsqlDataSource)
        (eventId: Guid)
        (processedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, RepositoryError>> =
        taskResult {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(markProcessedSql, connection)
                command.Parameters.AddWithValue("EventId", eventId) |> ignore
                command.Parameters.AddWithValue("ProcessedAtUtc", processedAtUtc) |> ignore
                let! _ = command.ExecuteNonQueryAsync(cancellationToken)
                return ()
            with ex ->
                return! Error(databaseError "OutboxEventRepository.markProcessed" ex)
        }

    let markFailed
        (dataSource: NpgsqlDataSource)
        (eventId: Guid)
        (nextAttemptAtUtc: DateTimeOffset)
        (failedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, RepositoryError>> =
        taskResult {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(markFailedSql, connection)
                command.Parameters.AddWithValue("EventId", eventId) |> ignore
                command.Parameters.AddWithValue("NextAttemptAtUtc", nextAttemptAtUtc) |> ignore
                command.Parameters.AddWithValue("FailedAtUtc", failedAtUtc) |> ignore
                let! _ = command.ExecuteNonQueryAsync(cancellationToken)
                return ()
            with ex ->
                return! Error(databaseError "OutboxEventRepository.markFailed" ex)
        }
