namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Npgsql
open NpgsqlTypes

module StreamNodeHeartbeatRepository =
    [<Literal>]
    let private insertSql = """INSERT INTO "StreamNodeHeartbeats" (
    "Id", "Status", "HeartbeatAtUtc", "FailureReason", "Metadata",
    "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (
    @Id, @Status, @HeartbeatAtUtc, @FailureReason, @Metadata::jsonb,
    false, @HeartbeatAtUtc, @HeartbeatAtUtc
);"""

    let private validStatuses =
        Set.ofList [ "Starting"; "Live"; "Degraded"; "Restarting"; "Failed"; "Offline" ]

    let private databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)

    let private addNullableText (command: NpgsqlCommand) name value =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Text)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value
        parameter |> ignore

    let insertHeartbeat
        (dataSource: NpgsqlDataSource)
        (id: Guid)
        (status: string)
        (heartbeatAtUtc: DateTimeOffset)
        (failureReason: string option)
        (metadataJson: string)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, RepositoryError>> =
        taskResult {
            do! validStatuses.Contains(status) |> Result.requireTrue (InvalidStreamNodeStatus status)

            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(insertSql, connection)
                command.Parameters.AddWithValue("Id", id) |> ignore
                command.Parameters.AddWithValue("Status", status) |> ignore
                command.Parameters.AddWithValue("HeartbeatAtUtc", heartbeatAtUtc) |> ignore
                addNullableText command "FailureReason" failureReason
                command.Parameters.AddWithValue("Metadata", metadataJson) |> ignore
                let! _ = command.ExecuteNonQueryAsync(cancellationToken)
                return ()
            with ex ->
                return! Error(databaseError "StreamNodeHeartbeatRepository.insertHeartbeat" ex)
        }
