namespace Web10.Radio.API

open System
open System.Numerics
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Npgsql
open Web10.Radio.Database
open Web10.Radio.Database.Repositories

[<RequireQualifiedAccess>]
module DevelopmentFixtures =
    [<Literal>]
    let private FixtureTelegramUserId = 900000001L

    [<Literal>]
    let private FixtureDisplayName = "dev_listener"

    [<Literal>]
    let private FixtureDonationAmountStars = 250

    [<Literal>]
    let private FixtureGoalTitle = "Web10.Radio launch"

    [<Literal>]
    let private FixtureGoalStars = 5000

    [<Literal>]
    let private FixtureSayText = "hello from the development fixture"

    type private FixturePayment =
        { Id: Guid
          Purpose: string
          PurposeEntityId: Guid option }

    type private CreatedFixturePayment =
        { Payment: FixturePayment
          RequiresPaidEvent: bool }

    let private databaseError operation (error: exn) =
        DatabaseError(operation, error.Message)

    let private fixtureInvoicePayload fixtureKey purpose =
        sprintf "dev:%s:%s" fixtureKey purpose

    let private stableTelegramUpdateId fixtureKey purpose =
        let payload = fixtureInvoicePayload fixtureKey purpose |> Encoding.UTF8.GetBytes
        let hash = SHA256.HashData(payload)
        let value = int64 (BigInteger(hash, true, true) % BigInteger(Int64.MaxValue))
        if value = 0L then 1L else value

    let private acquireFixtureLock
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        fixtureKey
        (cancellationToken: CancellationToken) =
        task {
            try
                use command =
                    new NpgsqlCommand(
                        "SELECT pg_advisory_xact_lock(hashtext('web10.radio.dev-fixture:' || @FixtureKey));",
                        connection,
                        transaction
                    )

                command.Parameters.AddWithValue("FixtureKey", fixtureKey) |> ignore
                let! _ = command.ExecuteScalarAsync(cancellationToken)
                return Ok()
            with
            | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested ->
                return raise error
            | error ->
                return Error(databaseError "DevelopmentFixtures.acquireFixtureLock" error)
        }

    let private findPaymentByInvoicePayload
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        invoicePayload
        (cancellationToken: CancellationToken) =
        task {
            try
                use command =
                    new NpgsqlCommand(
                        """SELECT "Id", "Purpose", "PurposeEntityId"
FROM "Payments"
WHERE "TelegramInvoicePayload" = @InvoicePayload
  AND "IsDeleted" = false
FOR UPDATE;""",
                        connection,
                        transaction
                    )

                command.Parameters.AddWithValue("InvoicePayload", invoicePayload) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)

                if not found then
                    return Ok None
                else
                    return
                        Ok(
                            Some
                                { Id = reader.GetGuid(0)
                                  Purpose = reader.GetString(1)
                                  PurposeEntityId = if reader.IsDBNull(2) then None else Some(reader.GetGuid(2)) }
                        )
            with
            | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested ->
                return raise error
            | error ->
                return Error(databaseError "DevelopmentFixtures.findPaymentByInvoicePayload" error)
        }

    let private ensureActiveGoal
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        goalId
        nowUtc
        (cancellationToken: CancellationToken) =
        task {
            try
                use command =
                    new NpgsqlCommand(
                        """INSERT INTO "DonationGoals" ("Id", "Title", "GoalStars", "RaisedStars", "IsActive", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @Title, @GoalStars, 0, true, false, @At, @At)
ON CONFLICT ("IsActive") WHERE "IsDeleted" = false AND "IsActive" = true
DO UPDATE SET "Title" = EXCLUDED."Title", "GoalStars" = EXCLUDED."GoalStars", "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc";""",
                        connection,
                        transaction
                    )

                command.Parameters.AddWithValue("Id", goalId) |> ignore
                command.Parameters.AddWithValue("Title", FixtureGoalTitle) |> ignore
                command.Parameters.AddWithValue("GoalStars", FixtureGoalStars) |> ignore
                command.Parameters.AddWithValue("At", nowUtc) |> ignore
                let! _ = command.ExecuteNonQueryAsync(cancellationToken)
                return Ok()
            with
            | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested ->
                return raise error
            | error ->
                return Error(databaseError "DevelopmentFixtures.ensureActiveGoal" error)
        }

    let private createDonationPayment
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (idGenerator: IIdGenerator)
        nowUtc
        invoicePayload
        (cancellationToken: CancellationToken) =
        task {
            let order: PaymentOrderToCreate =
                { Id = idGenerator.NewId()
                  TelegramUserId = FixtureTelegramUserId
                  Purpose = PaymentPurpose.Donation
                  PurposeEntityId = None
                  AmountStars = FixtureDonationAmountStars
                  InvoicePayload = invoicePayload
                  PayerDisplayName = Some FixtureDisplayName
                  CreatedAtUtc = nowUtc }

            let! result =
                PaymentRepository.createOrderInTransaction connection transaction order cancellationToken

            match result with
            | Error error ->
                return Error error
            | Ok(CommandOutcome.Rejected reason) ->
                return Error(DatabaseError("DevelopmentFixtures.createDonationPayment", reason))
            | Ok(CommandOutcome.Created created) ->
                return
                    Ok
                        { Payment =
                            { Id = created.Id
                              Purpose = "Donation"
                              PurposeEntityId = None }
                          RequiresPaidEvent = true }
            | Ok(CommandOutcome.AlreadyApplied existing) ->
                return
                    Ok
                        { Payment =
                            { Id = existing.Id
                              Purpose = "Donation"
                              PurposeEntityId = None }
                          RequiresPaidEvent = false }
        }

    let private createSayPayment
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (idGenerator: IIdGenerator)
        nowUtc
        sayPriceStars
        invoicePayload
        (cancellationToken: CancellationToken) =
        task {
            let sayMessageId = idGenerator.NewId()

            let sayMessage: SayMessageToCreate =
                { Id = sayMessageId
                  TelegramUserId = Some FixtureTelegramUserId
                  DisplayName = FixtureDisplayName
                  Text = FixtureSayText
                  SubmittedAtUtc = nowUtc }

            let order: PaymentOrderToCreate =
                { Id = idGenerator.NewId()
                  TelegramUserId = FixtureTelegramUserId
                  Purpose = PaymentPurpose.Say
                  PurposeEntityId = Some sayMessageId
                  AmountStars = sayPriceStars
                  InvoicePayload = invoicePayload
                  PayerDisplayName = Some FixtureDisplayName
                  CreatedAtUtc = nowUtc }

            let! result =
                TelegramCommandRepository.createSayWithPaymentInTransaction
                    connection
                    transaction
                    sayMessage
                    sayPriceStars
                    order
                    cancellationToken

            match result with
            | Error error ->
                return Error error
            | Ok(CommandOutcome.Rejected reason) ->
                return Error(DatabaseError("DevelopmentFixtures.createSayPayment", reason))
            | Ok(CommandOutcome.Created created) ->
                return
                    Ok
                        { Payment =
                            { Id = created.Id
                              Purpose = "Say"
                              PurposeEntityId = Some sayMessageId }
                          RequiresPaidEvent = true }
            | Ok(CommandOutcome.AlreadyApplied existing) ->
                match existing.PurposeEntityId with
                | None ->
                    return Error(DatabaseError("DevelopmentFixtures.createSayPayment", "The existing say payment has no say message."))
                | Some existingSayMessageId ->
                    return
                        Ok
                            { Payment =
                                { Id = existing.Id
                                  Purpose = "Say"
                                  PurposeEntityId = Some existingSayMessageId }
                              RequiresPaidEvent = false }
        }

    let private appendDonationPaid
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (idGenerator: IIdGenerator)
        (clock: IClock)
        (paymentId: Guid)
        invoicePayload
        amountStars
        telegramUpdateId
        (cancellationToken: CancellationToken) =
        task {
            let payload =
                JsonSerializer.Serialize(
                    {| paymentId = paymentId.ToString("D")
                       telegramUpdateId = telegramUpdateId
                       telegramPaymentChargeId = invoicePayload
                       telegramUserId = FixtureTelegramUserId
                       amountStars = amountStars
                       currency = "XTR" |},
                    ApiJson.options
                )

            match
                DomainEventEnvelope.create
                    idGenerator
                    clock
                    DomainEventType.DonationPaid
                    "Web10.Radio.API.Admin"
                    None
                    None
                    payload
            with
            | Error error ->
                return Error(DatabaseError("DevelopmentFixtures.appendDonationPaid", DomainEventError.toMessage error))
            | Ok envelope ->
                return!
                    OutboxEventRepository.appendInTransaction
                        connection
                        transaction
                        (OutboxMapping.toOutboxEvent envelope)
                        cancellationToken
        }

    let private existingDonation existing =
        match existing with
        | Some payment when payment.Purpose = "Donation" && payment.PurposeEntityId.IsNone ->
            Ok
                { Payment = payment
                  RequiresPaidEvent = false }
        | Some _ ->
            Error(DatabaseError("DevelopmentFixtures.existingDonation", "The fixture donation invoice payload belongs to a different payment."))
        | None ->
            Error(DatabaseError("DevelopmentFixtures.existingDonation", "No existing fixture donation payment."))

    let private existingSay existing =
        match existing with
        | Some payment when payment.Purpose = "Say" && payment.PurposeEntityId.IsSome ->
            Ok
                { Payment = payment
                  RequiresPaidEvent = false }
        | Some _ ->
            Error(DatabaseError("DevelopmentFixtures.existingSay", "The fixture say invoice payload belongs to a different payment."))
        | None ->
            Error(DatabaseError("DevelopmentFixtures.existingSay", "No existing fixture say payment."))

    let createPaidVerticalSlice
        (dataSource: NpgsqlDataSource)
        (idGenerator: IIdGenerator)
        (clock: IClock)
        sayPriceStars
        fixtureKey
        (cancellationToken: CancellationToken)
        : Task<Result<PaidVerticalSliceFixtureResponseDto, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    let donationInvoicePayload = fixtureInvoicePayload fixtureKey "donation"
                    let sayInvoicePayload = fixtureInvoicePayload fixtureKey "say"
                    let nowUtc = clock.UtcNow

                    let! lockResult = acquireFixtureLock connection transaction fixtureKey token

                    match lockResult with
                    | Error error ->
                        return Error error
                    | Ok () ->
                        let! goalResult = ensureActiveGoal connection transaction (idGenerator.NewId()) nowUtc token

                        match goalResult with
                        | Error error ->
                            return Error error
                        | Ok () ->
                            let! donationExisting =
                                findPaymentByInvoicePayload connection transaction donationInvoicePayload token

                            match donationExisting with
                            | Error error ->
                                return Error error
                            | Ok donationExisting ->
                                let! donationResult =
                                    match donationExisting with
                                    | Some _ -> Task.FromResult(existingDonation donationExisting)
                                    | None ->
                                        createDonationPayment
                                            connection
                                            transaction
                                            idGenerator
                                            nowUtc
                                            donationInvoicePayload
                                            token

                                match donationResult with
                                | Error error ->
                                    return Error error
                                | Ok donation ->
                                    let! sayExisting =
                                        findPaymentByInvoicePayload connection transaction sayInvoicePayload token

                                    match sayExisting with
                                    | Error error ->
                                        return Error error
                                    | Ok sayExisting ->
                                        let! sayResult =
                                            match sayExisting with
                                            | Some _ -> Task.FromResult(existingSay sayExisting)
                                            | None ->
                                                createSayPayment
                                                    connection
                                                    transaction
                                                    idGenerator
                                                    nowUtc
                                                    sayPriceStars
                                                    sayInvoicePayload
                                                    token

                                        match sayResult with
                                        | Error error ->
                                            return Error error
                                        | Ok say ->
                                            let! donationEventResult =
                                                if donation.RequiresPaidEvent then
                                                    appendDonationPaid
                                                        connection
                                                        transaction
                                                        idGenerator
                                                        clock
                                                        donation.Payment.Id
                                                        donationInvoicePayload
                                                        FixtureDonationAmountStars
                                                        (stableTelegramUpdateId fixtureKey "donation")
                                                        token
                                                else
                                                    Task.FromResult(Ok())

                                            match donationEventResult with
                                            | Error error ->
                                                return Error error
                                            | Ok () ->
                                                let! sayEventResult =
                                                    if say.RequiresPaidEvent then
                                                        appendDonationPaid
                                                            connection
                                                            transaction
                                                            idGenerator
                                                            clock
                                                            say.Payment.Id
                                                            sayInvoicePayload
                                                            sayPriceStars
                                                            (stableTelegramUpdateId fixtureKey "say")
                                                            token
                                                    else
                                                        Task.FromResult(Ok())

                                                match sayEventResult, say.Payment.PurposeEntityId with
                                                | Error error, _ ->
                                                    return Error error
                                                | Ok (), Some sayMessageId ->
                                                    return
                                                        Ok
                                                            { DonationPaymentId = donation.Payment.Id.ToString("D")
                                                              SayPaymentId = say.Payment.Id.ToString("D")
                                                              SayMessageId = sayMessageId.ToString("D") }
                                                | Ok (), None ->
                                                    return Error(DatabaseError("DevelopmentFixtures.createPaidVerticalSlice", "The fixture say payment has no say message."))
                })
            cancellationToken
