namespace Web10.Radio.API

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Web10.Radio.Telegram

/// Long-polling ingester for Telegram updates — the v0 MVP alternative to the webhook so the
/// bot works without a public HTTPS endpoint. Registered only when
/// WEB10_TELEGRAM__UPDATE_MODE=LongPolling. It deletes any active webhook (getUpdates and a
/// webhook are mutually exclusive), then pulls updates and routes each through the SAME shared
/// ingestion path the webhook uses (ApiEndpoints.processTelegramUpdate), so payment/command
/// handling never forks. Update dedupe by (telegramUpdateId, eventType) makes a restart
/// re-fetch idempotent, so the poll offset is kept in memory only.
type TelegramLongPollingHostedService
    (
        botClient: ITelegramBotClient,
        adapterState: ITelegramAdapterState,
        scopeFactory: IServiceScopeFactory,
        logger: ILogger<TelegramLongPollingHostedService>
    ) =
    inherit BackgroundService()

    // getUpdates long-poll window; the HttpClient default timeout (100s) comfortably exceeds it.
    let longPollTimeoutSeconds = 25
    // Backoff applied after a transport error or a transient processing failure.
    let transientDelay = TimeSpan.FromSeconds(3.0)

    let deleteExistingWebhook (stoppingToken: CancellationToken) =
        task {
            let! result = botClient.DeleteWebhookAsync(false, stoppingToken)

            match result with
            | Ok() -> ()
            | Error error ->
                adapterState.RecordError("telegram.long_polling.delete_webhook")
                logger.LogWarning("Telegram long polling could not delete the existing webhook: {description}.", error.Description)
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                do! deleteExistingWebhook stoppingToken

                let mutable offset: int64 option = None

                while not stoppingToken.IsCancellationRequested do
                    let mutable backoff = false
                    let! result = botClient.GetUpdatesAsync(offset, longPollTimeoutSeconds, stoppingToken)

                    match result with
                    | Ok updates when updates.Length = 0 -> ()
                    | Ok updates ->
                        use scope = scopeFactory.CreateScope()
                        let services = scope.ServiceProvider
                        let mutable index = 0
                        let mutable keepGoing = true

                        while keepGoing && index < updates.Length && not stoppingToken.IsCancellationRequested do
                            let update = updates[index]
                            let! outcome = ApiEndpoints.processTelegramUpdate services update stoppingToken

                            match outcome with
                            | TelegramUpdateAccepted ->
                                offset <- Some(update.UpdateId + 1L)
                                index <- index + 1
                            | TelegramUpdateRejected message ->
                                // Malformed/unsupported update: ack it (advance the offset) so it is not a poison pill.
                                logger.LogWarning("Telegram long polling skipped invalid update {updateId}: {message}.", update.UpdateId, message)
                                offset <- Some(update.UpdateId + 1L)
                                index <- index + 1
                            | TelegramPreCheckoutUnavailable error ->
                                // Transient infra failure: leave the offset so the update is re-fetched after a backoff.
                                logger.LogError("Telegram long polling pre-checkout failed for update {updateId}: {error}.", update.UpdateId, BackgroundWorkerError.toMessage error)
                                keepGoing <- false
                                backoff <- true
                            | TelegramIngestFailed error ->
                                logger.LogError("Telegram long polling ingest failed for update {updateId}: {error}.", update.UpdateId, BackgroundWorkerError.toMessage error)
                                keepGoing <- false
                                backoff <- true
                    | Error error ->
                        adapterState.RecordError("telegram.long_polling.get_updates")
                        logger.LogWarning("Telegram long polling getUpdates failed: {description}.", error.Description)
                        backoff <- true

                    if backoff && not stoppingToken.IsCancellationRequested then
                        do! Task.Delay(transientDelay, stoppingToken)
            with
            | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
        }
        :> Task

[<RequireQualifiedAccess>]
module TelegramLongPollingComposition =
    let addTelegramLongPolling (telegram: TelegramOptions) (services: IServiceCollection) : IServiceCollection =
        match telegram.UpdateMode with
        | TelegramUpdateMode.LongPolling -> services.AddHostedService<TelegramLongPollingHostedService>() |> ignore
        | TelegramUpdateMode.Webhook -> ()

        services
