namespace Web10.Radio.Telegram

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Microsoft.Extensions.Logging
open Npgsql
open Dodo.Primitives
open Web10.Radio.Application
open Web10.Radio.Database
open Web10.Radio.Database.Repositories

[<RequireQualifiedAccess>]
module private TelegramWorkerPayload =
    let parseObject eventType payloadJson =
        if String.IsNullOrWhiteSpace payloadJson then
            Error(InvalidPayload(eventType, "payload must be a JSON object."))
        else
            try
                use document = JsonDocument.Parse(payloadJson)
                if document.RootElement.ValueKind = JsonValueKind.Object then Ok(document.RootElement.Clone())
                else Error(InvalidPayload(eventType, "payload must be a JSON object."))
            with :? JsonException as ex -> Error(InvalidPayload(eventType, ex.Message))

    let requiredString (eventType: string) (name: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String then
            match value.GetString() with
            | text when not (String.IsNullOrWhiteSpace text) -> Ok text
            | _ -> Error(InvalidPayload(eventType, sprintf "%s is required." name))
        else Error(InvalidPayload(eventType, sprintf "%s is required." name))

    let requiredInt64 (eventType: string) (name: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        let mutable parsed = 0L
        if root.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.Number && value.TryGetInt64(&parsed) then Ok parsed
        else Error(InvalidPayload(eventType, sprintf "%s is required and must be an integer." name))

    let requiredInt (eventType: string) (name: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        let mutable parsed = 0
        if root.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.Number && value.TryGetInt32(&parsed) then Ok parsed
        else Error(InvalidPayload(eventType, sprintf "%s is required and must be an integer." name))

    let requirePositiveGuid (eventType: string) (name: string) (root: JsonElement) =
        result {
            let! text = requiredString eventType name root
            let mutable value = Guid.Empty
            do! Guid.TryParse(text, &value) |> Result.requireTrue (InvalidPayload(eventType, sprintf "%s must be a UUID." name))
            do! (value <> Guid.Empty) |> Result.requireTrue (InvalidPayload(eventType, sprintf "%s must not be empty." name))
            return value
        }

    let donationPaid payloadJson =
        result {
            let! root = parseObject "DonationPaid" payloadJson
            let! paymentId = requirePositiveGuid "DonationPaid" "paymentId" root
            let! updateId = requiredInt64 "DonationPaid" "telegramUpdateId" root
            let! userId = requiredInt64 "DonationPaid" "telegramUserId" root
            let! chargeId = requiredString "DonationPaid" "telegramPaymentChargeId" root
            let! amount = requiredInt "DonationPaid" "amountStars" root
            let! currency = requiredString "DonationPaid" "currency" root
            do! (updateId >= 0L) |> Result.requireTrue (InvalidPayload("DonationPaid", "telegramUpdateId must be non-negative."))
            do! (userId > 0L) |> Result.requireTrue (InvalidPayload("DonationPaid", "telegramUserId must be positive."))
            do! (amount > 0) |> Result.requireTrue (InvalidPayload("DonationPaid", "amountStars must be positive."))
            do! (currency = "XTR") |> Result.requireTrue (InvalidPayload("DonationPaid", "currency must be XTR."))
            return paymentId, updateId, userId, chargeId, amount, currency
        }

    let projection eventType payloadJson =
        result {
            let! root = parseObject eventType payloadJson
            match eventType with
            | "TrackRequestMatched" ->
                let! _ = requirePositiveGuid eventType "trackRequestId" root
                let! _ = requirePositiveGuid eventType "trackId" root
                return ()
            | "SayMessageModerated" ->
                let! _ = requiredString eventType "messageId" root
                let! _ = requiredString eventType "status" root
                return ()
            | "PaymentRefunded" ->
                let! _ = requirePositiveGuid eventType "paymentId" root
                return ()
            | _ -> return! Error(UnknownEventType eventType)
        }

[<RequireQualifiedAccess>]
module private TelegramWorkerLog =
    let paymentRejected (logger: ILogger) eventId paymentId reason =
        logger.LogWarning("Telegram payment event {EventId} for payment {PaymentId} was rejected: {Reason}", eventId, paymentId, reason)

    let dispatchFailed (logger: ILogger) eventType error =
        logger.LogError("Telegram event {EventType} dispatch failed: {Error}", eventType, BackgroundWorkerError.toMessage error)

type TelegramUpdateEventIngestor(dataSource: NpgsqlDataSource, timeProvider: TimeProvider) =
    interface ITelegramUpdateEventIngestor with
        member _.TryIngestAsync telegramUpdateId eventType producer payloadJson cancellationToken =
            task {
                match DomainEventEnvelope.create timeProvider eventType producer None None payloadJson |> Result.mapError DomainEventError with
                | Error error -> return Error error
                | Ok envelope ->
                    let inboxRecord =
                        { Id = Uuid.CreateVersion7().ToGuidBigEndian()
                          TelegramUpdateId = telegramUpdateId
                          EventType = DomainEventType.toString eventType
                          ReceivedAtUtc = timeProvider.GetUtcNow()
                          CorrelationId = Some envelope.CorrelationId
                          PayloadJson = envelope.PayloadJson }
                    return!
                        DatabaseSession.withTransactionResult
                            dataSource
                            (fun connection transaction token ->
                                taskResult {
                                    let! inserted = TelegramUpdateInboxRepository.tryRecordInTransaction connection transaction inboxRecord token |> TaskResult.mapError RepositoryError
                                    if inserted then
                                        do! OutboxEventRepository.appendInTransaction connection transaction (OutboxMapping.toOutboxEvent envelope) token |> TaskResult.mapError RepositoryError
                                    return inserted
                                })
                            cancellationToken
            }

type TelegramDomainEventPublisher(dataSource: NpgsqlDataSource) =
    interface IDomainEventPublisher with
        member _.PublishDurableAsync envelope cancellationToken =
            OutboxEventRepository.append dataSource (OutboxMapping.toOutboxEvent envelope) cancellationToken |> TaskResult.mapError RepositoryError

type TelegramDomainEventDispatcher
    (
        dataSource: NpgsqlDataSource,
        timeProvider: TimeProvider,
        telegramWorkflow: ITelegramBotWorkflow,
        logger: ILogger<TelegramDomainEventDispatcher>
    ) =
    let handlePayment (envelope: DomainEventEnvelope) cancellationToken =
        taskResult {
            let! paymentId, updateId, userId, chargeId, amount, currency = TelegramWorkerPayload.donationPaid envelope.PayloadJson
            let! outcome = PaymentRepository.completePayment dataSource paymentId userId chargeId amount currency (timeProvider.GetUtcNow()) cancellationToken |> TaskResult.mapError RepositoryError
            match outcome with
            | Completed _ -> return ()
            | Rejected reason ->
                TelegramWorkerLog.paymentRejected logger envelope.EventId paymentId reason
                return ()
        }

    let handleProjection (eventType: DomainEventType) (envelope: DomainEventEnvelope) : Task<Result<unit, BackgroundWorkerError>> =
        TelegramWorkerPayload.projection (DomainEventType.toString eventType) envelope.PayloadJson
        |> Task.FromResult

    interface IDomainEventDispatcher with
        member _.DispatchAsync envelope cancellationToken =
            match envelope.EventType with
            | DonationPaid -> handlePayment envelope cancellationToken
            | TrackRequested
            | SayMessageSubmitted
            | TelegramCommandReceived
            | TelegramCallbackReceived -> telegramWorkflow.HandleInteractionAsync envelope cancellationToken
            | DonationInvoiceCreated -> telegramWorkflow.SendInvoiceAsync envelope cancellationToken
            | TrackRequestMatched
            | SayMessageModerated
            | PaymentRefunded -> handleProjection envelope.EventType envelope
            | _ -> Task.FromResult(Error(UnknownEventType(DomainEventType.toString envelope.EventType)))
