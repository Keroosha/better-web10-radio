namespace Web10.Radio.Tests

open System
open System.Threading
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Dodo.Primitives
open Web10.Radio.Database
open Web10.Radio.Database.Repositories

module PaymentRepositoryTests =
    let private newId () = Uuid.CreateVersion7().ToGuidBigEndian()

    let private atUtc = DateTimeOffset(2026, 7, 10, 13, 0, 0, TimeSpan.Zero)

    let private execute (connection: NpgsqlConnection) sql configure =
        task {
            use command = new NpgsqlCommand(sql, connection)
            configure command
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    let private scalarInt (connection: NpgsqlConnection) sql configure =
        task {
            use command = new NpgsqlCommand(sql, connection)
            configure command
            let! value = command.ExecuteScalarAsync()
            return Convert.ToInt32(value)
        }

    let private insertSay connection sayId telegramUserId amountStars submittedAtUtc =
        execute
            connection
            """INSERT INTO "SayMessages" ("Id", "TelegramUserId", "DisplayName", "Text", "AmountStars", "Status", "SubmittedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @TelegramUserId, 'Payment test user', 'A paid message', @AmountStars, 'PendingPayment', @At, false, @At, @At);"""
            (fun command ->
                command.Parameters.AddWithValue("Id", sayId) |> ignore
                command.Parameters.AddWithValue("TelegramUserId", telegramUserId) |> ignore
                command.Parameters.AddWithValue("AmountStars", amountStars) |> ignore
                command.Parameters.AddWithValue("At", submittedAtUtc) |> ignore)

    let private insertTrackRequest connection requestId trackId telegramUserId requestedAtUtc =
        task {
            do!
                execute
                    connection
                    """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@TrackId, 'Payment test track', 'Payment test artist', false, @At, @At);"""
                    (fun command ->
                        command.Parameters.AddWithValue("TrackId", trackId) |> ignore
                        command.Parameters.AddWithValue("At", requestedAtUtc) |> ignore)

            do!
                execute
                    connection
                    """INSERT INTO "TrackRequests" ("Id", "TelegramUserId", "DisplayName", "Query", "MatchedTrackId", "Status", "RequestedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @TelegramUserId, 'Payment test user', 'Payment test track', @TrackId, 'PaidPending', @At, false, @At, @At);"""
                    (fun command ->
                        command.Parameters.AddWithValue("Id", requestId) |> ignore
                        command.Parameters.AddWithValue("TelegramUserId", telegramUserId) |> ignore
                        command.Parameters.AddWithValue("TrackId", trackId) |> ignore
                        command.Parameters.AddWithValue("At", requestedAtUtc) |> ignore)
        }

    let private setSayStatus connection sayId status updatedAtUtc =
        execute
            connection
            """UPDATE "SayMessages" SET "Status" = @Status, "UpdatedAtUtc" = @At WHERE "Id" = @Id;"""
            (fun command ->
                command.Parameters.AddWithValue("Id", sayId) |> ignore
                command.Parameters.AddWithValue("Status", status) |> ignore
                command.Parameters.AddWithValue("At", updatedAtUtc) |> ignore)

    let private setPaymentStatus connection paymentId status updatedAtUtc =
        execute
            connection
            """UPDATE "Payments" SET "Status" = @Status, "UpdatedAtUtc" = @At WHERE "Id" = @Id;"""
            (fun command ->
                command.Parameters.AddWithValue("Id", paymentId) |> ignore
                command.Parameters.AddWithValue("Status", status) |> ignore
                command.Parameters.AddWithValue("At", updatedAtUtc) |> ignore)

    let private readPayment connection paymentId =
        task {
            use command =
                new NpgsqlCommand(
                    """SELECT "TelegramUserId", "Purpose", "PurposeEntityId", "AmountStars", "Currency", "ProviderToken", "TelegramInvoicePayload", "Status", "TelegramPaymentChargeId", "PaidAtUtc"
FROM "Payments" WHERE "Id" = @Id AND "IsDeleted" = false;""",
                    connection
                )

            command.Parameters.AddWithValue("Id", paymentId) |> ignore
            use! reader = command.ExecuteReaderAsync()
            let! found = reader.ReadAsync()

            if not found then
                return failwithf "Expected active payment %O to exist." paymentId
            else
                return
                    (reader.GetInt64(0),
                     reader.GetString(1),
                     (if reader.IsDBNull(2) then None else Some(reader.GetGuid(2))),
                     reader.GetInt32(3),
                     reader.GetString(4),
                     reader.GetString(5),
                     reader.GetString(6),
                     reader.GetString(7),
                     (if reader.IsDBNull(8) then None else Some(reader.GetString(8))),
                     (if reader.IsDBNull(9) then None else Some(reader.GetFieldValue<DateTimeOffset>(9))))
        }

    let private readStatus connection tableName id =
        task {
            use command = new NpgsqlCommand(sprintf "SELECT \"Status\" FROM \"%s\" WHERE \"Id\" = @Id AND \"IsDeleted\" = false;" tableName, connection)
            command.Parameters.AddWithValue("Id", id) |> ignore
            let! value = command.ExecuteScalarAsync()
            return if isNull value then failwithf "Expected active %s %O to exist." tableName id else string value
        }

    let private insertActiveDonationGoal connection goalId title raisedStars goalStars =
        execute
            connection
            """INSERT INTO "DonationGoals" ("Id", "Title", "GoalStars", "RaisedStars", "IsActive", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @Title, @GoalStars, @RaisedStars, true, false, @At, @At);"""
            (fun command ->
                command.Parameters.AddWithValue("Id", goalId) |> ignore
                command.Parameters.AddWithValue("Title", title) |> ignore
                command.Parameters.AddWithValue("GoalStars", goalStars) |> ignore
                command.Parameters.AddWithValue("RaisedStars", raisedStars) |> ignore
                command.Parameters.AddWithValue("At", atUtc) |> ignore)

    let private loadDonationGoal dataSource =
        task {
            let! result = PlayerStateReadModel.loadDonationGoal dataSource CancellationToken.None

            match result with
            | Ok goal -> return goal
            | Error error -> return failwithf "Expected donation projection to load, but got %A." error
        }

    let private createOrder dataSource order =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                PaymentRepository.createOrderInTransaction connection transaction order cancellationToken)
            CancellationToken.None

    let private getOrder dataSource paymentId =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                PaymentRepository.tryGetOrderInTransaction connection transaction paymentId cancellationToken)
            CancellationToken.None

    let private validatePreCheckout dataSource telegramUserId currency amountStars invoicePayload =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                PaymentRepository.validatePreCheckoutInTransaction
                    connection
                    transaction
                    telegramUserId
                    currency
                    amountStars
                    invoicePayload
                    cancellationToken)
            CancellationToken.None

    let private assertOrderCreated
        (order: PaymentOrderToCreate)
        (result: Result<CommandOutcome<PaymentOrderToCreate>, RepositoryError>) =
        match result with
        | Ok(CommandOutcome.Created created) -> Assert.That(created, Is.EqualTo(order))
        | actual -> Assert.Fail(sprintf "Expected a newly created payment order, but got %A." actual)

    let private assertOrderAlreadyApplied
        (order: PaymentOrderToCreate)
        (result: Result<CommandOutcome<PaymentOrderToCreate>, RepositoryError>) =
        match result with
        | Ok(CommandOutcome.AlreadyApplied existing) -> Assert.That(existing, Is.EqualTo(order))
        | actual -> Assert.Fail(sprintf "Expected an idempotent payment-order outcome, but got %A." actual)

    let private assertOrderRejected (result: Result<CommandOutcome<PaymentOrderToCreate>, RepositoryError>) =
        match result with
        | Ok(CommandOutcome.Rejected _) -> ()
        | actual -> Assert.Fail(sprintf "Expected a rejected payment-order outcome, but got %A." actual)

    let private assertPreCheckoutApproved (result: Result<PreCheckoutDecision, RepositoryError>) =
        match result with
        | Ok PreCheckoutDecision.Approved -> ()
        | actual -> Assert.Fail(sprintf "Expected pre-checkout approval, but got %A." actual)

    let private assertPreCheckoutRejected (result: Result<PreCheckoutDecision, RepositoryError>) =
        match result with
        | Ok(PreCheckoutDecision.Rejected _) -> ()
        | actual -> Assert.Fail(sprintf "Expected pre-checkout rejection, but got %A." actual)

    let private assertCompleted
        (paymentId: Guid)
        (purpose: PaymentPurpose)
        (entityId: Guid option)
        (alreadyApplied: bool)
        (result: Result<CompletePaymentOutcome, RepositoryError>) =
        match result with
        | Ok(Completed completed) ->
            Assert.That(completed.PaymentId, Is.EqualTo(paymentId))
            Assert.That(completed.Purpose, Is.EqualTo(purpose))
            Assert.That(completed.PurposeEntityId, Is.EqualTo(entityId))
            Assert.That(completed.AlreadyApplied, Is.EqualTo(alreadyApplied))
        | actual -> Assert.Fail(sprintf "Expected completed payment outcome, but got %A." actual)

    let private assertCompletionRejected (result: Result<CompletePaymentOutcome, RepositoryError>) =
        match result with
        | Ok(CompletePaymentOutcome.Rejected _) -> ()
        | actual -> Assert.Fail(sprintf "Expected terminal payment rejection, but got %A." actual)

    let private assertChargeReuseRejected (result: Result<CompletePaymentOutcome, RepositoryError>) =
        match result with
        | Ok(CompletePaymentOutcome.Rejected "telegram charge id is already assigned") -> ()
        | actual -> Assert.Fail(sprintf "Expected the unique charge constraint to map to its terminal rejection, but got %A." actual)

    let private paymentOrder paymentId telegramUserId purpose purposeEntityId amountStars invoicePayload payerDisplayName =
        { Id = paymentId
          TelegramUserId = telegramUserId
          Purpose = purpose
          PurposeEntityId = purposeEntityId
          AmountStars = amountStars
          InvoicePayload = invoicePayload
          PayerDisplayName = payerDisplayName
          CreatedAtUtc = atUtc }

    let private createAndCompleteDonation dataSource paymentId telegramUserId amountStars invoicePayload payerDisplayName chargeId paidAtUtc =
        task {
            let order = paymentOrder paymentId telegramUserId PaymentPurpose.Donation None amountStars invoicePayload payerDisplayName
            let! created = createOrder dataSource order
            assertOrderCreated order created
            let! completion = PaymentRepository.completePayment dataSource paymentId telegramUserId chargeId amountStars "XTR" paidAtUtc CancellationToken.None
            assertCompleted paymentId PaymentPurpose.Donation None false completion
        }

    [<Test>]
    let ``createOrderInTransaction writes the fixed Stars transport contract and preserves idempotency distinctions`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                let paymentId = newId ()
                let order = paymentOrder paymentId 42001L PaymentPurpose.Donation None 73 "donation-order-1" (Some "Payment order snapshot")
                let mismatchedOrder = { order with PayerDisplayName = Some "Replacement snapshot" }

                let! created = createOrder dataSource order
                let! replay = createOrder dataSource order
                let! mismatch = createOrder dataSource mismatchedOrder

                assertOrderCreated order created
                assertOrderAlreadyApplied order replay
                assertOrderRejected mismatch
                let! persisted = getOrder dataSource paymentId

                let! userId, purpose, entityId, amount, currency, providerToken, payload, status, chargeId, paidAtUtc = readPayment connection paymentId
                Assert.That(userId, Is.EqualTo(42001L))
                Assert.That(purpose, Is.EqualTo("Donation"))
                Assert.That(Option.isNone entityId, Is.True)
                Assert.That(amount, Is.EqualTo(73))
                Assert.That(currency, Is.EqualTo("XTR"))
                Assert.That(providerToken, Is.EqualTo(""))
                Assert.That(payload, Is.EqualTo("donation-order-1"))
                match persisted with
                | Ok(Some persistedOrder) -> Assert.That(persistedOrder.PayerDisplayName, Is.EqualTo(Some "Payment order snapshot"))
                | actual -> Assert.Fail(sprintf "Expected the persisted payment order, but got %A." actual)
                Assert.That(status, Is.EqualTo("InvoiceCreated"))
                Assert.That(Option.isNone chargeId, Is.True)
                Assert.That(Option.isNone paidAtUtc, Is.True)
            })

    [<Test>]
    let ``pre-checkout approves only a matching live Say order and leaves the message pending payment`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                let sayId = newId ()
                let paymentId = newId ()
                let userId = 42002L
                let amount = 51
                let payload = "say-precheckout-valid"
                do! insertSay connection sayId userId amount atUtc
                let order = paymentOrder paymentId userId PaymentPurpose.Say (Some sayId) amount payload (Some "Pre-checkout payer")
                let! created = createOrder dataSource order
                assertOrderCreated order created

                let! firstApproval = validatePreCheckout dataSource userId "XTR" amount payload
                let! replayApproval = validatePreCheckout dataSource userId "XTR" amount payload
                assertPreCheckoutApproved firstApproval
                assertPreCheckoutApproved replayApproval

                let! _, _, _, storedAmount, currency, providerToken, _, paymentStatus, chargeId, paidAtUtc = readPayment connection paymentId
                let! sayStatus = readStatus connection "SayMessages" sayId
                let! queueCount = scalarInt connection "SELECT count(*) FROM \"PlaybackQueue\";" ignore
                Assert.That(storedAmount, Is.EqualTo(amount))
                Assert.That(currency, Is.EqualTo("XTR"))
                Assert.That(providerToken, Is.EqualTo(""))
                Assert.That(paymentStatus, Is.EqualTo("PreCheckoutApproved"))
                Assert.That(Option.isNone chargeId, Is.True)
                Assert.That(Option.isNone paidAtUtc, Is.True)
                Assert.That(sayStatus, Is.EqualTo("PendingPayment"))
                Assert.That(queueCount, Is.EqualTo(0))
            })

    [<Test>]
    let ``pre-checkout rejects bad payload identity currency amount and terminal payment status without mutation`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                let sayId = newId ()
                let paymentId = newId ()
                let userId = 42003L
                let amount = 52
                let payload = "say-precheckout-invalid"
                do! insertSay connection sayId userId amount atUtc
                let order = paymentOrder paymentId userId PaymentPurpose.Say (Some sayId) amount payload (Some "Invalid pre-checkout payer")
                let! created = createOrder dataSource order
                assertOrderCreated order created

                let attempts =
                    [ "unknown payload", userId, "XTR", amount, "does-not-exist"
                      "wrong user", userId + 1L, "XTR", amount, payload
                      "wrong currency", userId, "USD", amount, payload
                      "wrong amount", userId, "XTR", amount + 1, payload ]

                for name, attemptedUserId, attemptedCurrency, attemptedAmount, attemptedPayload in attempts do
                    let! decision = validatePreCheckout dataSource attemptedUserId attemptedCurrency attemptedAmount attemptedPayload
                    match decision with
                    | Ok(PreCheckoutDecision.Rejected _) -> ()
                    | actual -> Assert.Fail(sprintf "Expected %s to reject pre-checkout, but got %A." name actual)

                let! _, _, _, _, _, _, _, statusBeforeTerminalAttempt, chargeBeforeTerminalAttempt, paidBeforeTerminalAttempt = readPayment connection paymentId
                let! sayStatusBeforeTerminalAttempt = readStatus connection "SayMessages" sayId
                Assert.That(statusBeforeTerminalAttempt, Is.EqualTo("InvoiceCreated"))
                Assert.That(Option.isNone chargeBeforeTerminalAttempt, Is.True)
                Assert.That(Option.isNone paidBeforeTerminalAttempt, Is.True)
                Assert.That(sayStatusBeforeTerminalAttempt, Is.EqualTo("PendingPayment"))

                do! setPaymentStatus connection paymentId "Rejected" (atUtc.AddMinutes(1.0))
                let! terminalDecision = validatePreCheckout dataSource userId "XTR" amount payload
                assertPreCheckoutRejected terminalDecision

                let! _, _, _, _, _, _, _, statusAfterTerminalAttempt, chargeAfterTerminalAttempt, paidAfterTerminalAttempt = readPayment connection paymentId
                let! sayStatusAfterTerminalAttempt = readStatus connection "SayMessages" sayId
                Assert.That(statusAfterTerminalAttempt, Is.EqualTo("Rejected"))
                Assert.That(Option.isNone chargeAfterTerminalAttempt, Is.True)
                Assert.That(Option.isNone paidAfterTerminalAttempt, Is.True)
                Assert.That(sayStatusAfterTerminalAttempt, Is.EqualTo("PendingPayment"))
            })

    [<Test>]
    let ``successful Say payment is the first paid transition and identical replays survive both moderation terminals`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                let userId = 42004L
                let amount = 53

                for moderationStatus in [ "Approved"; "Rejected" ] do
                    let sayId = newId ()
                    let paymentId = newId ()
                    let chargeId = sprintf "say-charge-%s" moderationStatus
                    do! insertSay connection sayId userId amount atUtc
                    let order = paymentOrder paymentId userId PaymentPurpose.Say (Some sayId) amount (sprintf "say-%s" moderationStatus) (Some "Say payer")
                    let! created = createOrder dataSource order
                    assertOrderCreated order created

                    let! firstCompletion = PaymentRepository.completePayment dataSource paymentId userId chargeId amount "XTR" (atUtc.AddMinutes(2.0)) CancellationToken.None
                    assertCompleted paymentId PaymentPurpose.Say (Some sayId) false firstCompletion

                    let! paymentAfterFirstCompletion = readPayment connection paymentId
                    let! sayAfterFirstCompletion = readStatus connection "SayMessages" sayId
                    let _, _, _, _, _, _, _, paymentStatus, storedCharge, paidAtUtc = paymentAfterFirstCompletion
                    Assert.That(paymentStatus, Is.EqualTo("Paid"))
                    Assert.That(storedCharge, Is.EqualTo(Some chargeId))
                    Assert.That(paidAtUtc, Is.EqualTo(Some(atUtc.AddMinutes(2.0))))
                    Assert.That(sayAfterFirstCompletion, Is.EqualTo("PaidPendingModeration"))

                    do! setSayStatus connection sayId moderationStatus (atUtc.AddMinutes(3.0))
                    let! replay = PaymentRepository.completePayment dataSource paymentId userId chargeId amount "XTR" (atUtc.AddMinutes(4.0)) CancellationToken.None
                    assertCompleted paymentId PaymentPurpose.Say (Some sayId) true replay

                let mismatchSayId = newId ()
                let mismatchPaymentId = newId ()
                let acceptedChargeId = "say-charge-accepted"
                do! insertSay connection mismatchSayId userId amount atUtc
                let mismatchOrder = paymentOrder mismatchPaymentId userId PaymentPurpose.Say (Some mismatchSayId) amount "say-mismatch" (Some "Say mismatch payer")
                let! mismatchOrderCreated = createOrder dataSource mismatchOrder
                assertOrderCreated mismatchOrder mismatchOrderCreated
                let! accepted = PaymentRepository.completePayment dataSource mismatchPaymentId userId acceptedChargeId amount "XTR" (atUtc.AddMinutes(2.0)) CancellationToken.None
                assertCompleted mismatchPaymentId PaymentPurpose.Say (Some mismatchSayId) false accepted

                let invalidCompletions =
                    [ "different charge", userId, "other-charge", amount, "XTR"
                      "different user", userId + 1L, acceptedChargeId, amount, "XTR"
                      "different amount", userId, acceptedChargeId, amount + 1, "XTR"
                      "different currency", userId, acceptedChargeId, amount, "USD" ]

                for name, attemptedUserId, attemptedChargeId, attemptedAmount, attemptedCurrency in invalidCompletions do
                    let! outcome = PaymentRepository.completePayment dataSource mismatchPaymentId attemptedUserId attemptedChargeId attemptedAmount attemptedCurrency (atUtc.AddMinutes(5.0)) CancellationToken.None
                    match outcome with
                    | Ok(CompletePaymentOutcome.Rejected _) -> ()
                    | actual -> Assert.Fail(sprintf "Expected %s to reject completion, but got %A." name actual)

                let! _, _, _, _, _, _, _, mismatchPaymentStatus, mismatchStoredCharge, mismatchPaidAtUtc = readPayment connection mismatchPaymentId
                let! mismatchSayStatus = readStatus connection "SayMessages" mismatchSayId
                Assert.That(mismatchPaymentStatus, Is.EqualTo("Paid"))
                Assert.That(mismatchStoredCharge, Is.EqualTo(Some acceptedChargeId))
                Assert.That(mismatchPaidAtUtc, Is.EqualTo(Some(atUtc.AddMinutes(2.0))))
                Assert.That(mismatchSayStatus, Is.EqualTo("PaidPendingModeration"))
            })

    [<Test>]
    let ``successful Request payment queues its matched track exactly once and duplicate completion is observable as already applied`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                let userId = 42005L
                let requestId = newId ()
                let trackId = newId ()
                let paymentId = newId ()
                let amount = 54
                let chargeId = "request-charge-1"
                let paidAtUtc = atUtc.AddMinutes(2.0)
                do! insertTrackRequest connection requestId trackId userId atUtc
                let order = paymentOrder paymentId userId PaymentPurpose.Request (Some requestId) amount "request-payment" (Some "Request payer")
                let! created = createOrder dataSource order
                assertOrderCreated order created

                let! firstCompletion = PaymentRepository.completePayment dataSource paymentId userId chargeId amount "XTR" paidAtUtc CancellationToken.None
                let! replay = PaymentRepository.completePayment dataSource paymentId userId chargeId amount "XTR" (paidAtUtc.AddMinutes(1.0)) CancellationToken.None
                assertCompleted paymentId PaymentPurpose.Request (Some requestId) false firstCompletion
                assertCompleted paymentId PaymentPurpose.Request (Some requestId) true replay

                let! requestStatus = readStatus connection "TrackRequests" requestId
                let! _, _, _, _, _, _, _, paymentStatus, storedCharge, paymentPaidAtUtc = readPayment connection paymentId
                let! queueCount =
                    scalarInt
                        connection
                        """SELECT count(*) FROM "PlaybackQueue"
WHERE "Id" = @PaymentId AND "TrackId" = @TrackId AND "TrackRequestId" = @RequestId
  AND "Source" = 'request' AND "Status" = 'Queued' AND "IsDeleted" = false;"""
                        (fun command ->
                            command.Parameters.AddWithValue("PaymentId", paymentId) |> ignore
                            command.Parameters.AddWithValue("TrackId", trackId) |> ignore
                            command.Parameters.AddWithValue("RequestId", requestId) |> ignore)
                Assert.That(requestStatus, Is.EqualTo("Paid"))
                Assert.That(paymentStatus, Is.EqualTo("Paid"))
                Assert.That(storedCharge, Is.EqualTo(Some chargeId))
                Assert.That(paymentPaidAtUtc, Is.EqualTo(Some paidAtUtc))
                Assert.That(queueCount, Is.EqualTo(1))
            })

    [<Test>]
    let ``charge reuse is terminal for the conflicting payment and does not block a later valid completion`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                let userId = 42006L
                let amount = 55
                let sharedCharge = "globally-unique-charge"
                let firstPaymentId = newId ()
                let conflictingPaymentId = newId ()
                let laterPaymentId = newId ()
                let firstOrder = paymentOrder firstPaymentId userId PaymentPurpose.Donation None amount "charge-owner" (Some "Charge owner")
                let conflictingOrder = paymentOrder conflictingPaymentId userId PaymentPurpose.Donation None amount "charge-conflict" (Some "Charge conflict")
                let laterOrder = paymentOrder laterPaymentId userId PaymentPurpose.Donation None amount "charge-later" (Some "Later charge")

                let! firstCreated = createOrder dataSource firstOrder
                let! conflictingCreated = createOrder dataSource conflictingOrder
                let! laterCreated = createOrder dataSource laterOrder
                assertOrderCreated firstOrder firstCreated
                assertOrderCreated conflictingOrder conflictingCreated
                assertOrderCreated laterOrder laterCreated

                let! firstCompletion = PaymentRepository.completePayment dataSource firstPaymentId userId sharedCharge amount "XTR" (atUtc.AddMinutes(2.0)) CancellationToken.None
                let! conflictingCompletion = PaymentRepository.completePayment dataSource conflictingPaymentId userId sharedCharge amount "XTR" (atUtc.AddMinutes(3.0)) CancellationToken.None
                let! laterCompletion = PaymentRepository.completePayment dataSource laterPaymentId userId "later-charge" amount "XTR" (atUtc.AddMinutes(4.0)) CancellationToken.None
                assertCompleted firstPaymentId PaymentPurpose.Donation None false firstCompletion
                assertChargeReuseRejected conflictingCompletion
                assertCompleted laterPaymentId PaymentPurpose.Donation None false laterCompletion

                let! _, _, _, _, _, _, _, firstStatus, firstCharge, _ = readPayment connection firstPaymentId
                let! _, _, _, _, _, _, _, conflictingStatus, conflictingCharge, conflictingPaidAtUtc = readPayment connection conflictingPaymentId
                let! _, _, _, _, _, _, _, laterStatus, laterCharge, _ = readPayment connection laterPaymentId
                Assert.That(firstStatus, Is.EqualTo("Paid"))
                Assert.That(firstCharge, Is.EqualTo(Some sharedCharge))
                Assert.That(conflictingStatus, Is.EqualTo("InvoiceCreated"))
                Assert.That(Option.isNone conflictingCharge, Is.True)
                Assert.That(Option.isNone conflictingPaidAtUtc, Is.True)
                Assert.That(laterStatus, Is.EqualTo("Paid"))
                Assert.That(laterCharge, Is.EqualTo(Some "later-charge"))
            })

    [<Test>]
    let ``Donation completion reaches Paid without an active goal and replay is a no-op`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                let! activeGoalCount =
                    scalarInt
                        connection
                        "SELECT count(*) FROM \"DonationGoals\" WHERE \"IsDeleted\" = false AND \"IsActive\" = true;"
                        ignore
                Assert.That(activeGoalCount, Is.Zero)

                let paymentId = newId ()
                let userId = 42007L
                let amount = 56
                let chargeId = "donation-charge-1"
                let paidAtUtc = atUtc.AddMinutes(2.0)
                let order = paymentOrder paymentId userId PaymentPurpose.Donation None amount "purpose-neutral-donation" (Some "Donation payer")
                let! created = createOrder dataSource order
                assertOrderCreated order created

                let! firstCompletion = PaymentRepository.completePayment dataSource paymentId userId chargeId amount "XTR" paidAtUtc CancellationToken.None
                let! replay = PaymentRepository.completePayment dataSource paymentId userId chargeId amount "XTR" (paidAtUtc.AddMinutes(1.0)) CancellationToken.None
                assertCompleted paymentId PaymentPurpose.Donation None false firstCompletion
                assertCompleted paymentId PaymentPurpose.Donation None true replay

                let! _, purpose, entityId, storedAmount, currency, providerToken, _, status, storedCharge, storedPaidAtUtc = readPayment connection paymentId
                Assert.That(purpose, Is.EqualTo("Donation"))
                Assert.That(Option.isNone entityId, Is.True)
                Assert.That(storedAmount, Is.EqualTo(amount))
                Assert.That(currency, Is.EqualTo("XTR"))
                Assert.That(providerToken, Is.EqualTo(""))
                Assert.That(status, Is.EqualTo("Paid"))
                Assert.That(storedCharge, Is.EqualTo(Some chargeId))
                Assert.That(storedPaidAtUtc, Is.EqualTo(Some paidAtUtc))
            })

    [<Test>]
    let ``first paid Donation raises the active goal once and replay leaves its amount unchanged`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                let goalId = newId ()
                let paymentId = newId ()
                let userId = 42008L
                let amount = 57
                do! insertActiveDonationGoal connection goalId "Payment invariant" 100 1000
                let order = paymentOrder paymentId userId PaymentPurpose.Donation None amount "goal-increment" (Some "Goal donor")
                let! created = createOrder dataSource order
                assertOrderCreated order created

                let! firstCompletion = PaymentRepository.completePayment dataSource paymentId userId "goal-charge" amount "XTR" (atUtc.AddMinutes(2.0)) CancellationToken.None
                let! replay = PaymentRepository.completePayment dataSource paymentId userId "goal-charge" amount "XTR" (atUtc.AddMinutes(3.0)) CancellationToken.None
                assertCompleted paymentId PaymentPurpose.Donation None false firstCompletion
                assertCompleted paymentId PaymentPurpose.Donation None true replay

                let! raisedStars =
                    scalarInt
                        connection
                        "SELECT \"RaisedStars\" FROM \"DonationGoals\" WHERE \"Id\" = @Id AND \"IsDeleted\" = false AND \"IsActive\" = true;"
                        (fun command -> command.Parameters.AddWithValue("Id", goalId) |> ignore)
                Assert.That(raisedStars, Is.EqualTo(157))
            })


    [<Test>]
    let ``recent donation projects the payer snapshot stored with the payment`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                do! insertActiveDonationGoal connection (newId ()) "Projection goal" 0 1000
                let paymentId = newId ()
                do!
                    createAndCompleteDonation
                        dataSource
                        paymentId
                        42010L
                        59
                        "recent-snapshot"
                        (Some "Snapshot donor")
                        "recent-snapshot-charge"
                        (atUtc.AddMinutes(2.0))
                let! goal = loadDonationGoal dataSource
                let recent = goal.Recent |> List.exactlyOne
                Assert.That(recent.Id, Is.EqualTo(paymentId.ToString("D")))
                Assert.That(recent.DisplayName, Is.EqualTo("Snapshot donor"))
                Assert.That(recent.AmountStars, Is.EqualTo(59))
            })

    [<Test>]
    let ``top donor totals by Telegram user and resolves a tied winner to its latest nonblank snapshot`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                do! insertActiveDonationGoal connection (newId ()) "Top donor goal" 0 1000
                do! createAndCompleteDonation dataSource (newId ()) 42011L 60 "top-old-name" (Some "Earlier name") "top-charge-1" (atUtc.AddMinutes(1.0))
                do! createAndCompleteDonation dataSource (newId ()) 42011L 100 "top-latest-name" (Some "Latest name") "top-charge-2" (atUtc.AddMinutes(3.0))
                do! createAndCompleteDonation dataSource (newId ()) 42011L 1 "top-blank-name" (Some " ") "top-charge-3" (atUtc.AddMinutes(5.0))
                do! createAndCompleteDonation dataSource (newId ()) 42012L 161 "top-tie" (Some "Tied donor") "top-charge-4" (atUtc.AddMinutes(4.0))

                let! goal = loadDonationGoal dataSource
                Assert.That(goal.TopDonator.AmountStars, Is.EqualTo(161))
                Assert.That(goal.TopDonator.DisplayName, Is.EqualTo("Latest name"))
            })

    [<Test>]
    let ``top donor without a nonblank payer snapshot is anonymous`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                do! insertActiveDonationGoal connection (newId ()) "Anonymous donor goal" 0 1000
                do! createAndCompleteDonation dataSource (newId ()) 42013L 61 "anonymous-top" None "anonymous-top-charge" (atUtc.AddMinutes(2.0))
                let! goal = loadDonationGoal dataSource
                Assert.That(goal.TopDonator.AmountStars, Is.EqualTo(61))
                Assert.That(goal.TopDonator.DisplayName, Is.EqualTo("anonymous"))
            })
