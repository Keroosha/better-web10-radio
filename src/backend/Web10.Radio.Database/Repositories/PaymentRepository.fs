namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open Web10.Radio.Database

type PaymentPurpose =
    | Request
    | Say
    | Donation

[<RequireQualifiedAccess>]
type CommandOutcome<'T> =
    | Created of 'T
    | AlreadyApplied of 'T
    | Rejected of reason: string

type PaymentOrderToCreate =
    { Id: Guid
      TelegramUserId: int64
      Purpose: PaymentPurpose
      PurposeEntityId: Guid option
      AmountStars: int
      InvoicePayload: string
      CreatedAtUtc: DateTimeOffset }

[<RequireQualifiedAccess>]
type PreCheckoutDecision =
    | Approved
    | Rejected of reason: string

type CompletedPayment =
    { PaymentId: Guid
      Purpose: PaymentPurpose
      PurposeEntityId: Guid option
      AlreadyApplied: bool }

type CompletePaymentOutcome =
    | Completed of CompletedPayment
    | Rejected of reason: string

module PaymentRepository =
    let private purposeText = function | Request -> "Request" | Say -> "Say" | Donation -> "Donation"
    let private tryPurpose = function | "Request" -> Some Request | "Say" -> Some Say | "Donation" -> Some Donation | _ -> None
    let private error operation (ex: exn) = DatabaseError(operation, ex.Message)

    let private optionalGuid (command: NpgsqlCommand) name value =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Uuid)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value

    let private readOrderInTransaction (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (paymentId: Guid) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand("""SELECT "TelegramUserId", "Purpose", "PurposeEntityId", "AmountStars", "TelegramInvoicePayload", "Status", "TelegramPaymentChargeId"
FROM "Payments" WHERE "Id" = @Id AND "IsDeleted" = false FOR UPDATE;""", connection, transaction)
            command.Parameters.AddWithValue("Id", paymentId) |> ignore
            use! reader = command.ExecuteReaderAsync(cancellationToken)
            let! found = reader.ReadAsync(cancellationToken)
            if not found then return None else
                let entityId = if reader.IsDBNull 2 then None else Some(reader.GetGuid 2)
                let charge = if reader.IsDBNull 6 then None else Some(reader.GetString 6)
                return Some(reader.GetInt64 0, reader.GetString 1, entityId, reader.GetInt32 3, reader.GetString 4, reader.GetString 5, charge)
        }

    let tryGetOrderInTransaction
        (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (paymentId: Guid) (cancellationToken: CancellationToken)
        : Task<Result<PaymentOrderToCreate option, RepositoryError>> =
        task {
            try
                let! row = readOrderInTransaction connection transaction paymentId cancellationToken
                return
                    Ok(row |> Option.bind (fun (userId, purpose, entityId, amount, payload, _, _) ->
                        tryPurpose purpose |> Option.map (fun parsed ->
                            { Id = paymentId; TelegramUserId = userId; Purpose = parsed; PurposeEntityId = entityId; AmountStars = amount; InvoicePayload = payload; CreatedAtUtc = DateTimeOffset.MinValue })))
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(error "PaymentRepository.tryGetOrderInTransaction" ex)
        }

    let createOrderInTransaction
        (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (order: PaymentOrderToCreate) (cancellationToken: CancellationToken)
        : Task<Result<CommandOutcome<PaymentOrderToCreate>, RepositoryError>> =
        task {
            try
                if order.AmountStars <= 0 || String.IsNullOrWhiteSpace order.InvoicePayload then
                    return Ok(CommandOutcome.Rejected "payment order is invalid")
                else
                    use insert = new NpgsqlCommand("""INSERT INTO "Payments" ("Id", "TelegramUserId", "Purpose", "PurposeEntityId", "AmountStars", "Currency", "ProviderToken", "TelegramInvoicePayload", "Status", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @TelegramUserId, @Purpose, @PurposeEntityId, @AmountStars, 'XTR', '', @InvoicePayload, 'InvoiceCreated', false, @CreatedAtUtc, @CreatedAtUtc)
ON CONFLICT ("Id") DO NOTHING;""", connection, transaction)
                    insert.Parameters.AddWithValue("Id", order.Id) |> ignore
                    insert.Parameters.AddWithValue("TelegramUserId", order.TelegramUserId) |> ignore
                    insert.Parameters.AddWithValue("Purpose", purposeText order.Purpose) |> ignore
                    optionalGuid insert "PurposeEntityId" order.PurposeEntityId
                    insert.Parameters.AddWithValue("AmountStars", order.AmountStars) |> ignore
                    insert.Parameters.AddWithValue("InvoicePayload", order.InvoicePayload) |> ignore
                    insert.Parameters.AddWithValue("CreatedAtUtc", order.CreatedAtUtc) |> ignore
                    let! inserted = insert.ExecuteNonQueryAsync(cancellationToken)
                    if inserted = 1 then return Ok(CommandOutcome.Created order) else
                        let! existing = readOrderInTransaction connection transaction order.Id cancellationToken
                        match existing with
                        | Some(userId, purpose, entityId, amount, payload, _, _) when userId = order.TelegramUserId && purpose = purposeText order.Purpose && entityId = order.PurposeEntityId && amount = order.AmountStars && payload = order.InvoicePayload -> return Ok(CommandOutcome.AlreadyApplied order)
                        | _ -> return Ok(CommandOutcome.Rejected "payment order does not match existing order")
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(error "PaymentRepository.createOrderInTransaction" ex)
        }

    let private purposeLive (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (purpose: PaymentPurpose) (entityId: Guid) (cancellationToken: CancellationToken) =
        task {
            let sql = match purpose with | Request -> "SELECT \"Status\", \"MatchedTrackId\" FROM \"TrackRequests\" WHERE \"Id\" = @Id AND \"IsDeleted\" = false FOR UPDATE;" | Say -> "SELECT \"Status\", NULL::uuid FROM \"SayMessages\" WHERE \"Id\" = @Id AND \"IsDeleted\" = false FOR UPDATE;" | Donation -> "SELECT 'Donation', NULL::uuid;"
            use command = new NpgsqlCommand(sql, connection, transaction)
            command.Parameters.AddWithValue("Id", entityId) |> ignore
            use! reader = command.ExecuteReaderAsync(cancellationToken)
            let! found = reader.ReadAsync(cancellationToken)
            return if found then Some(reader.GetString 0, if reader.IsDBNull 1 then None else Some(reader.GetGuid 1)) else None
        }

    let validatePreCheckoutInTransaction
        (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (telegramUserId: int64) (currency: string) (amountStars: int) (invoicePayload: string) (cancellationToken: CancellationToken)
        : Task<Result<PreCheckoutDecision, RepositoryError>> =
        task {
            try
                use command = new NpgsqlCommand("""SELECT "Id", "TelegramUserId", "Purpose", "PurposeEntityId", "AmountStars", "Currency", "Status"
FROM "Payments" WHERE "TelegramInvoicePayload" = @Payload AND "IsDeleted" = false FOR UPDATE;""", connection, transaction)
                command.Parameters.AddWithValue("Payload", invoicePayload) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)
                if not found then return Ok(PreCheckoutDecision.Rejected "payment order was not found") else
                    let id, user, purposeTextValue, entityId, amount, storedCurrency, status =
                        reader.GetGuid 0, reader.GetInt64 1, reader.GetString 2, (if reader.IsDBNull 3 then None else Some(reader.GetGuid 3)), reader.GetInt32 4, reader.GetString 5, reader.GetString 6
                    reader.Close()
                    match tryPurpose purposeTextValue, entityId with
                    | Some purpose, Some entity when user = telegramUserId && currency = "XTR" && storedCurrency = "XTR" && amount = amountStars && (status = "InvoiceCreated" || status = "PreCheckoutApproved") ->
                        let! live = purposeLive connection transaction purpose entity cancellationToken
                        let expected = match purpose with | Request -> "PaidPending" | Say -> "PendingPayment" | Donation -> "Donation"
                        match live with
                        | Some(liveStatus, _) when liveStatus = expected ->
                            if status = "InvoiceCreated" then
                                use update = new NpgsqlCommand("UPDATE \"Payments\" SET \"Status\" = 'PreCheckoutApproved', \"UpdatedAtUtc\" = now() WHERE \"Id\" = @Id;", connection, transaction)
                                update.Parameters.AddWithValue("Id", id) |> ignore
                                let! _ = update.ExecuteNonQueryAsync(cancellationToken)
                                return Ok PreCheckoutDecision.Approved
                            else return Ok PreCheckoutDecision.Approved
                        | _ -> return Ok(PreCheckoutDecision.Rejected "payment purpose is not eligible")
                    | _ -> return Ok(PreCheckoutDecision.Rejected "payment does not match expected order")
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(error "PaymentRepository.validatePreCheckoutInTransaction" ex)
        }

    let private completeInTransaction connection transaction paymentId telegramUserId chargeId amountStars currency paidAtUtc cancellationToken =
        task {
            let! row = readOrderInTransaction connection transaction paymentId cancellationToken
            match row with
            | Some(user, purposeTextValue, entityId, amount, _, status, existingCharge) when user = telegramUserId && amount = amountStars && currency = "XTR" ->
                match tryPurpose purposeTextValue with
                | None -> return Ok(Rejected "payment purpose is invalid")
                | Some purpose ->
                    let applied = status = "Paid" && existingCharge = Some chargeId
                    if status = "Paid" && not applied then return Ok(Rejected "payment does not match expected order")
                    elif status <> "Paid" && status <> "InvoiceCreated" && status <> "PreCheckoutApproved" then return Ok(Rejected "payment does not match expected order")
                    else
                        match purpose, entityId with
                        | Donation, _ ->
                            if applied then
                                return Ok(Completed { PaymentId = paymentId; Purpose = purpose; PurposeEntityId = entityId; AlreadyApplied = true })
                            else
                                use update = new NpgsqlCommand("UPDATE \"Payments\" SET \"Status\" = 'Paid', \"TelegramPaymentChargeId\" = @Charge, \"PaidAtUtc\" = @At, \"UpdatedAtUtc\" = @At WHERE \"Id\" = @Id;", connection, transaction)
                                update.Parameters.AddWithValue("Id", paymentId) |> ignore
                                update.Parameters.AddWithValue("Charge", chargeId) |> ignore
                                update.Parameters.AddWithValue("At", paidAtUtc) |> ignore
                                let! _ = update.ExecuteNonQueryAsync(cancellationToken)
                                return Ok(Completed { PaymentId = paymentId; Purpose = purpose; PurposeEntityId = entityId; AlreadyApplied = false })
                        | (Request | Say), None -> return Ok(Rejected "payment purpose entity is missing")
                        | Say, Some sayId ->
                            let! live = purposeLive connection transaction Say sayId cancellationToken
                            let valid = match live with | Some(status, _) when (not applied && status = "PendingPayment") || (applied && (status = "PaidPendingModeration" || status = "Approved" || status = "Rejected")) -> true | _ -> false
                            if not valid then return Ok(Rejected "payment purpose is not eligible")
                            elif applied then return Ok(Completed { PaymentId = paymentId; Purpose = Say; PurposeEntityId = Some sayId; AlreadyApplied = true })
                            else
                                use updateSay = new NpgsqlCommand("UPDATE \"SayMessages\" SET \"Status\" = 'PaidPendingModeration', \"PaidAtUtc\" = @At, \"UpdatedAtUtc\" = @At WHERE \"Id\" = @Id AND \"IsDeleted\" = false AND \"Status\" = 'PendingPayment' AND \"AmountStars\" = @Amount;", connection, transaction)
                                updateSay.Parameters.AddWithValue("Id", sayId) |> ignore
                                updateSay.Parameters.AddWithValue("At", paidAtUtc) |> ignore
                                updateSay.Parameters.AddWithValue("Amount", amountStars) |> ignore
                                let! changed = updateSay.ExecuteNonQueryAsync(cancellationToken)
                                if changed = 1 then
                                    use updatePayment = new NpgsqlCommand("UPDATE \"Payments\" SET \"Status\" = 'Paid', \"TelegramPaymentChargeId\" = @Charge, \"PaidAtUtc\" = @At, \"UpdatedAtUtc\" = @At WHERE \"Id\" = @Id;", connection, transaction)
                                    updatePayment.Parameters.AddWithValue("Id", paymentId) |> ignore
                                    updatePayment.Parameters.AddWithValue("Charge", chargeId) |> ignore
                                    updatePayment.Parameters.AddWithValue("At", paidAtUtc) |> ignore
                                    let! _ = updatePayment.ExecuteNonQueryAsync(cancellationToken)
                                    ()
                                return Ok(if changed = 1 then Completed { PaymentId = paymentId; Purpose = Say; PurposeEntityId = Some sayId; AlreadyApplied = false } else Rejected "payment purpose is not eligible")
                        | Request, Some requestId ->
                            let! live = purposeLive connection transaction Request requestId cancellationToken
                            let valid, trackId = match live with | Some(status, Some track) when (not applied && status = "PaidPending") || (applied && status = "Paid") -> true, Some track | _ -> false, None
                            if not valid then return Ok(Rejected "payment purpose is not eligible")
                            elif applied then
                                use verify = new NpgsqlCommand("SELECT 1 FROM \"PlaybackQueue\" WHERE \"Id\" = @Id AND \"TrackRequestId\" = @RequestId AND \"IsDeleted\" = false;", connection, transaction)
                                verify.Parameters.AddWithValue("Id", paymentId) |> ignore
                                verify.Parameters.AddWithValue("RequestId", requestId) |> ignore
                                let! exists = verify.ExecuteScalarAsync(cancellationToken)
                                return Ok(if isNull exists then Rejected "payment queue is missing" else Completed { PaymentId = paymentId; Purpose = Request; PurposeEntityId = Some requestId; AlreadyApplied = true })
                            else
                                use updateRequest = new NpgsqlCommand("UPDATE \"TrackRequests\" SET \"Status\" = 'Paid', \"UpdatedAtUtc\" = @At WHERE \"Id\" = @Id AND \"IsDeleted\" = false AND \"Status\" = 'PaidPending' AND \"MatchedTrackId\" IS NOT NULL;", connection, transaction)
                                updateRequest.Parameters.AddWithValue("Id", requestId) |> ignore
                                updateRequest.Parameters.AddWithValue("At", paidAtUtc) |> ignore
                                let! changed = updateRequest.ExecuteNonQueryAsync(cancellationToken)
                                if changed = 1 then
                                    use queue = new NpgsqlCommand("""INSERT INTO "PlaybackQueue" ("Id", "TrackId", "TrackRequestId", "PlaylistItemId", "Source", "Status", "Priority", "RequestedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @TrackId, @RequestId, NULL, 'request', 'Queued', 0, @At, false, @At, @At) ON CONFLICT ("Id") DO NOTHING;""", connection, transaction)
                                    queue.Parameters.AddWithValue("Id", paymentId) |> ignore
                                    queue.Parameters.AddWithValue("TrackId", trackId.Value) |> ignore
                                    queue.Parameters.AddWithValue("RequestId", requestId) |> ignore
                                    queue.Parameters.AddWithValue("At", paidAtUtc) |> ignore
                                    let! _ = queue.ExecuteNonQueryAsync(cancellationToken)
                                    use updatePayment = new NpgsqlCommand("UPDATE \"Payments\" SET \"Status\" = 'Paid', \"TelegramPaymentChargeId\" = @Charge, \"PaidAtUtc\" = @At, \"UpdatedAtUtc\" = @At WHERE \"Id\" = @Id;", connection, transaction)
                                    updatePayment.Parameters.AddWithValue("Id", paymentId) |> ignore
                                    updatePayment.Parameters.AddWithValue("Charge", chargeId) |> ignore
                                    updatePayment.Parameters.AddWithValue("At", paidAtUtc) |> ignore
                                    let! _ = updatePayment.ExecuteNonQueryAsync(cancellationToken)
                                    ()
                                return Ok(if changed = 1 then Completed { PaymentId = paymentId; Purpose = Request; PurposeEntityId = Some requestId; AlreadyApplied = false } else Rejected "payment purpose is not eligible")
            | _ -> return Ok(Rejected "payment does not match expected order")
        }

    let completePayment (dataSource: NpgsqlDataSource) (paymentId: Guid) (telegramUserId: int64) (telegramPaymentChargeId: string) (amountStars: int) (currency: string) (paidAtUtc: DateTimeOffset) (cancellationToken: CancellationToken) : Task<Result<CompletePaymentOutcome, RepositoryError>> =
        task {
            try
                return! DatabaseSession.withTransactionResult dataSource (fun connection transaction token -> completeInTransaction connection transaction paymentId telegramUserId telegramPaymentChargeId amountStars currency paidAtUtc token) cancellationToken
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | :? PostgresException as ex when ex.SqlState = "23505" && ex.ConstraintName = "UX_Payments_Active_TelegramPaymentChargeId" -> return Ok(Rejected "telegram charge id is already assigned")
            | ex -> return Error(error "PaymentRepository.completePayment" ex)
        }
