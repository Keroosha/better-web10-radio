namespace Web10.Radio.API

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql
open Web10.Radio.Database
open Web10.Radio.Database.Repositories

type BackgroundWorkerError =
    | DomainEventError of DomainEventError
    | RepositoryError of RepositoryError
    | UnknownEventType of value: string
    | InvalidPayload of eventType: string * message: string
    | PaymentStateRejected of paymentId: Guid
    | StateTransitionRejected of operation: string * id: Guid
    | UnexpectedException of operation: string * message: string

module BackgroundWorkerError =
    let toMessage error =
        match error with
        | DomainEventError domainError -> DomainEventError.toMessage domainError
        | RepositoryError repositoryError -> RepositoryError.toMessage repositoryError
        | UnknownEventType value -> sprintf "Unknown domain event type: %s." value
        | InvalidPayload(eventType, message) -> sprintf "Invalid payload for %s: %s" eventType message
        | PaymentStateRejected paymentId -> sprintf "Payment state rejected for payment %O." paymentId
        | StateTransitionRejected(operation, id) -> sprintf "State transition rejected: %s for %O." operation id
        | UnexpectedException(operation, message) -> sprintf "Unexpected background worker exception: %s: %s" operation message

type IDomainEventDispatcher =
    abstract member DispatchAsync: DomainEventEnvelope -> CancellationToken -> Task<Result<unit, BackgroundWorkerError>>

type IDomainEventPublisher =
    abstract member PublishDurableAsync: DomainEventEnvelope -> CancellationToken -> Task<Result<unit, BackgroundWorkerError>>

type IPlaybackQueueWorkflow =
    abstract member HandleAsync: DomainEventEnvelope -> CancellationToken -> Task<Result<unit, BackgroundWorkerError>>

type ILibraryScanWorkflow =
    abstract member HandleAsync: DomainEventEnvelope -> CancellationToken -> Task<Result<unit, BackgroundWorkerError>>
type PlaybackCompletion =
    | Succeeded
    | Failed of reason: string

type IPlaybackCompletionReporter =
    abstract member RenewLeaseAsync:
        queueItemId: Guid ->
        claimOwner: Guid ->
        claimAttempt: int ->
        CancellationToken ->
            Task<Result<bool, BackgroundWorkerError>>

    abstract member ReportAsync:
        queueItemId: Guid ->
        claimOwner: Guid ->
        claimAttempt: int ->
        outcome: PlaybackCompletion ->
        CancellationToken ->
            Task<Result<bool, BackgroundWorkerError>>



type ITelegramUpdateEventIngestor =
    abstract member TryIngestAsync:
        telegramUpdateId: int64 ->
        eventType: DomainEventType ->
        producer: string ->
        payloadJson: string ->
        CancellationToken ->
            Task<Result<bool, BackgroundWorkerError>>

module private JsonPayload =
    let objectWithStrings (fields: (string * string) list) =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))
        writer.WriteStartObject()

        for key, value in fields do
            writer.WriteString(key, value)

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(buffer.ToArray())

    let objectWithStringsAndRaw (fields: (string * string) list) (rawFields: (string * string) list) =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))
        writer.WriteStartObject()

        for key, value in fields do
            writer.WriteString(key, value)

        for key, rawValue in rawFields do
            writer.WritePropertyName(key)
            writer.WriteRawValue(rawValue)

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(buffer.ToArray())

    let parseObject eventType (payloadJson: string) =
        if String.IsNullOrWhiteSpace payloadJson then
            Error(InvalidPayload(eventType, "payload must be a JSON object."))
        else
            try
                use document = JsonDocument.Parse(payloadJson)

                if document.RootElement.ValueKind = JsonValueKind.Object then
                    Ok(document.RootElement.Clone())
                else
                    Error(InvalidPayload(eventType, "payload must be a JSON object."))
            with
            | :? JsonException as ex -> Error(InvalidPayload(eventType, ex.Message))

    let tryGetString (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String then
            let text = value.GetString()

            if String.IsNullOrWhiteSpace text then None else Some text
        else
            None

    let tryGetInt (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.Number then
            let mutable parsed = 0

            if value.TryGetInt32(&parsed) then Some parsed else None
        else
            None

    let tryGetInt64 (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.Number then
            let mutable parsed = 0L
            if value.TryGetInt64(&parsed) then Some parsed else None
        else
            None

    let tryGetObjectRaw (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.Object then
            Some(value.GetRawText())
        else
            None

module private EventPublishing =
    let publish
        (publisher: IDomainEventPublisher)
        (idGenerator: IIdGenerator)
        (clock: IClock)
        (eventType: DomainEventType)
        (producer: string)
        (correlationId: Guid option)
        (causationId: Guid option)
        (payloadJson: string)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, BackgroundWorkerError>> =
        taskResult {
            let! envelope =
                DomainEventEnvelope.create idGenerator clock eventType producer correlationId causationId payloadJson
                |> Result.mapError DomainEventError

            do! publisher.PublishDurableAsync envelope cancellationToken
        }

module private OutboxMapping =
    let toOutboxEvent (envelope: DomainEventEnvelope) =
        { Id = envelope.EventId
          EventType = DomainEventType.toString envelope.EventType
          OccurredAtUtc = envelope.OccurredAtUtc
          Producer = envelope.Producer
          CorrelationId = Some envelope.CorrelationId
          CausationId = envelope.CausationId
          PayloadJson = envelope.PayloadJson }

module private PaymentAgent =
    type private DonationPaidPayload =
        { PaymentId: Guid
          TelegramPaymentChargeId: string
          AmountStars: int
          Currency: string }

    let private parseDonationPaid payloadJson =
        result {
            let! root = JsonPayload.parseObject "DonationPaid" payloadJson
            let! paymentIdText = JsonPayload.tryGetString "paymentId" root |> Result.requireSome (InvalidPayload("DonationPaid", "paymentId is required."))
            let mutable paymentId = Guid.Empty
            do! Guid.TryParse(paymentIdText, &paymentId) |> Result.requireTrue (InvalidPayload("DonationPaid", "paymentId must be a UUID."))
            let! telegramPaymentChargeId =
                JsonPayload.tryGetString "telegramPaymentChargeId" root
                |> Result.requireSome (InvalidPayload("DonationPaid", "telegramPaymentChargeId is required."))
            let! amountStars = JsonPayload.tryGetInt "amountStars" root |> Result.requireSome (InvalidPayload("DonationPaid", "amountStars is required."))
            do! (amountStars > 0) |> Result.requireTrue (InvalidPayload("DonationPaid", "amountStars must be positive."))
            let! currency = JsonPayload.tryGetString "currency" root |> Result.requireSome (InvalidPayload("DonationPaid", "currency is required."))
            do! (currency = "XTR") |> Result.requireTrue (InvalidPayload("DonationPaid", "currency must be XTR."))

            return
                { PaymentId = paymentId
                  TelegramPaymentChargeId = telegramPaymentChargeId
                  AmountStars = amountStars
                  Currency = currency }
        }

    let handle
        (dataSource: NpgsqlDataSource)
        (clock: IClock)
        (envelope: DomainEventEnvelope)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, BackgroundWorkerError>> =
        taskResult {
            let! payload = parseDonationPaid envelope.PayloadJson
            let! wasMarkedPaid =
                PaymentRepository.markDonationPaid dataSource payload.PaymentId payload.TelegramPaymentChargeId payload.AmountStars payload.Currency clock.UtcNow cancellationToken
                |> TaskResult.mapError RepositoryError

            do! wasMarkedPaid |> Result.requireTrue (PaymentStateRejected payload.PaymentId)
            return ()
        }

module private StreamNodeAgent =
    let private parseFailurePayload eventType payloadJson =
        result {
            let! root = JsonPayload.parseObject eventType payloadJson
            return JsonPayload.tryGetString "reason" root
        }

    let private parseHeartbeatPayload eventType payloadJson =
        result {
            let! root = JsonPayload.parseObject eventType payloadJson
            let! status = JsonPayload.tryGetString "status" root |> Result.requireSome (InvalidPayload(eventType, "status is required."))
            let failureReason = JsonPayload.tryGetString "failureReason" root
            let metadataJson = JsonPayload.tryGetObjectRaw "metadata" root |> Option.defaultValue "{}"
            return status, failureReason, metadataJson
        }

    let recordFailure
        (dataSource: NpgsqlDataSource)
        (idGenerator: IIdGenerator)
        (clock: IClock)
        (state: StreamNodeHeartbeatState)
        (envelope: DomainEventEnvelope)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, BackgroundWorkerError>> =
        taskResult {
            let eventType = DomainEventType.toString envelope.EventType
            let! reason = parseFailurePayload eventType envelope.PayloadJson
            let heartbeatAtUtc = clock.UtcNow

            do!
                StreamNodeHeartbeatRepository.insertHeartbeat
                    dataSource
                    (idGenerator.NewId())
                    "Degraded"
                    heartbeatAtUtc
                    reason
                    envelope.PayloadJson
                    cancellationToken
                |> TaskResult.mapError RepositoryError

            state.RecordHeartbeat(heartbeatAtUtc, reason)
            return ()
        }

    let recordHeartbeat
        (dataSource: NpgsqlDataSource)
        (idGenerator: IIdGenerator)
        (clock: IClock)
        (state: StreamNodeHeartbeatState)
        (envelope: DomainEventEnvelope)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, BackgroundWorkerError>> =
        taskResult {
            let eventType = DomainEventType.toString envelope.EventType
            let! status, failureReason, metadataJson = parseHeartbeatPayload eventType envelope.PayloadJson
            let heartbeatAtUtc = clock.UtcNow

            do!
                StreamNodeHeartbeatRepository.insertHeartbeat
                    dataSource
                    (idGenerator.NewId())
                    status
                    heartbeatAtUtc
                    failureReason
                    metadataJson
                    cancellationToken
                |> TaskResult.mapError RepositoryError

            state.RecordHeartbeat(heartbeatAtUtc, failureReason)
            return ()
        }

module private PayloadValidation =
    let requireUuid eventType fieldName root =
        result {
            let! text =
                JsonPayload.tryGetString fieldName root
                |> Result.requireSome (InvalidPayload(eventType, sprintf "%s is required." fieldName))
            let mutable parsed = Guid.Empty
            do! Guid.TryParse(text, &parsed) |> Result.requireTrue (InvalidPayload(eventType, sprintf "%s must be a UUID." fieldName))
            return parsed
        }

    let requireString eventType fieldName root =
        JsonPayload.tryGetString fieldName root
        |> Result.requireSome (InvalidPayload(eventType, sprintf "%s is required." fieldName))

module private TelegramCommandAgent =
    let handle (dataSource: NpgsqlDataSource) (envelope: DomainEventEnvelope) cancellationToken =
        taskResult {
            let eventType = DomainEventType.toString envelope.EventType
            let! root = JsonPayload.parseObject eventType envelope.PayloadJson
            let telegramUserId = JsonPayload.tryGetInt64 "telegramUserId" root
            let displayName = JsonPayload.tryGetString "displayName" root

            match envelope.EventType with
            | TrackRequested ->
                let! query = PayloadValidation.requireString eventType "query" root

                let! _ =
                    TelegramCommandRepository.createTrackRequest
                        dataSource
                        { Id = envelope.EventId
                          TelegramUserId = telegramUserId
                          DisplayName = displayName
                          Query = query
                          RequestedAtUtc = envelope.OccurredAtUtc
                          CorrelationId = envelope.CorrelationId }
                        cancellationToken
                    |> TaskResult.mapError RepositoryError

                return ()
            | SayMessageSubmitted ->
                let! text = PayloadValidation.requireString eventType "text" root

                let! _ =
                    TelegramCommandRepository.createSayMessage
                        dataSource
                        { Id = envelope.EventId
                          TelegramUserId = telegramUserId
                          DisplayName = displayName |> Option.defaultValue "Telegram user"
                          Text = text
                          SubmittedAtUtc = envelope.OccurredAtUtc }
                        cancellationToken
                    |> TaskResult.mapError RepositoryError

                return ()
            | _ -> return! Error(InvalidPayload(eventType, "event is not handled by TelegramCommandAgent."))
        }

module private PlaybackQueueAgent =
    let handle (workflow: IPlaybackQueueWorkflow) envelope cancellationToken =
        workflow.HandleAsync envelope cancellationToken

module private LibraryScanAgent =
    let handle (workflow: ILibraryScanWorkflow) envelope cancellationToken =
        workflow.HandleAsync envelope cancellationToken

type private AgentWorkItem(envelope: DomainEventEnvelope, cancellationToken: CancellationToken) =
    let completion =
        TaskCompletionSource<Result<unit, BackgroundWorkerError>>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable state = 0

    member _.Envelope = envelope
    member _.CancellationToken = cancellationToken
    member _.Task = completion.Task
    member _.IsQueued = Volatile.Read(&state) = 0

    member _.TryStart() =
        Interlocked.CompareExchange(&state, 1, 0) = 0

    member _.TryCancel() =
        if Interlocked.CompareExchange(&state, 2, 0) = 0 then
            completion.TrySetCanceled(cancellationToken) |> ignore

    member _.Complete(result: Result<unit, BackgroundWorkerError>) =
        if Interlocked.Exchange(&state, 2) = 1 then
            completion.TrySetResult(result) |> ignore

    member _.CompleteCanceled() =
        if Interlocked.Exchange(&state, 2) = 1 then
            completion.TrySetCanceled(cancellationToken) |> ignore

type DomainEventDispatcher
    (
        dataSource: NpgsqlDataSource,
        idGenerator: IIdGenerator,
        clock: IClock,
        streamNodeState: StreamNodeHeartbeatState,
        playbackWorkflow: IPlaybackQueueWorkflow,
        libraryScanWorkflow: ILibraryScanWorkflow,
        applicationLifetime: IHostApplicationLifetime,
        logger: ILogger<DomainEventDispatcher>
    ) =
    let stoppingToken = applicationLifetime.ApplicationStopping

    let startAgent
        (operation: string)
        (handler: DomainEventEnvelope -> CancellationToken -> Task<Result<unit, BackgroundWorkerError>>)
        =
        MailboxProcessor.Start(
            (fun (inbox: MailboxProcessor<AgentWorkItem>) ->
                let rec loop () =
                    async {
                        try
                            let! work = inbox.Receive()

                            if work.TryStart() then
                                try
                                    let! result = handler work.Envelope work.CancellationToken |> Async.AwaitTask
                                    work.Complete(result)
                                with
                                | :? OperationCanceledException when work.CancellationToken.IsCancellationRequested ->
                                    work.CompleteCanceled()
                                | ex ->
                                    logger.LogError(
                                        ex,
                                        "Background agent {operation} failed for event {eventId} of type {eventType}.",
                                        operation,
                                        work.Envelope.EventId,
                                        DomainEventType.toString work.Envelope.EventType
                                    )

                                    work.Complete(Error(UnexpectedException(operation, ex.Message)))

                            return! loop ()
                        with
                        | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> return ()
                    }

                loop ()),
            cancellationToken = stoppingToken
        )

    let paymentAgent = startAgent "PaymentAgent" (PaymentAgent.handle dataSource clock)
    let playbackQueueAgent = startAgent "PlaybackQueueAgent" (PlaybackQueueAgent.handle playbackWorkflow)
    let libraryScanAgent = startAgent "LibraryScanAgent" (LibraryScanAgent.handle libraryScanWorkflow)
    let telegramCommandAgent = startAgent "TelegramCommandAgent" (TelegramCommandAgent.handle dataSource)

    let streamNodeAgent =
        startAgent
            "StreamNodeAgent"
            (fun envelope cancellationToken ->
                match envelope.EventType with
                | StreamNodeFailureDetected ->
                    StreamNodeAgent.recordFailure dataSource idGenerator clock streamNodeState envelope cancellationToken
                | StreamNodeHeartbeatReceived ->
                    StreamNodeAgent.recordHeartbeat dataSource idGenerator clock streamNodeState envelope cancellationToken
                | _ -> Task.FromResult(Ok()))

    let dispatchToAgent (agent: MailboxProcessor<AgentWorkItem>) (envelope: DomainEventEnvelope) (cancellationToken: CancellationToken) =
        task {
            use linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken)
            let work = new AgentWorkItem(envelope, linkedCancellation.Token)
            use registration = linkedCancellation.Token.Register(fun () -> work.TryCancel())

            if work.IsQueued then
                agent.Post(work)

            return! work.Task
        }

    interface IDomainEventDispatcher with
        member _.DispatchAsync envelope cancellationToken =
            match envelope.EventType with
            | DonationPaid -> dispatchToAgent paymentAgent envelope cancellationToken
            | PlaybackQueueItemClaimed
            | PlaybackStarted
            | PlaybackEnded -> dispatchToAgent playbackQueueAgent envelope cancellationToken
            | LibraryScanRequested
            | TrackDiscovered -> dispatchToAgent libraryScanAgent envelope cancellationToken
            | StreamNodeHeartbeatReceived
            | StreamNodeFailureDetected -> dispatchToAgent streamNodeAgent envelope cancellationToken
            | TrackRequested
            | SayMessageSubmitted -> dispatchToAgent telegramCommandAgent envelope cancellationToken
            | DonationInvoiceCreated
            | PaymentRefunded
            | TrackRequestMatched
            | SayMessageModerated
            | AdminGoalChanged
            | SocialLinkChanged -> Task.FromResult(Ok())

type DomainEventPublisher(dataSource: NpgsqlDataSource) =
    interface IDomainEventPublisher with
        member _.PublishDurableAsync envelope cancellationToken =
            OutboxEventRepository.append dataSource (OutboxMapping.toOutboxEvent envelope) cancellationToken
            |> TaskResult.mapError RepositoryError

type OutboxRelayHostedService
    (
        dataSource: NpgsqlDataSource,
        dispatcher: IDomainEventDispatcher,
        clock: IClock,
        idGenerator: IIdGenerator,
        logger: ILogger<OutboxRelayHostedService>
    ) as this =
    inherit BackgroundService()

    let claimOwner = idGenerator.NewId()

    let toEnvelope eventType (record: OutboxEventRecord) =
        { EventId = record.Id
          EventType = eventType
          OccurredAtUtc = record.OccurredAtUtc
          Producer = record.Producer
          CorrelationId = record.CorrelationId |> Option.defaultWith idGenerator.NewId
          CausationId = record.CausationId
          PayloadJson = record.PayloadJson }

    let bestEffortMarkFailed (record: OutboxEventRecord) nextAttemptAtUtc failedAtUtc cancellationToken =
        task {
            let! result =
                OutboxEventRepository.markFailed
                    dataSource
                    record.Id
                    record.ClaimOwner
                    record.ClaimAttempt
                    nextAttemptAtUtc
                    failedAtUtc
                    cancellationToken

            match result with
            | Ok true -> ()
            | Ok false ->
                logger.LogWarning(
                    "Outbox failure fence rejected event {eventId}, owner {claimOwner}, attempt {claimAttempt}.",
                    record.Id,
                    record.ClaimOwner,
                    record.ClaimAttempt
                )
            | Error repositoryError ->
                logger.LogError(
                    "Failed to mark relayed outbox event {eventId} as failed: {error}.",
                    record.Id,
                    RepositoryError.toMessage repositoryError
                )
        }

    member _.ProcessDueEventsOnceAsync(cancellationToken: CancellationToken) : Task<Result<int, BackgroundWorkerError>> =
        task {
            let! claimResult =
                OutboxEventRepository.tryClaimDueOrdered dataSource claimOwner clock.UtcNow 1 cancellationToken

            match claimResult with
            | Error repositoryError -> return Error(RepositoryError repositoryError)
            | Ok None -> return Ok 0
            | Ok(Some acquiredLease) ->
                use lease = acquiredLease

                match lease.Records with
                | [] -> return Ok 0
                | record :: _ ->
                    match DomainEventType.tryParse record.EventType with
                    | None ->
                        let nowUtc = clock.UtcNow
                        do! bestEffortMarkFailed record (nowUtc.AddHours(1.0)) nowUtc cancellationToken
                        return Error(UnknownEventType record.EventType)
                    | Some eventType ->
                        let envelope = toEnvelope eventType record
                        let! dispatchResult = dispatcher.DispatchAsync envelope cancellationToken

                        match dispatchResult with
                        | Error error ->
                            let nowUtc = clock.UtcNow
                            do! bestEffortMarkFailed record (nowUtc.AddSeconds(2.0)) nowUtc cancellationToken
                            return Error error
                        | Ok () ->
                            let! markResult =
                                OutboxEventRepository.markProcessed
                                    dataSource
                                    record.Id
                                    record.ClaimOwner
                                    record.ClaimAttempt
                                    clock.UtcNow
                                    cancellationToken

                            match markResult with
                            | Error repositoryError -> return Error(RepositoryError repositoryError)
                            | Ok false ->
                                return Error(StateTransitionRejected("mark fenced outbox event processed", record.Id))
                            | Ok true -> return Ok 1
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                while not stoppingToken.IsCancellationRequested do
                    let! result = this.ProcessDueEventsOnceAsync(stoppingToken)

                    match result with
                    | Ok 0 -> do! Task.Delay(TimeSpan.FromSeconds(1.0), stoppingToken)
                    | Ok _ -> ()
                    | Error error ->
                        logger.LogError("Outbox relay failed: {error}.", BackgroundWorkerError.toMessage error)
                        do! Task.Delay(TimeSpan.FromSeconds(1.0), stoppingToken)
            with
            | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
        }
        :> Task

type TelegramUpdateEventIngestor(dataSource: NpgsqlDataSource, idGenerator: IIdGenerator, clock: IClock) =
    interface ITelegramUpdateEventIngestor with
        member _.TryIngestAsync telegramUpdateId eventType producer payloadJson cancellationToken =
            task {
                match DomainEventEnvelope.create idGenerator clock eventType producer None None payloadJson |> Result.mapError DomainEventError with
                | Error error -> return Error error
                | Ok envelope ->
                    let inboxRecord =
                        { Id = idGenerator.NewId()
                          TelegramUpdateId = telegramUpdateId
                          EventType = DomainEventType.toString eventType
                          ReceivedAtUtc = clock.UtcNow
                          CorrelationId = Some envelope.CorrelationId
                          PayloadJson = envelope.PayloadJson }

                    return!
                        DatabaseSession.withTransactionResult
                            dataSource
                            (fun connection transaction cancellationToken ->
                                taskResult {
                                    let! wasInserted =
                                        TelegramUpdateInboxRepository.tryRecordInTransaction connection transaction inboxRecord cancellationToken
                                        |> TaskResult.mapError RepositoryError

                                    if wasInserted then
                                        do!
                                            OutboxEventRepository.appendInTransaction
                                                connection
                                                transaction
                                                (OutboxMapping.toOutboxEvent envelope)
                                                cancellationToken
                                            |> TaskResult.mapError RepositoryError

                                    return wasInserted
                                })
                            cancellationToken
            }

type LibraryScanHostedService
    (
        dataSource: NpgsqlDataSource,
        idGenerator: IIdGenerator,
        clock: IClock,
        options: Web10Options,
        s3ObjectEnumerator: IS3ObjectEnumerator,
        logger: ILogger<LibraryScanHostedService>
    ) as this =
    inherit BackgroundService()

    let claimOwner = idGenerator.NewId()
    let claimLease = TimeSpan.FromMinutes(5.0)

    let supportedExtensions =
        Set.ofList [ ".mp3"; ".flac"; ".wav"; ".ogg"; ".m4a"; ".aac"; ".opus" ]

    let configuredDefaultBackend () =
        { Id = None
          Name = "configured-default"
          Type =
            match options.Storage.Type with
            | Local -> "Local"
            | S3 -> "S3"
          LocalRoot = if String.IsNullOrWhiteSpace options.Storage.LocalRoot then None else Some options.Storage.LocalRoot
          S3Bucket = if String.IsNullOrWhiteSpace options.Storage.S3Bucket then None else Some options.Storage.S3Bucket }

    let contentTypeFor extension =
        match extension with
        | ".mp3" -> Some "audio/mpeg"
        | ".flac" -> Some "audio/flac"
        | ".wav" -> Some "audio/wav"
        | ".ogg" -> Some "audio/ogg"
        | ".m4a" -> Some "audio/mp4"
        | ".aac" -> Some "audio/aac"
        | ".opus" -> Some "audio/opus"
        | _ -> None

    let metadataFromPath (path: string) =
        let stem = Path.GetFileNameWithoutExtension(path)
        let separatorIndex = stem.IndexOf(" - ", StringComparison.Ordinal)

        if separatorIndex > 0 && separatorIndex + 3 < stem.Length then
            let artist = stem.Substring(0, separatorIndex).Trim()
            let title = stem.Substring(separatorIndex + 3).Trim()
            (if String.IsNullOrWhiteSpace artist then "Unknown Artist" else artist),
            (if String.IsNullOrWhiteSpace title then stem else title)
        else
            "Unknown Artist", stem

    let createLibraryEnvelope eventType payloadJson =
        DomainEventEnvelope.create idGenerator clock eventType "Web10.Radio.API.LibraryScan" None None payloadJson
        |> Result.mapError DomainEventError

    let appendEnvelope connection transaction envelope cancellationToken =
        OutboxEventRepository.appendInTransaction connection transaction (OutboxMapping.toOutboxEvent envelope) cancellationToken
        |> TaskResult.mapError RepositoryError


    let requireTransition operation id wasUpdated =
        wasUpdated |> Result.requireTrue (StateTransitionRejected(operation, id))

    let resolveStorageBackend (job: LibraryScanJobRecord) cancellationToken =
        taskResult {
            match job.StorageBackendId with
            | None -> return Some(configuredDefaultBackend())
            | Some storageBackendId ->
                return!
                    LibraryScanRepository.getStorageBackend dataSource storageBackendId cancellationToken
                    |> TaskResult.mapError RepositoryError
        }

    let renewClaim (job: LibraryScanJobRecord) cancellationToken =
        taskResult {
            let nowUtc = clock.UtcNow
            let! renewed =
                LibraryScanRepository.renewJobLease
                    dataSource
                    job.Id
                    job.ClaimOwner
                    job.ClaimAttempt
                    (nowUtc + claimLease)
                    nowUtc
                    cancellationToken
                |> TaskResult.mapError RepositoryError

            do! requireTransition "renew library scan lease" job.Id renewed
        }

    let insertDiscoveredFile
        (backend: StorageBackendRecord)
        storagePath
        cachePath
        isCached
        sizeBytes
        cancellationToken =
        taskResult {
            let nowUtc = clock.UtcNow
            let artist, title = metadataFromPath storagePath

            let discovered =
                { TrackId = idGenerator.NewId()
                  TrackFileId = idGenerator.NewId()
                  StorageBackendId = backend.Id
                  StoragePath = storagePath
                  CachePath = cachePath
                  IsCached = isCached
                  Title = title
                  Artist = artist
                  ContentType = contentTypeFor (Path.GetExtension(storagePath).ToLowerInvariant())
                  SizeBytes = sizeBytes
                  DiscoveredAtUtc = nowUtc }

            do!
                DatabaseSession.withTransactionResult
                    dataSource
                    (fun connection transaction cancellationToken ->
                        taskResult {
                            let! inserted =
                                LibraryScanRepository.insertDiscoveredTrackInTransaction connection transaction discovered cancellationToken
                                |> TaskResult.mapError RepositoryError

                            if inserted then
                                let payload =
                                    JsonPayload.objectWithStrings
                                        [ "trackId", string discovered.TrackId
                                          "trackFileId", string discovered.TrackFileId
                                          "storagePath", discovered.StoragePath ]

                                let! envelope = createLibraryEnvelope TrackDiscovered payload
                                do! appendEnvelope connection transaction envelope cancellationToken
                        })
                    cancellationToken
        }

    let processLocalBackend (job: LibraryScanJobRecord) (backend: StorageBackendRecord) localRoot cancellationToken =
        taskResult {
            do!
                Directory.Exists localRoot
                |> Result.requireTrue (UnexpectedException("LibraryScanHostedService", "local storage root not found"))

            let files =
                Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories)
                |> Seq.filter (fun path -> supportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))

            for filePath in files do
                do! renewClaim job cancellationToken
                let fileInfo = FileInfo(filePath)
                do! insertDiscoveredFile backend filePath (Some filePath) true (Some fileInfo.Length) cancellationToken
        }

    let processS3Backend (job: LibraryScanJobRecord) (backend: StorageBackendRecord) bucketName cancellationToken =
        taskResult {
            let visitPage (items: System.Collections.Generic.IReadOnlyList<S3ObjectDescriptor>) (pageCancellationToken: CancellationToken) =
                task {
                    let! pageResult =
                        taskResult {
                            do! renewClaim job pageCancellationToken

                            for item in items do
                                if supportedExtensions.Contains(Path.GetExtension(item.Key).ToLowerInvariant()) then
                                    do!
                                        insertDiscoveredFile
                                            backend
                                            item.Key
                                            None
                                            false
                                            (Some item.SizeBytes)
                                            pageCancellationToken
                        }

                    match pageResult with
                    | Ok () -> return ()
                    | Error error ->
                        return raise (InvalidOperationException(BackgroundWorkerError.toMessage error))
                }
                :> Task

            do! s3ObjectEnumerator.VisitPagesAsync(bucketName, visitPage, cancellationToken)
        }

    let completeClaim (job: LibraryScanJobRecord) cancellationToken =
        taskResult {
            let! completed =
                LibraryScanRepository.completeJob
                    dataSource
                    job.Id
                    job.ClaimOwner
                    job.ClaimAttempt
                    clock.UtcNow
                    cancellationToken
                |> TaskResult.mapError RepositoryError

            do! requireTransition "complete library scan" job.Id completed
        }

    let failClaim (job: LibraryScanJobRecord) reason cancellationToken =
        LibraryScanRepository.failJob
            dataSource
            job.Id
            job.ClaimOwner
            job.ClaimAttempt
            clock.UtcNow
            reason
            cancellationToken
        |> TaskResult.mapError RepositoryError

    let processClaimedJob (job: LibraryScanJobRecord) cancellationToken =
        taskResult {
            let! backend = resolveStorageBackend job cancellationToken
            let! backend =
                backend
                |> Result.requireSome (UnexpectedException("LibraryScanHostedService", "storage backend not found"))

            match backend.Type with
            | "Local" ->
                let! localRoot =
                    backend.LocalRoot
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Result.requireSome (UnexpectedException("LibraryScanHostedService", "local storage root not found"))

                do! processLocalBackend job backend localRoot cancellationToken
            | "S3" ->
                let! bucketName =
                    backend.S3Bucket
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Result.requireSome (UnexpectedException("LibraryScanHostedService", "S3 bucket is required"))

                do! processS3Backend job backend bucketName cancellationToken
            | value ->
                return!
                    Error(UnexpectedException("LibraryScanHostedService", sprintf "unsupported storage backend type: %s" value))

            do! completeClaim job cancellationToken
        }

    let runClaimedJob (job: LibraryScanJobRecord) cancellationToken =
        task {
            let! processingResult =
                task {
                    try
                        return! processClaimedJob job cancellationToken
                    with
                    | :? OperationCanceledException as ex ->
                        return Error(UnexpectedException("LibraryScanHostedService", ex.Message))
                    | ex -> return Error(UnexpectedException("LibraryScanHostedService", ex.Message))
                }

            match processingResult with
            | Ok () -> return Ok true
            | Error processingError ->
                let reason = BackgroundWorkerError.toMessage processingError
                let! failureResult = failClaim job reason CancellationToken.None

                match failureResult with
                | Ok _ -> return Error processingError
                | Error failureError -> return Error failureError
        }

    let claimAndProcess jobId cancellationToken =
        taskResult {
            let nowUtc = clock.UtcNow
            let leaseExpiresAtUtc = nowUtc + claimLease
            let! job =
                match jobId with
                | None ->
                    LibraryScanRepository.claimNextJob
                        dataSource
                        claimOwner
                        nowUtc
                        leaseExpiresAtUtc
                        cancellationToken
                | Some id ->
                    LibraryScanRepository.claimJobById
                        dataSource
                        id
                        claimOwner
                        nowUtc
                        leaseExpiresAtUtc
                        cancellationToken
                |> TaskResult.mapError RepositoryError

            match job with
            | None -> return false
            | Some claimed -> return! runClaimedJob claimed cancellationToken
        }

    member _.ProcessOneJobAsync(cancellationToken: CancellationToken) =
        claimAndProcess None cancellationToken

    member _.ProcessJobAsync(jobId: Guid, cancellationToken: CancellationToken) =
        claimAndProcess (Some jobId) cancellationToken

    member _.HandleEventAsync(envelope: DomainEventEnvelope, cancellationToken: CancellationToken) =
        taskResult {
            let eventType = DomainEventType.toString envelope.EventType
            let! root = JsonPayload.parseObject eventType envelope.PayloadJson

            match envelope.EventType with
            | LibraryScanRequested ->
                let! jobId = PayloadValidation.requireUuid eventType "libraryScanJobId" root
                let! _ = this.ProcessJobAsync(jobId, cancellationToken)
                return ()
            | TrackDiscovered ->
                let! _ = PayloadValidation.requireUuid eventType "trackId" root
                let! _ = PayloadValidation.requireUuid eventType "trackFileId" root
                let! _ = PayloadValidation.requireString eventType "storagePath" root
                return ()
            | _ -> return! Error(InvalidPayload(eventType, "event is not handled by LibraryScanAgent."))
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                while not stoppingToken.IsCancellationRequested do
                    let! result = this.ProcessOneJobAsync(stoppingToken)

                    match result with
                    | Ok true -> ()
                    | Ok false -> do! Task.Delay(TimeSpan.FromSeconds(5.0), stoppingToken)
                    | Error error ->
                        logger.LogError("Library scan worker failed: {error}.", BackgroundWorkerError.toMessage error)
                        do! Task.Delay(TimeSpan.FromSeconds(5.0), stoppingToken)
            with
            | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
        }
        :> Task

    interface ILibraryScanWorkflow with
        member _.HandleAsync envelope cancellationToken = this.HandleEventAsync(envelope, cancellationToken)

type PlaybackCompletionReporter
    (
        dataSource: NpgsqlDataSource,
        idGenerator: IIdGenerator,
        clock: IClock
    ) =
    let leaseDuration = TimeSpan.FromSeconds(30.0)

    let validateIdentity queueItemId claimOwner claimAttempt =
        result {
            do!
                (queueItemId <> Guid.Empty)
                |> Result.requireTrue (InvalidPayload("PlaybackEnded", "queueItemId must be a UUID."))

            do!
                (claimOwner <> Guid.Empty)
                |> Result.requireTrue (InvalidPayload("PlaybackEnded", "claimOwner must be a UUID."))

            do!
                (claimAttempt > 0)
                |> Result.requireTrue (InvalidPayload("PlaybackEnded", "claimAttempt must be positive."))
        }

    interface IPlaybackCompletionReporter with
        member _.RenewLeaseAsync queueItemId claimOwner claimAttempt cancellationToken =
            taskResult {
                do! validateIdentity queueItemId claimOwner claimAttempt
                let nowUtc = clock.UtcNow

                return!
                    PlaybackQueueRepository.renewPlayingLease
                        dataSource
                        queueItemId
                        claimOwner
                        claimAttempt
                        nowUtc
                        (nowUtc + leaseDuration)
                        cancellationToken
                    |> TaskResult.mapError RepositoryError
            }

        member _.ReportAsync queueItemId claimOwner claimAttempt outcome cancellationToken =
            taskResult {
                do! validateIdentity queueItemId claimOwner claimAttempt

                let! status, failureReason =
                    match outcome with
                    | Succeeded -> Ok("played", None)
                    | Failed reason when not (String.IsNullOrWhiteSpace reason) ->
                        Ok("failed", Some(reason.Trim()))
                    | Failed _ ->
                        Error(InvalidPayload("PlaybackEnded", "failure reason is required."))

                let payloadJson =
                    JsonPayload.objectWithStringsAndRaw
                        ([ "queueItemId", string queueItemId
                           "claimOwner", string claimOwner
                           "status", status ]
                         @ (failureReason
                            |> Option.map (fun reason -> [ "failureReason", reason ])
                            |> Option.defaultValue []))
                        [ "claimAttempt", string claimAttempt ]

                let! envelope =
                    DomainEventEnvelope.create
                        idGenerator
                        clock
                        PlaybackEnded
                        "Web10.Radio.StreamNode"
                        None
                        None
                        payloadJson
                    |> Result.mapError DomainEventError

                return!
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction cancellationToken ->
                            taskResult {
                                let finishedAtUtc = clock.UtcNow
                                let! active =
                                    PlaybackQueueRepository.lockOwnedPlayingClaimInTransaction
                                        connection
                                        transaction
                                        queueItemId
                                        claimOwner
                                        claimAttempt
                                        finishedAtUtc
                                        cancellationToken
                                    |> TaskResult.mapError RepositoryError

                                if not active then
                                    return false
                                else
                                    let! transitioned =
                                        match outcome with
                                        | Succeeded ->
                                            PlaybackQueueRepository.markPlayedInTransaction
                                                connection
                                                transaction
                                                queueItemId
                                                claimOwner
                                                claimAttempt
                                                finishedAtUtc
                                                cancellationToken
                                        | Failed _ ->
                                            PlaybackQueueRepository.markFailedInTransaction
                                                connection
                                                transaction
                                                queueItemId
                                                claimOwner
                                                claimAttempt
                                                finishedAtUtc
                                                failureReason.Value
                                                cancellationToken
                                        |> TaskResult.mapError RepositoryError

                                    do!
                                        transitioned
                                        |> Result.requireTrue
                                            (StateTransitionRejected("complete active playback claim", queueItemId))

                                    do!
                                        OutboxEventRepository.appendInTransaction
                                            connection
                                            transaction
                                            (OutboxMapping.toOutboxEvent envelope)
                                            cancellationToken
                                        |> TaskResult.mapError RepositoryError

                                    return true
                            })
                        cancellationToken
            }

type PlaybackProgramHostedService
    (
        dataSource: NpgsqlDataSource,
        idGenerator: IIdGenerator,
        clock: IClock,
        logger: ILogger<PlaybackProgramHostedService>
    ) as this =
    inherit BackgroundService()

    let claimOwner = idGenerator.NewId()
    let claimLease = TimeSpan.FromSeconds(30.0)

    let createPlaybackEnvelope eventType payloadJson =
        DomainEventEnvelope.create idGenerator clock eventType "Web10.Radio.API.PlaybackProgram" None None payloadJson
        |> Result.mapError DomainEventError

    let appendEnvelope connection transaction envelope cancellationToken =
        OutboxEventRepository.appendInTransaction connection transaction (OutboxMapping.toOutboxEvent envelope) cancellationToken
        |> TaskResult.mapError RepositoryError

    let claimIdentityPayload queueItemId owner attempt extraStrings =
        JsonPayload.objectWithStringsAndRaw
            (("queueItemId", string queueItemId) :: ("claimOwner", string owner) :: extraStrings)
            [ "claimAttempt", string attempt ]

    let claimedPayload (claimed: ClaimedPlaybackQueueItem) =
        claimIdentityPayload claimed.QueueItemId claimed.ClaimOwner claimed.ClaimAttempt []

    let playbackStartedPayload queueItemId owner attempt trackId cachePath =
        claimIdentityPayload
            queueItemId
            owner
            attempt
            [ "trackId", string trackId
              "cachePath", cachePath ]

    let playbackEndedPayload queueItemId owner attempt status failureReason =
        let fields =
            [ "status", status ]
            @ (failureReason |> Option.map (fun reason -> [ "failureReason", reason ]) |> Option.defaultValue [])

        claimIdentityPayload queueItemId owner attempt fields

    let streamFailurePayload queueItemId reason =
        JsonPayload.objectWithStrings
            [ "queueItemId", string queueItemId
              "reason", reason ]

    let parseClaimIdentity eventType root =
        result {
            let! queueItemId = PayloadValidation.requireUuid eventType "queueItemId" root
            let! owner = PayloadValidation.requireUuid eventType "claimOwner" root
            let! attempt =
                JsonPayload.tryGetInt "claimAttempt" root
                |> Result.requireSome (InvalidPayload(eventType, "claimAttempt is required."))

            do! (attempt > 0) |> Result.requireTrue (InvalidPayload(eventType, "claimAttempt must be positive."))
            return queueItemId, owner, attempt
        }

    let appendFailureEvents connection transaction queueItemId owner attempt failureReason cancellationToken =
        taskResult {
            let! playbackEndedEnvelope =
                playbackEndedPayload queueItemId owner attempt "failed" (Some failureReason)
                |> createPlaybackEnvelope PlaybackEnded

            let! streamFailureEnvelope =
                streamFailurePayload queueItemId failureReason
                |> createPlaybackEnvelope StreamNodeFailureDetected

            do! appendEnvelope connection transaction playbackEndedEnvelope cancellationToken
            do! appendEnvelope connection transaction streamFailureEnvelope cancellationToken
        }

    let failOwnedClaim connection transaction queueItemId owner attempt failureReason cancellationToken =
        taskResult {
            let! failed =
                PlaybackQueueRepository.markFailedInTransaction
                    connection
                    transaction
                    queueItemId
                    owner
                    attempt
                    clock.UtcNow
                    failureReason
                    cancellationToken
                |> TaskResult.mapError RepositoryError

            if failed then
                do! appendFailureEvents connection transaction queueItemId owner attempt failureReason cancellationToken
        }

    let handleClaimedEvent queueItemId owner attempt cancellationToken =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                taskResult {
                    let nowUtc = clock.UtcNow
                    let! claimed =
                        PlaybackQueueRepository.getOwnedClaimInTransaction
                            connection
                            transaction
                            queueItemId
                            owner
                            attempt
                            nowUtc
                            cancellationToken
                        |> TaskResult.mapError RepositoryError

                    match claimed with
                    | None -> return ()
                    | Some claimed ->
                        match claimed.TrackId with
                        | None ->
                            do!
                                failOwnedClaim
                                    connection
                                    transaction
                                    queueItemId
                                    owner
                                    attempt
                                    "playback queue item has no track"
                                    cancellationToken
                        | Some trackId ->
                            let! cachePath =
                                PlaybackQueueRepository.findCachedTrackFileInTransaction
                                    connection
                                    transaction
                                    trackId
                                    cancellationToken
                                |> TaskResult.mapError RepositoryError

                            match cachePath with
                            | None ->
                                do!
                                    failOwnedClaim
                                        connection
                                        transaction
                                        queueItemId
                                        owner
                                        attempt
                                        "cache path unavailable"
                                        cancellationToken
                            | Some cachePath ->
                                let! playing =
                                    PlaybackQueueRepository.markPlayingInTransaction
                                        connection
                                        transaction
                                        queueItemId
                                        owner
                                        attempt
                                        nowUtc
                                        (nowUtc + claimLease)
                                        cancellationToken
                                    |> TaskResult.mapError RepositoryError

                                if playing then
                                    let! envelope =
                                        playbackStartedPayload queueItemId owner attempt trackId cachePath
                                        |> createPlaybackEnvelope PlaybackStarted

                                    do! appendEnvelope connection transaction envelope cancellationToken
                })
            cancellationToken


    let handleEndedEvent queueItemId owner attempt status failureReason cancellationToken =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                taskResult {
                    match status with
                    | "played" ->
                        let! _ =
                            PlaybackQueueRepository.markPlayedInTransaction
                                connection
                                transaction
                                queueItemId
                                owner
                                attempt
                                clock.UtcNow
                                cancellationToken
                            |> TaskResult.mapError RepositoryError

                        return ()
                    | "failed" ->
                        let! reason =
                            failureReason
                            |> Result.requireSome (InvalidPayload("PlaybackEnded", "failureReason is required."))

                        let! _ =
                            PlaybackQueueRepository.markFailedInTransaction
                                connection
                                transaction
                                queueItemId
                                owner
                                attempt
                                clock.UtcNow
                                reason
                                cancellationToken
                            |> TaskResult.mapError RepositoryError

                        return ()
                    | value ->
                        return!
                            Error(InvalidPayload("PlaybackEnded", sprintf "status must be played or failed, got %s." value))
                })
            cancellationToken

    member _.ProcessOneQueueItemAsync(cancellationToken: CancellationToken) : Task<Result<bool, BackgroundWorkerError>> =
        taskResult {
            let nowUtc = clock.UtcNow

            return!
                DatabaseSession.withTransactionResult
                    dataSource
                    (fun connection transaction cancellationToken ->
                        taskResult {
                            let! claimed =
                                PlaybackQueueRepository.claimNextDetailedInTransaction
                                    connection
                                    transaction
                                    claimOwner
                                    nowUtc
                                    (nowUtc + claimLease)
                                    cancellationToken
                                |> TaskResult.mapError RepositoryError

                            match claimed with
                            | None -> return false
                            | Some claimed ->
                                let! envelope = claimed |> claimedPayload |> createPlaybackEnvelope PlaybackQueueItemClaimed
                                do! appendEnvelope connection transaction envelope cancellationToken
                                return true
                        })
                    cancellationToken
        }

    member _.HandleEventAsync(envelope: DomainEventEnvelope, cancellationToken: CancellationToken) =
        taskResult {
            let eventType = DomainEventType.toString envelope.EventType
            let! root = JsonPayload.parseObject eventType envelope.PayloadJson
            let! queueItemId, owner, attempt = parseClaimIdentity eventType root

            match envelope.EventType with
            | PlaybackQueueItemClaimed ->
                do! handleClaimedEvent queueItemId owner attempt cancellationToken
            | PlaybackStarted ->
                let! _ = PayloadValidation.requireUuid eventType "trackId" root
                let! _ = PayloadValidation.requireString eventType "cachePath" root
                return ()
            | PlaybackEnded ->
                let! status = PayloadValidation.requireString eventType "status" root
                let! failureReason =
                    match status with
                    | "played" -> Ok None
                    | "failed" ->
                        PayloadValidation.requireString eventType "failureReason" root
                        |> Result.map Some
                    | value ->
                        Error(InvalidPayload(eventType, sprintf "status must be played or failed, got %s." value))

                do! handleEndedEvent queueItemId owner attempt status failureReason cancellationToken
            | _ -> return! Error(InvalidPayload(eventType, "event is not handled by PlaybackQueueAgent."))
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                while not stoppingToken.IsCancellationRequested do
                    let! result = this.ProcessOneQueueItemAsync(stoppingToken)

                    match result with
                    | Ok true -> ()
                    | Ok false -> do! Task.Delay(TimeSpan.FromSeconds(2.0), stoppingToken)
                    | Error error ->
                        logger.LogError("Playback program worker failed: {error}.", BackgroundWorkerError.toMessage error)
                        do! Task.Delay(TimeSpan.FromSeconds(2.0), stoppingToken)
            with
            | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
        }
        :> Task

    interface IPlaybackQueueWorkflow with
        member _.HandleAsync envelope cancellationToken = this.HandleEventAsync(envelope, cancellationToken)

module BackgroundWorkerComposition =
    let addBackgroundWorkers (options: Web10Options) (services: IServiceCollection) : IServiceCollection =
        services.AddSingleton<Web10Options>(options) |> ignore
        services.AddSingleton<LibraryScanHostedService>() |> ignore
        services.AddSingleton<ILibraryScanWorkflow>(fun provider ->
            provider.GetRequiredService<LibraryScanHostedService>() :> ILibraryScanWorkflow)
        |> ignore
        services.AddSingleton<PlaybackProgramHostedService>() |> ignore
        services.AddSingleton<IPlaybackQueueWorkflow>(fun provider ->
            provider.GetRequiredService<PlaybackProgramHostedService>() :> IPlaybackQueueWorkflow)
        |> ignore
        services.AddSingleton<IDomainEventDispatcher, DomainEventDispatcher>() |> ignore
        services.AddSingleton<IDomainEventPublisher, DomainEventPublisher>() |> ignore
        services.AddSingleton<IPlaybackCompletionReporter, PlaybackCompletionReporter>() |> ignore
        services.AddSingleton<ITelegramUpdateEventIngestor, TelegramUpdateEventIngestor>() |> ignore
        services.AddHostedService<OutboxRelayHostedService>() |> ignore
        services.AddHostedService(fun provider -> provider.GetRequiredService<LibraryScanHostedService>()) |> ignore
        services.AddHostedService(fun provider -> provider.GetRequiredService<PlaybackProgramHostedService>()) |> ignore
        services
