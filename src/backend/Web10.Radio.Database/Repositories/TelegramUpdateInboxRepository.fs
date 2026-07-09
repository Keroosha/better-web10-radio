namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Npgsql
open NpgsqlTypes
open Web10.Radio.Database

type TelegramUpdateInboxRecord =
    { Id: Guid
      TelegramUpdateId: int64
      EventType: string
      ReceivedAtUtc: DateTimeOffset
      CorrelationId: Guid option
      PayloadJson: string }

module TelegramUpdateInboxRepository =
    [<Literal>]
    let private insertSql = """INSERT INTO "TelegramUpdateInbox" (
    "Id", "TelegramUpdateId", "EventType", "ReceivedAtUtc", "CorrelationId", "Payload",
    "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (
    @Id, @TelegramUpdateId, @EventType, @ReceivedAtUtc, @CorrelationId, @Payload::jsonb,
    false, @ReceivedAtUtc, @ReceivedAtUtc
)
ON CONFLICT ("TelegramUpdateId", "EventType") WHERE "IsDeleted" = false DO NOTHING;"""

    let private databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)

    let private addNullableUuid (command: NpgsqlCommand) name value =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Uuid)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value
        parameter |> ignore

    let tryRecordInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (record: TelegramUpdateInboxRecord)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(insertSql, connection, transaction)
                command.Parameters.AddWithValue("Id", record.Id) |> ignore
                command.Parameters.AddWithValue("TelegramUpdateId", record.TelegramUpdateId) |> ignore
                command.Parameters.AddWithValue("EventType", record.EventType) |> ignore
                command.Parameters.AddWithValue("ReceivedAtUtc", record.ReceivedAtUtc) |> ignore
                addNullableUuid command "CorrelationId" record.CorrelationId
                command.Parameters.AddWithValue("Payload", record.PayloadJson) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "TelegramUpdateInboxRepository.tryRecordInTransaction" ex)
        }

    let tryRecord
        (dataSource: NpgsqlDataSource)
        (record: TelegramUpdateInboxRecord)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken -> tryRecordInTransaction connection transaction record cancellationToken)
            cancellationToken
