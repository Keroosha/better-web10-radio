namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Web10.Radio.Database

type StreamNodeDesiredState =
    | Running
    | Stopped

type StreamNodeControlState =
    { Id: Guid
      DesiredState: StreamNodeDesiredState
      RestartGeneration: int
      CreatedAtUtc: DateTimeOffset
      UpdatedAtUtc: DateTimeOffset }

module StreamNodeControlRepository =
    let private databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)

    let private stateToDatabase = function
        | Running -> "Running"
        | Stopped -> "Stopped"

    let private stateFromDatabase = function
        | "Running" -> Ok Running
        | "Stopped" -> Ok Stopped
        | value -> Error(DatabaseError("StreamNodeControlRepository.read", sprintf "Unexpected desired state '%s'." value))

    let private readControlState (reader: NpgsqlDataReader) =
        match stateFromDatabase (reader.GetString(1)) with
        | Ok desiredState ->
            Ok
                { Id = reader.GetGuid(0)
                  DesiredState = desiredState
                  RestartGeneration = reader.GetInt32(2)
                  CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(3)
                  UpdatedAtUtc = reader.GetFieldValue<DateTimeOffset>(4) }
        | Error error -> Error error

    let private selectLocked (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (token: CancellationToken) : Task<Result<StreamNodeControlState option, RepositoryError>> =
        task {
            use command = new NpgsqlCommand("""SELECT "Id", "DesiredState", "RestartGeneration", "CreatedAtUtc", "UpdatedAtUtc"
FROM "StreamNodeControlState"
WHERE "SingletonKey" = 'primary' AND "IsDeleted" = false
FOR UPDATE;""", connection, transaction)
            use! reader = command.ExecuteReaderAsync(token)
            let! found = reader.ReadAsync(token)
            return if found then readControlState reader |> Result.map Some else Ok None
        }

    let getOrCreate
        (dataSource: NpgsqlDataSource)
        (candidateId: Guid)
        (atUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<StreamNodeControlState, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        use insert = new NpgsqlCommand("""INSERT INTO "StreamNodeControlState" ("Id", "SingletonKey", "DesiredState", "RestartGeneration", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, 'primary', 'Running', 0, false, @AtUtc, @AtUtc)
ON CONFLICT ("SingletonKey") WHERE "IsDeleted" = false AND "SingletonKey" = 'primary' DO NOTHING;""", connection, transaction)
                        insert.Parameters.AddWithValue("Id", candidateId) |> ignore
                        insert.Parameters.AddWithValue("AtUtc", atUtc) |> ignore
                        let! _ = insert.ExecuteNonQueryAsync(token)
                        let! state = selectLocked connection transaction token
                        return
                            match state with
                            | Ok(Some value) -> Ok value
                            | Ok None -> Error(DatabaseError("StreamNodeControlRepository.getOrCreate", "The singleton row was not available after insertion."))
                            | Error error -> Error error
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | ex -> return Error(databaseError "StreamNodeControlRepository.getOrCreate" ex)
                })
            cancellationToken

    let private updateLocked (desiredState: StreamNodeDesiredState) (incrementRestartGeneration: bool) (atUtc: DateTimeOffset) (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (token: CancellationToken) : Task<Result<StreamNodeControlState, RepositoryError>> =
        task {
            let! locked = selectLocked connection transaction token
            match locked with
            | Error error -> return Error error
            | Ok None -> return Error(DatabaseError("StreamNodeControlRepository.update", "The singleton control row has not been created."))
            | Ok(Some _) ->
                use command = new NpgsqlCommand("""UPDATE "StreamNodeControlState"
SET "DesiredState" = @DesiredState,
    "RestartGeneration" = CASE WHEN @IncrementRestartGeneration THEN "RestartGeneration" + 1 ELSE "RestartGeneration" END,
    "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "SingletonKey" = 'primary' AND "IsDeleted" = false
RETURNING "Id", "DesiredState", "RestartGeneration", "CreatedAtUtc", "UpdatedAtUtc";""", connection, transaction)
                command.Parameters.AddWithValue("DesiredState", stateToDatabase desiredState) |> ignore
                command.Parameters.AddWithValue("IncrementRestartGeneration", incrementRestartGeneration) |> ignore
                command.Parameters.AddWithValue("UpdatedAtUtc", atUtc) |> ignore
                use! reader = command.ExecuteReaderAsync(token)
                let! found = reader.ReadAsync(token)
                return
                    if found then readControlState reader
                    else Error(DatabaseError("StreamNodeControlRepository.update", "The locked singleton row disappeared."))
        }

    let setDesiredState
        (dataSource: NpgsqlDataSource)
        (desiredState: StreamNodeDesiredState)
        (atUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<StreamNodeControlState, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        return! updateLocked desiredState false atUtc connection transaction token
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | ex -> return Error(databaseError "StreamNodeControlRepository.setDesiredState" ex)
                })
            cancellationToken

    let restart
        (dataSource: NpgsqlDataSource)
        (atUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<StreamNodeControlState, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        return! updateLocked Running true atUtc connection transaction token
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | ex -> return Error(databaseError "StreamNodeControlRepository.restart" ex)
                })
            cancellationToken
