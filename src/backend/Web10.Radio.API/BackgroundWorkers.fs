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
    abstract member DispatchPersistedAsync: DomainEventEnvelope -> CancellationToken -> Task<Result<unit, BackgroundWorkerError>>

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

module private PlaybackQueueAgent =
    let handle (envelope: DomainEventEnvelope) (_cancellationToken: CancellationToken) : Task<Result<unit, BackgroundWorkerError>> =
        taskResult {
            let eventType = DomainEventType.toString envelope.EventType
            let! root = JsonPayload.parseObject eventType envelope.PayloadJson

            match envelope.EventType with
            | PlaybackQueueItemClaimed ->
                let! _ = PayloadValidation.requireUuid eventType "queueItemId" root
                return ()
            | PlaybackStarted ->
                let! _ = PayloadValidation.requireUuid eventType "queueItemId" root
                let! _ = PayloadValidation.requireUuid eventType "trackId" root
                let! _ = PayloadValidation.requireString eventType "cachePath" root
                return ()
            | PlaybackEnded ->
                let! _ = PayloadValidation.requireUuid eventType "queueItemId" root
                let! status = PayloadValidation.requireString eventType "status" root

                match status with
                | "played" -> return ()
                | "failed" ->
                    let! _ = PayloadValidation.requireString eventType "failureReason" root
                    return ()
                | value -> return! Error(InvalidPayload(eventType, sprintf "status must be played or failed, got %s." value))
            | _ -> return! Error(InvalidPayload(eventType, "event is not handled by PlaybackQueueAgent."))
        }

module private LibraryScanAgent =
    let handle (envelope: DomainEventEnvelope) (_cancellationToken: CancellationToken) : Task<Result<unit, BackgroundWorkerError>> =
        taskResult {
            let eventType = DomainEventType.toString envelope.EventType
            let! root = JsonPayload.parseObject eventType envelope.PayloadJson

            match envelope.EventType with
            | LibraryScanRequested ->
                let! _ = PayloadValidation.requireUuid eventType "libraryScanJobId" root
                return ()
            | TrackDiscovered ->
                let! _ = PayloadValidation.requireUuid eventType "trackId" root
                let! _ = PayloadValidation.requireUuid eventType "trackFileId" root
                let! _ = PayloadValidation.requireString eventType "storagePath" root
                return ()
            | _ -> return! Error(InvalidPayload(eventType, "event is not handled by LibraryScanAgent."))
        }

type private AgentMessage =
    | Handle of DomainEventEnvelope * CancellationToken * AsyncReplyChannel<Result<unit, BackgroundWorkerError>>

type DomainEventDispatcher
    (
        dataSource: NpgsqlDataSource,
        idGenerator: IIdGenerator,
        clock: IClock,
        streamNodeState: StreamNodeHeartbeatState,
        logger: ILogger<DomainEventDispatcher>
    ) =
    let startAgent operation handler =
        MailboxProcessor.Start(fun inbox ->
            let rec loop () =
                async {
                    let! message = inbox.Receive()

                    match message with
                    | Handle(envelope, cancellationToken, reply) ->
                        let! result =
                            async {
                                try
                                    return! handler envelope cancellationToken |> Async.AwaitTask
                                with ex ->
                                    logger.LogError(ex, "Background agent {operation} failed for event {eventId} of type {eventType}.", operation, envelope.EventId, DomainEventType.toString envelope.EventType)
                                    return Error(UnexpectedException(operation, ex.Message))
                            }

                        reply.Reply(result)

                    return! loop ()
                }

            loop ())

    let paymentAgent = startAgent "PaymentAgent" (PaymentAgent.handle dataSource clock)
    let playbackQueueAgent = startAgent "PlaybackQueueAgent" PlaybackQueueAgent.handle
    let libraryScanAgent = startAgent "LibraryScanAgent" LibraryScanAgent.handle

    let streamNodeAgent =
        startAgent
            "StreamNodeAgent"
            (fun envelope cancellationToken ->
                match envelope.EventType with
                | StreamNodeFailureDetected -> StreamNodeAgent.recordFailure dataSource idGenerator clock streamNodeState envelope cancellationToken
                | StreamNodeHeartbeatReceived -> StreamNodeAgent.recordHeartbeat dataSource idGenerator clock streamNodeState envelope cancellationToken
                | _ -> Task.FromResult(Ok()))

    let dispatchToAgent (agent: MailboxProcessor<AgentMessage>) (envelope: DomainEventEnvelope) (cancellationToken: CancellationToken) =
        agent.PostAndAsyncReply(fun reply -> Handle(envelope, cancellationToken, reply))
        |> fun workflow -> Async.StartAsTask(workflow, cancellationToken = cancellationToken)

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
            | DonationInvoiceCreated
            | PaymentRefunded
            | TrackRequested
            | TrackRequestMatched
            | SayMessageSubmitted
            | SayMessageModerated
            | AdminGoalChanged
            | SocialLinkChanged -> Task.FromResult(Ok())

type DomainEventPublisher(dataSource: NpgsqlDataSource, dispatcher: IDomainEventDispatcher, clock: IClock, logger: ILogger<DomainEventPublisher>) =
    let bestEffortMarkFailed eventId nextAttemptAtUtc failedAtUtc cancellationToken =
        task {
            let! result = OutboxEventRepository.markFailed dataSource eventId nextAttemptAtUtc failedAtUtc cancellationToken

            match result with
            | Ok () -> ()
            | Error repositoryError ->
                logger.LogError(
                    "Failed to mark outbox event {eventId} as failed: {error}.",
                    eventId,
                    RepositoryError.toMessage repositoryError
                )
        }

    interface IDomainEventPublisher with
        member _.DispatchPersistedAsync envelope cancellationToken =
            task {
                try
                    let! dispatchResult = dispatcher.DispatchAsync envelope cancellationToken

                    match dispatchResult with
                    | Ok () ->
                        let! markProcessedResult = OutboxEventRepository.markProcessed dataSource envelope.EventId clock.UtcNow cancellationToken
                        return markProcessedResult |> Result.mapError RepositoryError
                    | Error error ->
                        do! bestEffortMarkFailed envelope.EventId (clock.UtcNow.AddSeconds(2.0)) clock.UtcNow cancellationToken
                        return Error error
                with ex ->
                    do! bestEffortMarkFailed envelope.EventId (clock.UtcNow.AddSeconds(2.0)) clock.UtcNow cancellationToken
                    return Error(UnexpectedException("DomainEventPublisher.DispatchPersistedAsync", ex.Message))
            }

        member this.PublishDurableAsync envelope cancellationToken =
            task {
                let! appendResult = OutboxEventRepository.append dataSource (OutboxMapping.toOutboxEvent envelope) cancellationToken

                match appendResult with
                | Error repositoryError -> return Error(RepositoryError repositoryError)
                | Ok () -> return! (this :> IDomainEventPublisher).DispatchPersistedAsync envelope cancellationToken
            }

type OutboxRelayHostedService(serviceScopeFactory: IServiceScopeFactory, logger: ILogger<OutboxRelayHostedService>) as this =
    inherit BackgroundService()

    let toEnvelope (idGenerator: IIdGenerator) eventType (record: OutboxEventRecord) =
        { EventId = record.Id
          EventType = eventType
          OccurredAtUtc = record.OccurredAtUtc
          Producer = record.Producer
          CorrelationId = record.CorrelationId |> Option.defaultWith idGenerator.NewId
          CausationId = record.CausationId
          PayloadJson = record.PayloadJson }

    let bestEffortMarkFailed (dataSource: NpgsqlDataSource) eventId nextAttemptAtUtc failedAtUtc cancellationToken =
        task {
            let! result = OutboxEventRepository.markFailed dataSource eventId nextAttemptAtUtc failedAtUtc cancellationToken

            match result with
            | Ok () -> ()
            | Error repositoryError ->
                logger.LogError(
                    "Failed to mark relayed outbox event {eventId} as failed: {error}.",
                    eventId,
                    RepositoryError.toMessage repositoryError
                )
        }

    member _.ProcessDueEventsOnceAsync(cancellationToken: CancellationToken) : Task<Result<int, BackgroundWorkerError>> =
        task {
            use scope = serviceScopeFactory.CreateScope()
            let dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>()
            let dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>()
            let clock = scope.ServiceProvider.GetRequiredService<IClock>()
            let idGenerator = scope.ServiceProvider.GetRequiredService<IIdGenerator>()
            let! claimResult = OutboxEventRepository.claimDue dataSource clock.UtcNow 20 cancellationToken

            match claimResult with
            | Error repositoryError -> return Error(RepositoryError repositoryError)
            | Ok records ->
                let mutable firstError: BackgroundWorkerError option = None
                let rememberFirst error =
                    match firstError with
                    | None -> firstError <- Some error
                    | Some _ -> ()

                for record in records do
                    match DomainEventType.tryParse record.EventType with
                    | None ->
                        let error = UnknownEventType record.EventType
                        do! bestEffortMarkFailed dataSource record.Id (clock.UtcNow.AddHours(1.0)) clock.UtcNow cancellationToken
                        rememberFirst error
                    | Some eventType ->
                        let envelope = toEnvelope idGenerator eventType record
                        let! dispatchResult = dispatcher.DispatchAsync envelope cancellationToken

                        match dispatchResult with
                        | Ok () ->
                            let! markResult = OutboxEventRepository.markProcessed dataSource record.Id clock.UtcNow cancellationToken

                            match markResult with
                            | Ok () -> ()
                            | Error repositoryError -> rememberFirst (RepositoryError repositoryError)
                        | Error error ->
                            do! bestEffortMarkFailed dataSource record.Id (clock.UtcNow.AddSeconds(2.0)) clock.UtcNow cancellationToken
                            rememberFirst error

                match firstError with
                | None -> return Ok(List.length records)
                | Some error -> return Error error
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                let! result = this.ProcessDueEventsOnceAsync(stoppingToken)

                match result with
                | Ok _ -> ()
                | Error error -> logger.LogError("Outbox relay failed: {error}.", BackgroundWorkerError.toMessage error)

                do! Task.Delay(TimeSpan.FromSeconds(1.0), stoppingToken)
        }
        :> Task

type TelegramUpdateEventIngestor(dataSource: NpgsqlDataSource, idGenerator: IIdGenerator, clock: IClock, publisher: IDomainEventPublisher, logger: ILogger<TelegramUpdateEventIngestor>) =
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

                    let! transactionResult =
                        DatabaseSession.withTransactionResult
                            dataSource
                            (fun connection transaction cancellationToken ->
                                taskResult {
                                    let! wasInserted =
                                        TelegramUpdateInboxRepository.tryRecordInTransaction connection transaction inboxRecord cancellationToken
                                        |> TaskResult.mapError RepositoryError

                                    if wasInserted then
                                        do!
                                            OutboxEventRepository.appendInTransaction connection transaction (OutboxMapping.toOutboxEvent envelope) cancellationToken
                                            |> TaskResult.mapError RepositoryError

                                    return wasInserted
                                })
                            cancellationToken

                    match transactionResult with
                    | Error error -> return Error error
                    | Ok false -> return Ok false
                    | Ok true ->
                        let! dispatchResult = publisher.DispatchPersistedAsync envelope cancellationToken

                        match dispatchResult with
                        | Ok () -> return Ok true
                        | Error error -> return Error error
            }

type LibraryScanHostedService
    (
        dataSource: NpgsqlDataSource,
        publisher: IDomainEventPublisher,
        idGenerator: IIdGenerator,
        clock: IClock,
        options: Web10Options,
        logger: ILogger<LibraryScanHostedService>
    ) as this =
    inherit BackgroundService()

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

    let dispatchPersistedEnvelope envelope cancellationToken =
        publisher.DispatchPersistedAsync envelope cancellationToken

    let failurePayload jobId reason =
        JsonPayload.objectWithStrings
            [ "reason", reason
              "libraryScanJobId", string jobId ]

    let failJobAndPublish jobId reason cancellationToken =
        taskResult {
            let nowUtc = clock.UtcNow
            let! envelope =
                DatabaseSession.withTransactionResult
                    dataSource
                    (fun connection transaction cancellationToken ->
                        taskResult {
                            do!
                                LibraryScanRepository.failJobInTransaction connection transaction jobId nowUtc reason cancellationToken
                                |> TaskResult.mapError RepositoryError
                            let! envelope = failurePayload jobId reason |> createLibraryEnvelope StreamNodeFailureDetected
                            do! appendEnvelope connection transaction envelope cancellationToken
                            return envelope
                        })
                    cancellationToken

            do! dispatchPersistedEnvelope envelope cancellationToken
            return true
        }

    let resolveStorageBackend (job: LibraryScanJobRecord) cancellationToken =
        taskResult {
            match job.StorageBackendId with
            | None -> return Some(configuredDefaultBackend())
            | Some storageBackendId ->
                let! backend = LibraryScanRepository.getStorageBackend dataSource storageBackendId cancellationToken |> TaskResult.mapError RepositoryError
                return backend
        }

    let insertDiscoveredFile (backend: StorageBackendRecord) (filePath: string) cancellationToken =
        taskResult {
            let nowUtc = clock.UtcNow
            let artist, title = metadataFromPath filePath
            let fileInfo = FileInfo(filePath)

            let discovered =
                { TrackId = idGenerator.NewId()
                  TrackFileId = idGenerator.NewId()
                  StorageBackendId = backend.Id
                  StoragePath = filePath
                  CachePath = Some filePath
                  IsCached = true
                  Title = title
                  Artist = artist
                  ContentType = contentTypeFor (Path.GetExtension(filePath).ToLowerInvariant())
                  SizeBytes = Some fileInfo.Length
                  DiscoveredAtUtc = nowUtc }

            let! envelope =
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
                                return Some envelope
                            else
                                return None
                        })
                    cancellationToken

            match envelope with
            | None -> return ()
            | Some envelope ->
                do! dispatchPersistedEnvelope envelope cancellationToken
                return ()
        }

    let processLocalBackend (job: LibraryScanJobRecord) (backend: StorageBackendRecord) (localRoot: string) cancellationToken =
        taskResult {
            if not (Directory.Exists localRoot) then
                return! failJobAndPublish job.Id "local storage root not found" cancellationToken
            else
                let filesResult =
                    try
                        Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories)
                        |> Seq.filter (fun path -> supportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                        |> Seq.toList
                        |> Ok
                    with ex ->
                        Error("local storage enumeration failed: " + ex.Message)

                match filesResult with
                | Error reason -> return! failJobAndPublish job.Id reason cancellationToken
                | Ok files ->
                    for filePath in files do
                        do! insertDiscoveredFile backend filePath cancellationToken

                    do! LibraryScanRepository.completeJob dataSource job.Id clock.UtcNow cancellationToken |> TaskResult.mapError RepositoryError
                    return true
        }

    member _.ProcessOneJobAsync(cancellationToken: CancellationToken) : Task<Result<bool, BackgroundWorkerError>> =
        taskResult {
            let! job = LibraryScanRepository.claimNextJob dataSource clock.UtcNow cancellationToken |> TaskResult.mapError RepositoryError

            match job with
            | None -> return false
            | Some job ->
                let! backend = resolveStorageBackend job cancellationToken

                match backend with
                | None -> return! failJobAndPublish job.Id "storage backend not found" cancellationToken
                | Some backend ->
                    match backend.Type with
                    | "Local" ->
                        match backend.LocalRoot with
                        | Some localRoot when not (String.IsNullOrWhiteSpace localRoot) -> return! processLocalBackend job backend localRoot cancellationToken
                        | _ -> return! failJobAndPublish job.Id "local storage root not found" cancellationToken
                    | "S3" ->
                        return!
                            failJobAndPublish
                                job.Id
                                "S3 library scan requires credentials/config not defined in SPEC v0"
                                cancellationToken
                    | value -> return! failJobAndPublish job.Id (sprintf "unsupported storage backend type: %s" value) cancellationToken
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                let! result = this.ProcessOneJobAsync(stoppingToken)

                match result with
                | Ok true -> ()
                | Ok false -> do! Task.Delay(TimeSpan.FromSeconds(5.0), stoppingToken)
                | Error error ->
                    logger.LogError("Library scan worker failed: {error}.", BackgroundWorkerError.toMessage error)
                    do! Task.Delay(TimeSpan.FromSeconds(5.0), stoppingToken)
        }
        :> Task

type PlaybackProgramHostedService
    (
        dataSource: NpgsqlDataSource,
        publisher: IDomainEventPublisher,
        idGenerator: IIdGenerator,
        clock: IClock,
        logger: ILogger<PlaybackProgramHostedService>
    ) as this =
    inherit BackgroundService()

    let createPlaybackEnvelope eventType payloadJson =
        DomainEventEnvelope.create idGenerator clock eventType "Web10.Radio.API.PlaybackProgram" None None payloadJson
        |> Result.mapError DomainEventError

    let appendEnvelope connection transaction envelope cancellationToken =
        OutboxEventRepository.appendInTransaction connection transaction (OutboxMapping.toOutboxEvent envelope) cancellationToken
        |> TaskResult.mapError RepositoryError

    let requireTransition operation queueItemId wasUpdated =
        wasUpdated |> Result.requireTrue (StateTransitionRejected(operation, queueItemId))

    let claimedPayload queueItemId =
        JsonPayload.objectWithStrings [ "queueItemId", string queueItemId ]

    let playbackFailedPayload queueItemId failureReason =
        JsonPayload.objectWithStrings
            [ "queueItemId", string queueItemId
              "status", "failed"
              "failureReason", failureReason ]

    let streamFailurePayload queueItemId reason =
        JsonPayload.objectWithStrings
            [ "queueItemId", string queueItemId
              "reason", reason ]

    let playbackStartedPayload queueItemId trackId cachePath =
        JsonPayload.objectWithStrings
            [ "queueItemId", string queueItemId
              "trackId", string trackId
              "cachePath", cachePath ]

    let dispatchPersistedEvents envelopes cancellationToken =
        task {
            let mutable firstError: BackgroundWorkerError option = None
            let rememberFirst error =
                match firstError with
                | None -> firstError <- Some error
                | Some _ -> ()

            for envelope in envelopes do
                let! dispatchResult = publisher.DispatchPersistedAsync envelope cancellationToken

                match dispatchResult with
                | Ok () -> ()
                | Error error -> rememberFirst error

            match firstError with
            | None -> return Ok()
            | Some error -> return Error error
        }

    member _.ProcessOneQueueItemAsync(cancellationToken: CancellationToken) : Task<Result<bool, BackgroundWorkerError>> =
        taskResult {
            let nowUtc = clock.UtcNow
            let! transactionResult =
                DatabaseSession.withTransactionResult
                    dataSource
                    (fun connection transaction cancellationToken ->
                        taskResult {
                            let! claimed =
                                PlaybackQueueRepository.claimNextDetailedInTransaction connection transaction nowUtc cancellationToken
                                |> TaskResult.mapError RepositoryError

                            match claimed with
                            | None -> return false, []
                            | Some claimed ->
                                let! claimedEnvelope = claimed.QueueItemId |> claimedPayload |> createPlaybackEnvelope PlaybackQueueItemClaimed
                                do! appendEnvelope connection transaction claimedEnvelope cancellationToken

                                match claimed.TrackId with
                                | None ->
                                    let failureReason = "playback queue item has no track"
                                    let! wasMarkedFailed =
                                        PlaybackQueueRepository.markFailedInTransaction connection transaction claimed.QueueItemId nowUtc failureReason cancellationToken
                                        |> TaskResult.mapError RepositoryError
                                    do! requireTransition "mark playback failed" claimed.QueueItemId wasMarkedFailed
                                    let! playbackEndedEnvelope = playbackFailedPayload claimed.QueueItemId failureReason |> createPlaybackEnvelope PlaybackEnded
                                    let! streamFailureEnvelope = streamFailurePayload claimed.QueueItemId failureReason |> createPlaybackEnvelope StreamNodeFailureDetected
                                    do! appendEnvelope connection transaction playbackEndedEnvelope cancellationToken
                                    do! appendEnvelope connection transaction streamFailureEnvelope cancellationToken
                                    return true, [ claimedEnvelope; playbackEndedEnvelope; streamFailureEnvelope ]
                                | Some trackId ->
                                    let! cachePath =
                                        PlaybackQueueRepository.findCachedTrackFileInTransaction connection transaction trackId cancellationToken
                                        |> TaskResult.mapError RepositoryError

                                    match cachePath with
                                    | None ->
                                        let failureReason = "cache path unavailable"
                                        let! wasMarkedFailed =
                                            PlaybackQueueRepository.markFailedInTransaction connection transaction claimed.QueueItemId nowUtc failureReason cancellationToken
                                            |> TaskResult.mapError RepositoryError
                                        do! requireTransition "mark playback failed" claimed.QueueItemId wasMarkedFailed
                                        let! playbackEndedEnvelope = playbackFailedPayload claimed.QueueItemId failureReason |> createPlaybackEnvelope PlaybackEnded
                                        let! streamFailureEnvelope = streamFailurePayload claimed.QueueItemId failureReason |> createPlaybackEnvelope StreamNodeFailureDetected
                                        do! appendEnvelope connection transaction playbackEndedEnvelope cancellationToken
                                        do! appendEnvelope connection transaction streamFailureEnvelope cancellationToken
                                        return true, [ claimedEnvelope; playbackEndedEnvelope; streamFailureEnvelope ]
                                    | Some cachePath ->
                                        let! wasMarkedPlaying =
                                            PlaybackQueueRepository.markPlayingInTransaction connection transaction claimed.QueueItemId nowUtc cancellationToken
                                            |> TaskResult.mapError RepositoryError
                                        do! requireTransition "mark playback playing" claimed.QueueItemId wasMarkedPlaying
                                        let! playbackStartedEnvelope =
                                            playbackStartedPayload claimed.QueueItemId trackId cachePath
                                            |> createPlaybackEnvelope PlaybackStarted
                                        do! appendEnvelope connection transaction playbackStartedEnvelope cancellationToken
                                        return true, [ claimedEnvelope; playbackStartedEnvelope ]
                        })
                    cancellationToken

            let wasProcessed, envelopes = transactionResult
            do! dispatchPersistedEvents envelopes cancellationToken
            return wasProcessed
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                let! result = this.ProcessOneQueueItemAsync(stoppingToken)

                match result with
                | Ok true -> ()
                | Ok false -> do! Task.Delay(TimeSpan.FromSeconds(2.0), stoppingToken)
                | Error error ->
                    logger.LogError("Playback program worker failed: {error}.", BackgroundWorkerError.toMessage error)
                    do! Task.Delay(TimeSpan.FromSeconds(2.0), stoppingToken)
        }
        :> Task

module BackgroundWorkerComposition =
    let addBackgroundWorkers (options: Web10Options) (services: IServiceCollection) : IServiceCollection =
        services.AddSingleton<Web10Options>(options) |> ignore
        services.AddSingleton<IDomainEventDispatcher, DomainEventDispatcher>() |> ignore
        services.AddSingleton<IDomainEventPublisher, DomainEventPublisher>() |> ignore
        services.AddSingleton<ITelegramUpdateEventIngestor, TelegramUpdateEventIngestor>() |> ignore
        services.AddHostedService<OutboxRelayHostedService>() |> ignore
        services.AddHostedService<LibraryScanHostedService>() |> ignore
        services.AddHostedService<PlaybackProgramHostedService>() |> ignore
        services
