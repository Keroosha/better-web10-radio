namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
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

type TrackRequestSnapshot =
    { Id: Guid
      TelegramUserId: int64 option
      Status: string
      MatchedTrackId: Guid option }

[<RequireQualifiedAccess>]
module TelegramCommandRepository =
    let private error operation (ex: exn) = DatabaseError(operation, ex.Message)
    let private addOption (command: NpgsqlCommand) name dbType value =
        let parameter = command.Parameters.Add(name, dbType)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value

    let private readRequest (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (requestId: Guid) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand("SELECT \"TelegramUserId\", \"MatchedTrackId\", \"Status\" FROM \"TrackRequests\" WHERE \"Id\" = @Id AND \"IsDeleted\" = false FOR UPDATE;", connection, transaction)
            command.Parameters.AddWithValue("Id", requestId) |> ignore
            use! reader = command.ExecuteReaderAsync(cancellationToken)
            let! found = reader.ReadAsync(cancellationToken)
            return if found then Some((if reader.IsDBNull 0 then None else Some(reader.GetInt64 0)), (if reader.IsDBNull 1 then None else Some(reader.GetGuid 1)), reader.GetString 2) else None
        }

    let private readSay (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (sayId: Guid) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand("SELECT \"TelegramUserId\", \"Status\", \"AmountStars\" FROM \"SayMessages\" WHERE \"Id\" = @Id AND \"IsDeleted\" = false FOR UPDATE;", connection, transaction)
            command.Parameters.AddWithValue("Id", sayId) |> ignore
            use! reader = command.ExecuteReaderAsync(cancellationToken)
            let! found = reader.ReadAsync(cancellationToken)
            return if found then Some((if reader.IsDBNull 0 then None else Some(reader.GetInt64 0)), reader.GetString 1, reader.GetInt32 2) else None
        }

    let private activeOrderForPurpose (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (purpose: string) (entityId: Guid) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand("SELECT \"Id\", \"TelegramUserId\", \"AmountStars\", \"TelegramInvoicePayload\" FROM \"Payments\" WHERE \"Purpose\" = @Purpose AND \"PurposeEntityId\" = @EntityId AND \"IsDeleted\" = false FOR UPDATE;", connection, transaction)
            command.Parameters.AddWithValue("Purpose", purpose) |> ignore
            command.Parameters.AddWithValue("EntityId", entityId) |> ignore
            use! reader = command.ExecuteReaderAsync(cancellationToken)
            let! found = reader.ReadAsync(cancellationToken)
            return if found then Some(reader.GetGuid 0, reader.GetInt64 1, reader.GetInt32 2, reader.GetString 3) else None
        }

    let private orderFromExisting purpose entityId (id, userId, amount, payload) =
        { Id = id; TelegramUserId = userId; Purpose = purpose; PurposeEntityId = Some entityId; AmountStars = amount; InvoicePayload = payload; CreatedAtUtc = DateTimeOffset.MinValue }

    let tryGetActiveOrderForPurposeInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (purpose: PaymentPurpose)
        (entityId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<PaymentOrderToCreate option, RepositoryError>> =
        task {
            try
                let! row = activeOrderForPurpose connection transaction (match purpose with | Request -> "Request" | Say -> "Say" | Donation -> "Donation") entityId cancellationToken
                return Ok(row |> Option.map (orderFromExisting purpose entityId))
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(error "TelegramCommandRepository.tryGetActiveOrderForPurposeInTransaction" ex)
        }

    let tryGetActiveRequest
        (dataSource: NpgsqlDataSource)
        (requestId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<TrackRequestSnapshot option, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("SELECT r.\"TelegramUserId\", t.\"Id\", r.\"Status\" FROM \"TrackRequests\" AS r LEFT JOIN \"Tracks\" AS t ON t.\"Id\" = r.\"MatchedTrackId\" AND t.\"IsDeleted\" = false WHERE r.\"Id\" = @Id AND r.\"IsDeleted\" = false;", connection)
                command.Parameters.AddWithValue("Id", requestId) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)
                return
                    Ok(
                        if found then
                            Some
                                { Id = requestId
                                  TelegramUserId = if reader.IsDBNull(0) then None else Some(reader.GetInt64(0))
                                  MatchedTrackId = if reader.IsDBNull(1) then None else Some(reader.GetGuid(1))
                                  Status = reader.GetString(2) }
                        else
                            None
                    )
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(error "TelegramCommandRepository.tryGetActiveRequest" ex)
        }

    let createNeedsReviewTrackRequestInTransaction
        (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (request: TrackRequestToCreate) (cancellationToken: CancellationToken)
        : Task<Result<CommandOutcome<TrackRequestToCreate>, RepositoryError>> =
        task {
            try
                use insert = new NpgsqlCommand("""INSERT INTO "TrackRequests" ("Id", "TelegramUserId", "DisplayName", "Query", "MatchedTrackId", "Status", "RequestedAtUtc", "CorrelationId", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @UserId, @DisplayName, @Query, NULL, 'NeedsReview', @At, @CorrelationId, false, @At, @At) ON CONFLICT ("Id") DO NOTHING;""", connection, transaction)
                insert.Parameters.AddWithValue("Id", request.Id) |> ignore
                addOption insert "UserId" NpgsqlDbType.Bigint request.TelegramUserId
                addOption insert "DisplayName" NpgsqlDbType.Text request.DisplayName
                insert.Parameters.AddWithValue("Query", request.Query) |> ignore; insert.Parameters.AddWithValue("At", request.RequestedAtUtc) |> ignore; insert.Parameters.AddWithValue("CorrelationId", request.CorrelationId) |> ignore
                let! written = insert.ExecuteNonQueryAsync(cancellationToken)
                if written = 1 then return Ok(CommandOutcome.Created request) else
                    let! existing = readRequest connection transaction request.Id cancellationToken
                    match existing with
                    | Some(_, _, "NeedsReview") -> return Ok(CommandOutcome.AlreadyApplied request)
                    | _ -> return Ok(CommandOutcome.Rejected "track request already has a different state")
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(error "TelegramCommandRepository.createNeedsReviewTrackRequestInTransaction" ex)
        }

    let createMatchedTrackRequestInTransaction
        (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (request: TrackRequestToCreate) (trackId: Guid) (cancellationToken: CancellationToken)
        : Task<Result<CommandOutcome<TrackRequestToCreate>, RepositoryError>> =
        task {
            try
                use insert = new NpgsqlCommand("""INSERT INTO "TrackRequests" ("Id", "TelegramUserId", "DisplayName", "Query", "MatchedTrackId", "Status", "RequestedAtUtc", "CorrelationId", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @UserId, @DisplayName, @Query, @TrackId, 'Matched', @At, @CorrelationId, false, @At, @At)
ON CONFLICT ("Id") DO NOTHING;""", connection, transaction)
                insert.Parameters.AddWithValue("Id", request.Id) |> ignore; addOption insert "UserId" NpgsqlDbType.Bigint request.TelegramUserId; addOption insert "DisplayName" NpgsqlDbType.Text request.DisplayName
                insert.Parameters.AddWithValue("Query", request.Query) |> ignore; insert.Parameters.AddWithValue("TrackId", trackId) |> ignore; insert.Parameters.AddWithValue("At", request.RequestedAtUtc) |> ignore; insert.Parameters.AddWithValue("CorrelationId", request.CorrelationId) |> ignore
                let! written = insert.ExecuteNonQueryAsync(cancellationToken)
                if written = 1 then return Ok(CommandOutcome.Created request) else
                    let! existing = readRequest connection transaction request.Id cancellationToken
                    match existing with
                    | Some(_, Some matched, "Matched") when matched = trackId -> return Ok(CommandOutcome.AlreadyApplied request)
                    | _ -> return Ok(CommandOutcome.Rejected "track request already has a different match")
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(error "TelegramCommandRepository.createMatchedTrackRequestInTransaction" ex)
        }

    let selectTrackInTransaction
        (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (requestId: Guid) (telegramUserId: int64) (trackId: Guid) (updatedAtUtc: DateTimeOffset) (cancellationToken: CancellationToken)
        : Task<Result<CommandOutcome<Guid>, RepositoryError>> =
        task {
            try
                let! existing = readRequest connection transaction requestId cancellationToken
                match existing with
                | Some(Some owner, _, "NeedsReview") when owner = telegramUserId ->
                    use update = new NpgsqlCommand("UPDATE \"TrackRequests\" SET \"MatchedTrackId\" = @TrackId, \"Status\" = 'Matched', \"UpdatedAtUtc\" = @At WHERE \"Id\" = @Id AND \"Status\" = 'NeedsReview' AND \"IsDeleted\" = false;", connection, transaction)
                    update.Parameters.AddWithValue("Id", requestId) |> ignore; update.Parameters.AddWithValue("TrackId", trackId) |> ignore; update.Parameters.AddWithValue("At", updatedAtUtc) |> ignore
                    let! changed = update.ExecuteNonQueryAsync(cancellationToken)
                    return if changed = 1 then Ok(CommandOutcome.Created trackId) else Ok(CommandOutcome.Rejected "track request state changed")
                | Some(Some owner, Some matched, "Matched") when owner = telegramUserId && matched = trackId -> return Ok(CommandOutcome.AlreadyApplied trackId)
                | _ -> return Ok(CommandOutcome.Rejected "track selection is unavailable")
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(error "TelegramCommandRepository.selectTrackInTransaction" ex)
        }

    let cancelTrackRequestInTransaction
        (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (requestId: Guid) (telegramUserId: int64) (updatedAtUtc: DateTimeOffset) (cancellationToken: CancellationToken)
        : Task<Result<CommandOutcome<unit>, RepositoryError>> =
        task {
            try
                let! existing = readRequest connection transaction requestId cancellationToken
                match existing with
                | Some(Some owner, _, status) when owner = telegramUserId && (status = "NeedsReview" || status = "Matched") ->
                    let! payment = activeOrderForPurpose connection transaction "Request" requestId cancellationToken
                    if payment.IsSome then return Ok(CommandOutcome.Rejected "track request already has a payment") else
                        use update = new NpgsqlCommand("UPDATE \"TrackRequests\" SET \"Status\" = 'Rejected', \"UpdatedAtUtc\" = @At WHERE \"Id\" = @Id AND \"IsDeleted\" = false AND \"Status\" IN ('NeedsReview', 'Matched');", connection, transaction)
                        update.Parameters.AddWithValue("Id", requestId) |> ignore; update.Parameters.AddWithValue("At", updatedAtUtc) |> ignore
                        let! changed = update.ExecuteNonQueryAsync(cancellationToken)
                        return if changed = 1 then Ok(CommandOutcome.Created ()) else Ok(CommandOutcome.Rejected "track request state changed")
                | Some(Some owner, _, "Rejected") when owner = telegramUserId -> return Ok(CommandOutcome.AlreadyApplied ())
                | _ -> return Ok(CommandOutcome.Rejected "track cancellation is unavailable")
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(error "TelegramCommandRepository.cancelTrackRequestInTransaction" ex)
        }

    let createSayMessageInTransaction
        (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (message: SayMessageToCreate) (amountStars: int) (cancellationToken: CancellationToken)
        : Task<Result<CommandOutcome<SayMessageToCreate>, RepositoryError>> =
        task {
            try
                if amountStars <= 0 then return Ok(CommandOutcome.Rejected "say message amount must be positive") else
                    use insert = new NpgsqlCommand("""INSERT INTO "SayMessages" ("Id", "TelegramUserId", "DisplayName", "Text", "AmountStars", "Color", "Status", "SubmittedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @UserId, @DisplayName, @Text, @Amount, NULL, 'PendingPayment', @At, false, @At, @At) ON CONFLICT ("Id") DO NOTHING;""", connection, transaction)
                    insert.Parameters.AddWithValue("Id", message.Id) |> ignore; addOption insert "UserId" NpgsqlDbType.Bigint message.TelegramUserId; insert.Parameters.AddWithValue("DisplayName", message.DisplayName) |> ignore; insert.Parameters.AddWithValue("Text", message.Text) |> ignore; insert.Parameters.AddWithValue("Amount", amountStars) |> ignore; insert.Parameters.AddWithValue("At", message.SubmittedAtUtc) |> ignore
                    let! written = insert.ExecuteNonQueryAsync(cancellationToken)
                    if written = 1 then return Ok(CommandOutcome.Created message) else
                        let! existing = readSay connection transaction message.Id cancellationToken
                        match existing with | Some(_, "PendingPayment", amount) when amount = amountStars -> return Ok(CommandOutcome.AlreadyApplied message) | _ -> return Ok(CommandOutcome.Rejected "say message already has a different state")
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(error "TelegramCommandRepository.createSayMessageInTransaction" ex)
        }

    let createRequestPaymentInTransaction
        (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (requestId: Guid) (telegramUserId: int64) (order: PaymentOrderToCreate) (cancellationToken: CancellationToken)
        : Task<Result<CommandOutcome<PaymentOrderToCreate>, RepositoryError>> =
        task {
            try
                let! request = readRequest connection transaction requestId cancellationToken
                match request with
                | Some(Some owner, Some _, "Matched") when owner = telegramUserId && order.Purpose = Request && order.PurposeEntityId = Some requestId && order.TelegramUserId = telegramUserId ->
                    use transition = new NpgsqlCommand("UPDATE \"TrackRequests\" SET \"Status\" = 'PaidPending', \"UpdatedAtUtc\" = @At WHERE \"Id\" = @Id AND \"Status\" = 'Matched' AND \"IsDeleted\" = false;", connection, transaction)
                    transition.Parameters.AddWithValue("Id", requestId) |> ignore; transition.Parameters.AddWithValue("At", order.CreatedAtUtc) |> ignore
                    let! changed = transition.ExecuteNonQueryAsync(cancellationToken)
                    if changed <> 1 then return Ok(CommandOutcome.Rejected "track request state changed") else return! PaymentRepository.createOrderInTransaction connection transaction order cancellationToken
                | Some(Some owner, Some _, "PaidPending") when owner = telegramUserId ->
                    let! existing = activeOrderForPurpose connection transaction "Request" requestId cancellationToken
                    match existing with | Some payment -> return Ok(CommandOutcome.AlreadyApplied(orderFromExisting Request requestId payment)) | None -> return Ok(CommandOutcome.Rejected "payment order is missing")
                | _ -> return Ok(CommandOutcome.Rejected "track request is unavailable")
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(error "TelegramCommandRepository.createRequestPaymentInTransaction" ex)
        }

    let createSayWithPaymentInTransaction
        (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (message: SayMessageToCreate) (amountStars: int) (order: PaymentOrderToCreate) (cancellationToken: CancellationToken)
        : Task<Result<CommandOutcome<PaymentOrderToCreate>, RepositoryError>> =
        task {
            try
                let! say = createSayMessageInTransaction connection transaction message amountStars cancellationToken
                match say with
                | Error repositoryError -> return Error repositoryError
                | Ok(CommandOutcome.Rejected reason) -> return Ok(CommandOutcome.Rejected reason)
                | Ok(CommandOutcome.Created _) ->
                    if order.Purpose <> Say || order.PurposeEntityId <> Some message.Id || order.AmountStars <> amountStars then return Ok(CommandOutcome.Rejected "payment order does not match say message")
                    else return! PaymentRepository.createOrderInTransaction connection transaction order cancellationToken
                | Ok(CommandOutcome.AlreadyApplied _) ->
                    let! existing = activeOrderForPurpose connection transaction "Say" message.Id cancellationToken
                    match existing with | Some payment -> return Ok(CommandOutcome.AlreadyApplied(orderFromExisting Say message.Id payment)) | None -> return Ok(CommandOutcome.Rejected "payment order is missing")
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(error "TelegramCommandRepository.createSayWithPaymentInTransaction" ex)
        }

    let createTrackRequest (dataSource: NpgsqlDataSource) request cancellationToken : Task<Result<bool, RepositoryError>> =
        task {
            let! result = DatabaseSession.withTransactionResult dataSource (fun connection transaction token -> createNeedsReviewTrackRequestInTransaction connection transaction request token) cancellationToken
            return result |> Result.map (function | CommandOutcome.Created _ -> true | CommandOutcome.AlreadyApplied _ -> false | CommandOutcome.Rejected _ -> false)
        }

    let createSayMessage (dataSource: NpgsqlDataSource) message amountStars cancellationToken : Task<Result<bool, RepositoryError>> =
        task {
            let! result = DatabaseSession.withTransactionResult dataSource (fun connection transaction token -> createSayMessageInTransaction connection transaction message amountStars token) cancellationToken
            return result |> Result.map (function | CommandOutcome.Created _ -> true | CommandOutcome.AlreadyApplied _ -> false | CommandOutcome.Rejected _ -> false)
        }
