namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes

[<RequireQualifiedAccess>]
type SayMessageModerationFilter =
    | Pending
    | Approved
    | Rejected

[<RequireQualifiedAccess>]
type SayMessageModerationTarget =
    | Approved
    | Rejected

[<RequireQualifiedAccess>]
type SayMessageModerationOutcome =
    | Applied
    | AlreadyApplied
    | NotFound
    | Conflict

type SayMessageForModeration =
    { Id: Guid
      TelegramUserId: int64 option
      DisplayName: string
      Text: string
      AmountStars: int
      Color: string option
      Status: SayMessageModerationFilter
      SubmittedAtUtc: DateTimeOffset
      PaidAtUtc: DateTimeOffset option
      ModeratedAtUtc: DateTimeOffset option
      ModerationReason: string option }

module SayMessageRepository =
    [<Literal>]
    let private listForModerationSql = """SELECT
    "Id",
    "TelegramUserId",
    "DisplayName",
    "Text",
    "AmountStars",
    "Color",
    "Status",
    "SubmittedAtUtc",
    "PaidAtUtc",
    "ModeratedAtUtc",
    "ModerationReason"
FROM "SayMessages"
WHERE "IsDeleted" = false
  AND "Status" = @Status
ORDER BY "SubmittedAtUtc" DESC, "CreatedAtUtc" DESC
LIMIT 100;"""

    [<Literal>]
    let private lockForModerationSql = """SELECT "Status", "ModerationReason"
FROM "SayMessages"
WHERE "Id" = @Id
  AND "IsDeleted" = false
FOR UPDATE;"""

    let private databaseError operation (ex: exn) =
        DatabaseError(operation, ex.Message)

    let private filterStatus = function
        | SayMessageModerationFilter.Pending -> "PaidPendingModeration"
        | SayMessageModerationFilter.Approved -> "Approved"
        | SayMessageModerationFilter.Rejected -> "Rejected"

    let private tryFilter = function
        | "PaidPendingModeration" -> Some SayMessageModerationFilter.Pending
        | "Approved" -> Some SayMessageModerationFilter.Approved
        | "Rejected" -> Some SayMessageModerationFilter.Rejected
        | _ -> None

    let private targetStatus = function
        | SayMessageModerationTarget.Approved -> "Approved"
        | SayMessageModerationTarget.Rejected -> "Rejected"

    let private nullableString (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull ordinal then None else Some(reader.GetString ordinal)

    let private nullableDateTimeOffset (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull ordinal then None else Some(reader.GetFieldValue<DateTimeOffset>(ordinal))

    let private normalizeReason =
        Option.map (fun (value: string) -> if isNull value then String.Empty else value.Trim())
        >> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))

    let listForModeration
        (dataSource: NpgsqlDataSource)
        (filter: SayMessageModerationFilter)
        (cancellationToken: CancellationToken)
        : Task<Result<SayMessageForModeration list, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(listForModerationSql, connection)
                command.Parameters.AddWithValue("Status", filterStatus filter) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let messages = ResizeArray<SayMessageForModeration>()
                let mutable keepReading = true

                while keepReading do
                    let! hasRow = reader.ReadAsync(cancellationToken)

                    if hasRow then
                        match tryFilter (reader.GetString 6) with
                        | Some status ->
                            messages.Add
                                { Id = reader.GetGuid 0
                                  TelegramUserId = if reader.IsDBNull 1 then None else Some(reader.GetInt64 1)
                                  DisplayName = reader.GetString 2
                                  Text = reader.GetString 3
                                  AmountStars = reader.GetInt32 4
                                  Color = nullableString reader 5
                                  Status = status
                                  SubmittedAtUtc = reader.GetFieldValue<DateTimeOffset>(7)
                                  PaidAtUtc = nullableDateTimeOffset reader 8
                                  ModeratedAtUtc = nullableDateTimeOffset reader 9
                                  ModerationReason = nullableString reader 10 }
                        | None ->
                            ()
                    else
                        keepReading <- false

                return Ok(List.ofSeq messages)
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested ->
                return raise ex
            | ex ->
                return Error(databaseError "SayMessageRepository.listForModeration" ex)
        }

    let moderateInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (messageId: Guid)
        (target: SayMessageModerationTarget)
        (reason: string option)
        (moderatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<SayMessageModerationOutcome, RepositoryError>> =
        task {
            try
                let moderationReason =
                    match target with
                    | SayMessageModerationTarget.Approved -> None
                    | SayMessageModerationTarget.Rejected -> normalizeReason reason

                use lockCommand = new NpgsqlCommand(lockForModerationSql, connection, transaction)
                lockCommand.Parameters.AddWithValue("Id", messageId) |> ignore
                use! reader = lockCommand.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)

                if not found then
                    return Ok SayMessageModerationOutcome.NotFound
                else
                    let status = reader.GetString 0
                    let persistedReason = nullableString reader 1
                    reader.Close()

                    if status = "PaidPendingModeration" then
                        use update =
                            new NpgsqlCommand(
                                """UPDATE "SayMessages"
SET "Status" = @Status,
    "ModeratedAtUtc" = @ModeratedAtUtc,
    "ModerationReason" = @ModerationReason,
    "UpdatedAtUtc" = @ModeratedAtUtc
WHERE "Id" = @Id
  AND "IsDeleted" = false
  AND "Status" = 'PaidPendingModeration';""",
                                connection,
                                transaction
                            )

                        update.Parameters.AddWithValue("Id", messageId) |> ignore
                        update.Parameters.AddWithValue("Status", targetStatus target) |> ignore
                        update.Parameters.AddWithValue("ModeratedAtUtc", moderatedAtUtc) |> ignore
                        let reasonParameter = update.Parameters.Add("ModerationReason", NpgsqlDbType.Text)
                        reasonParameter.Value <- moderationReason |> Option.map box |> Option.defaultValue DBNull.Value
                        let! changed = update.ExecuteNonQueryAsync(cancellationToken)
                        return Ok(if changed = 1 then SayMessageModerationOutcome.Applied else SayMessageModerationOutcome.Conflict)
                    elif status = targetStatus target && persistedReason = moderationReason then
                        return Ok SayMessageModerationOutcome.AlreadyApplied
                    else
                        return Ok SayMessageModerationOutcome.Conflict
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested ->
                return raise ex
            | ex ->
                return Error(databaseError "SayMessageRepository.moderateInTransaction" ex)
        }
