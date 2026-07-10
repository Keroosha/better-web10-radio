namespace Web10.Radio.API

open System
open System.Threading
open System.Threading.Tasks
open Web10.Radio.Database.Repositories
open Web10.Radio.Database

type BackgroundWorkerError =
    | DomainEventError of DomainEventError
    | RepositoryError of RepositoryError
    | UnknownEventType of value: string
    | InvalidPayload of eventType: string * message: string
    | StateTransitionRejected of operation: string * id: Guid
    | TelegramTransportError of methodName: string * description: string
    | UnexpectedException of operation: string * message: string

module BackgroundWorkerError =
    let toMessage error =
        match error with
        | DomainEventError domainError -> DomainEventError.toMessage domainError
        | RepositoryError repositoryError -> RepositoryError.toMessage repositoryError
        | UnknownEventType value -> sprintf "Unknown domain event type: %s." value
        | InvalidPayload(eventType, message) -> sprintf "Invalid payload for %s: %s" eventType message
        | StateTransitionRejected(operation, id) -> sprintf "State transition rejected: %s for %O." operation id
        | TelegramTransportError(methodName, description) -> sprintf "Telegram transport %s failed: %s" methodName description
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

type TelegramPreCheckoutInput =
    { TelegramUpdateId: int64
      QueryId: string
      TelegramUserId: int64
      LanguageCode: string option
      Currency: string
      TotalAmount: int
      InvoicePayload: string }

type ITelegramBotWorkflow =
    abstract member HandleInteractionAsync:
        DomainEventEnvelope -> CancellationToken -> Task<Result<unit, BackgroundWorkerError>>

    abstract member SendInvoiceAsync:
        DomainEventEnvelope -> CancellationToken -> Task<Result<unit, BackgroundWorkerError>>