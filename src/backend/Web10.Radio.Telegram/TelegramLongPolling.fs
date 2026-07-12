namespace Web10.Radio.Telegram

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Web10.Radio.Application
open Funogram.Telegram.Types

[<RequireQualifiedAccess>]
module private TelegramLongPollingLog =
    let private adapterRequestFailedMessage =
        LoggerMessage.Define<string, Nullable<int>>(
            LogLevel.Warning,
            EventId(3210, "TelegramLongPollingAdapterRequestFailed"),
            "Telegram long polling adapter request {method} failed with status {statusCode}."
        )

    let private updateRetryMessage =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            EventId(3211, "TelegramLongPollingUpdateRetry"),
            "Telegram long polling update processing will retry after {failureKind}."
        )

    let private unhandledProcessingFailureMessage =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            EventId(3212, "TelegramLongPollingUnhandledProcessingFailure"),
            "Telegram long polling update processing failed with {exceptionType}."
        )

    let adapterRequestFailed (logger: ILogger) (error: TelegramBotError) =
        let errorCode =
            match error.ErrorCode with
            | Some value -> Nullable value
            | None -> Nullable()

        adapterRequestFailedMessage.Invoke(logger, error.Method, errorCode, null)

    let updateRetry (logger: ILogger) failureKind =
        updateRetryMessage.Invoke(logger, failureKind, null)

    let unhandledProcessingFailure (logger: ILogger) (error: exn) =
        unhandledProcessingFailureMessage.Invoke(logger, error.GetType().Name, error)

type TelegramLongPollingHostedService
    (
        telegramClient: ITelegramBotClient,
        scopeFactory: IServiceScopeFactory,
        adapterState: ITelegramAdapterState,
        logger: ILogger<TelegramLongPollingHostedService>
    ) =
    inherit BackgroundService()

    let retryDelay = TimeSpan.FromSeconds(1.0)
    let emptyPollDelay = TimeSpan.FromMilliseconds(250.0)

    let recordAdapterRequestFailure stateMessage error =
        adapterState.RecordError(stateMessage)
        TelegramLongPollingLog.adapterRequestFailed logger error

    let processUpdates currentOffset (updates: Update array) (stoppingToken: CancellationToken) =
        task {
            let orderedUpdates = updates |> Array.sortBy (fun update -> update.UpdateId)
            let mutable nextOffset = currentOffset
            let mutable retryRequired = false

            for update in orderedUpdates do
                if not retryRequired
                   && (nextOffset |> Option.forall (fun offset -> update.UpdateId >= offset)) then
                    try
                        use scope = scopeFactory.CreateScope()

                        let! outcome =
                            TelegramEndpoints.processTelegramUpdate scope.ServiceProvider update stoppingToken

                        match outcome with
                        | TelegramUpdateAccepted ->
                            nextOffset <- Some(update.UpdateId + 1L)
                        | TelegramUpdateRejected _ ->
                            nextOffset <- Some(update.UpdateId + 1L)
                        | TelegramPreCheckoutUnavailable _ ->
                            adapterState.RecordError("telegram.polling.pre_checkout_retry")
                            TelegramLongPollingLog.updateRetry logger "pre_checkout_unavailable"
                            retryRequired <- true
                        | TelegramIngestFailed _ ->
                            adapterState.RecordError("telegram.polling.ingest_retry")
                            TelegramLongPollingLog.updateRetry logger "ingest_failed"
                            retryRequired <- true
                    with
                    | :? OperationCanceledException when stoppingToken.IsCancellationRequested ->
                        stoppingToken.ThrowIfCancellationRequested()
                    | error ->
                        adapterState.RecordError("telegram.polling.processing_failed")
                        TelegramLongPollingLog.unhandledProcessingFailure logger error
                        retryRequired <- true

            return nextOffset, retryRequired
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                let mutable webhookDeleted = false

                while not webhookDeleted && not stoppingToken.IsCancellationRequested do
                    let! deleteResult = telegramClient.DeleteWebhookAsync(false, stoppingToken)

                    match deleteResult with
                    | Ok () -> webhookDeleted <- true
                    | Error error ->
                        recordAdapterRequestFailure "telegram.polling.delete_webhook_failed" error
                        do! Task.Delay(retryDelay, stoppingToken)

                let mutable offset: int64 option = None

                while webhookDeleted && not stoppingToken.IsCancellationRequested do
                    let! pollResult = telegramClient.GetUpdatesAsync(offset, stoppingToken)

                    match pollResult with
                    | Error error ->
                        recordAdapterRequestFailure "telegram.polling.get_updates_failed" error
                        do! Task.Delay(retryDelay, stoppingToken)
                    | Ok updates ->
                        let! nextOffset, retryRequired = processUpdates offset updates stoppingToken
                        offset <- nextOffset

                        if retryRequired then
                            do! Task.Delay(retryDelay, stoppingToken)
                        elif updates.Length = 0 then
                            do! Task.Delay(emptyPollDelay, stoppingToken)
            with
            | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
        }
        :> Task

[<RequireQualifiedAccess>]
module TelegramLongPollingComposition =
    let addTelegramLongPolling (updateMode: TelegramUpdateMode) (services: IServiceCollection) =
        match updateMode with
        | Webhook -> services
        | LongPolling ->
            services.AddHostedService<TelegramLongPollingHostedService>() |> ignore
            services
