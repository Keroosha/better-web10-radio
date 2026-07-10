namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Npgsql
open NpgsqlTypes
open Web10.Radio.Database

type TrackRequestToCreate =
    { Id: Guid
      TelegramUserId: int64 option
      DisplayName: string option
      Query: string
      RequestedAtUtc: DateTimeOffset
      CorrelationId: Guid }

type SayMessageToCreate =
    { Id: Guid
      TelegramUserId: int64 option
      DisplayName: string
      Text: string
      SubmittedAtUtc: DateTimeOffset }

[<RequireQualifiedAccess>]
module TelegramCommandRepository =
    [<Literal>]
    let private insertTrackRequestSql = """INSERT INTO "TrackRequests" (
    "Id", "TelegramUserId", "DisplayName", "Query", "MatchedTrackId", "Status", "RequestedAtUtc", "CorrelationId",
    "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (
    @Id, @TelegramUserId, @DisplayName, @Query, NULL, 'NeedsReview', @RequestedAtUtc, @CorrelationId,
    false, @RequestedAtUtc, @RequestedAtUtc
)
ON CONFLICT ("Id") DO NOTHING;"""

    [<Literal>]
    let private insertSayMessageSql = """INSERT INTO "SayMessages" (
    "Id", "TelegramUserId", "DisplayName", "Text", "AmountStars", "Color", "Status", "SubmittedAtUtc",
    "PaidAtUtc", "ModeratedAtUtc", "ModerationReason", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (
    @Id, @TelegramUserId, @DisplayName, @Text, 0, NULL, 'PendingPayment', @SubmittedAtUtc,
    NULL, NULL, NULL, false, @SubmittedAtUtc, @SubmittedAtUtc
)
ON CONFLICT ("Id") DO NOTHING;"""

    let private databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)

    let private addNullableInt64 (command: NpgsqlCommand) name value =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Bigint)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value
        parameter |> ignore

    let private addNullableText (command: NpgsqlCommand) name value =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Text)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value
        parameter |> ignore

    let createTrackRequest
        (dataSource: NpgsqlDataSource)
        (request: TrackRequestToCreate)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(insertTrackRequestSql, connection)
                command.Parameters.AddWithValue("Id", request.Id) |> ignore
                addNullableInt64 command "TelegramUserId" request.TelegramUserId
                addNullableText command "DisplayName" request.DisplayName
                command.Parameters.AddWithValue("Query", request.Query) |> ignore
                command.Parameters.AddWithValue("RequestedAtUtc", request.RequestedAtUtc) |> ignore
                command.Parameters.AddWithValue("CorrelationId", request.CorrelationId) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "TelegramCommandRepository.createTrackRequest" ex)
        }

    let createSayMessage
        (dataSource: NpgsqlDataSource)
        (message: SayMessageToCreate)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(insertSayMessageSql, connection)
                command.Parameters.AddWithValue("Id", message.Id) |> ignore
                addNullableInt64 command "TelegramUserId" message.TelegramUserId
                command.Parameters.AddWithValue("DisplayName", message.DisplayName) |> ignore
                command.Parameters.AddWithValue("Text", message.Text) |> ignore
                command.Parameters.AddWithValue("SubmittedAtUtc", message.SubmittedAtUtc) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "TelegramCommandRepository.createSayMessage" ex)
        }
