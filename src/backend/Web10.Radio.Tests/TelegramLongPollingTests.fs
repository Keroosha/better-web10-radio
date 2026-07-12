namespace Web10.Radio.Tests

open System
open System.Collections.Concurrent
open System.Text
open System.Threading
open System.Threading.Tasks
open Funogram.Telegram.Types
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging.Abstractions
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Telegram
open Web10.Radio.Application

module TelegramLongPollingTests =
    let private shortTimeout = TimeSpan.FromSeconds(5.0)
    let private retryTimeout = TimeSpan.FromSeconds(10.0)

    let private awaitSignal (timeout: TimeSpan) (message: string) (signal: Task) =
        task {
            let! completed = Task.WhenAny(signal, Task.Delay(timeout))
            Assert.That(Object.ReferenceEquals(completed, signal), Is.True, message)
        }

    let private parseUpdate (json: string) =
        let bytes = Encoding.UTF8.GetBytes(json)

        match TelegramUpdateJson.tryParse bytes bytes.Length with
        | Ok update -> update
        | Error message -> failwithf "Invalid test update JSON: %s" message

    let private helpUpdate (updateId: int64) =
        parseUpdate (
            sprintf
                """{"update_id":%d,"message":{"message_id":7,"date":1783400400,"chat":{"id":500,"type":"private"},"from":{"id":500,"is_bot":false,"first_name":"Poller"},"text":"/help"}}"""
                updateId
        )

    let private preCheckoutUpdate (updateId: int64) =
        parseUpdate (
            sprintf
                """{"update_id":%d,"pre_checkout_query":{"id":"precheckout-%d","from":{"id":500,"is_bot":false,"first_name":"Poller"},"currency":"XTR","total_amount":50,"invoice_payload":"00000000-0000-0000-0000-000000000001"}}"""
                updateId
                updateId
        )

    type private RecordingAdapterState() =
        let updates = ConcurrentQueue<int64>()
        let errors = ConcurrentQueue<string>()

        member _.Updates = updates.ToArray()
        member _.Errors = errors.ToArray()

        interface ITelegramAdapterState with
            member _.Snapshot() =
                { IsConfigured = true
                  ChannelIdOrUsername = "@web10_test"
                  LastUpdateId = updates.ToArray() |> Array.tryLast
                  LastError = errors.ToArray() |> Array.tryLast }

            member _.RecordUpdate(updateId) = updates.Enqueue(updateId)
            member _.RecordError(message) = errors.Enqueue(message)

    type private RecordingIngestor(failFirstAttempt: bool) =
        let ingested = ConcurrentQueue<int64 * DomainEventType>()
        let mutable attempts = 0

        member _.Ingested = ingested.ToArray()
        member _.Attempts = Volatile.Read(&attempts)

        interface ITelegramUpdateEventIngestor with
            member _.TryIngestAsync telegramUpdateId eventType _producer _payloadJson _cancellationToken =
                let attempt = Interlocked.Increment(&attempts)
                ingested.Enqueue((telegramUpdateId, eventType))

                if failFirstAttempt && attempt = 1 then
                    Task.FromResult(Error(UnexpectedException("ingest", "transient failure")))
                else
                    Task.FromResult(Ok true)

    type private RecordingPreCheckoutWorkflow(failFirstAttempt: bool) =
        let inputs = ConcurrentQueue<TelegramPreCheckoutInput>()
        let mutable attempts = 0

        member _.Inputs = inputs.ToArray()
        member _.Attempts = Volatile.Read(&attempts)

        interface ITelegramPreCheckoutWorkflow with
            member _.HandleAsync input _cancellationToken =
                let attempt = Interlocked.Increment(&attempts)
                inputs.Enqueue(input)

                if failFirstAttempt && attempt = 1 then
                    Task.FromResult(Error(UnexpectedException("pre-checkout", "transient failure")))
                else
                    Task.FromResult(Ok())

    type private PollResponse =
        | Batch of Update array
        | WaitForCancellation

    type private ScriptedBotClient(responses: PollResponse array) =
        let offsets = ConcurrentQueue<int64 option>()
        let deletedWith = ConcurrentQueue<bool>()
        let blockingFetch = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        let cancellationObserved = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable deleteCount = 0
        let mutable fetchCount = 0
        let mutable fetchedBeforeDelete = 0

        member _.Offsets = offsets.ToArray()
        member _.DeletedWith = deletedWith.ToArray()
        member _.FetchedBeforeDelete = Volatile.Read(&fetchedBeforeDelete) <> 0
        member _.BlockingFetch = blockingFetch.Task
        member _.CancellationObserved = cancellationObserved.Task

        interface ITelegramBotClient with
            member _.SendTextAsync(_chatId, _text, _keyboard, _cancellationToken) = Task.FromResult(Ok())
            member _.SendInvoiceAsync(_invoice, _cancellationToken) = Task.FromResult(Ok())
            member _.AnswerCallbackAsync(_callbackQueryId, _text, _cancellationToken) = Task.FromResult(Ok())
            member _.AnswerPreCheckoutAsync(_preCheckoutQueryId, _errorMessage, _cancellationToken) = Task.FromResult(Ok())

            member _.DeleteWebhookAsync(dropPendingUpdates, _cancellationToken) =
                deletedWith.Enqueue(dropPendingUpdates)
                Interlocked.Increment(&deleteCount) |> ignore
                Task.FromResult(Ok())

            member _.GetUpdatesAsync(offset, cancellationToken) =
                if Volatile.Read(&deleteCount) = 0 then
                    Interlocked.Exchange(&fetchedBeforeDelete, 1) |> ignore

                offsets.Enqueue(offset)
                let responseIndex = Interlocked.Increment(&fetchCount) - 1

                let response =
                    if responseIndex < responses.Length then
                        responses[responseIndex]
                    else
                        WaitForCancellation

                match response with
                | Batch updates -> Task.FromResult(Ok updates)
                | WaitForCancellation ->
                    blockingFetch.TrySetResult() |> ignore

                    task {
                        try
                            do! Task.Delay(Timeout.Infinite, cancellationToken)
                            return Ok(Array.empty<Update>)
                        finally
                            cancellationObserved.TrySetResult() |> ignore
                    }

    type private FakeProvider
        (
            state: ITelegramAdapterState,
            workflow: ITelegramPreCheckoutWorkflow,
            ingestor: ITelegramUpdateEventIngestor
        ) =
        interface IServiceProvider with
            member _.GetService(serviceType: Type) : obj =
                if serviceType = typeof<ITelegramAdapterState> then box state
                elif serviceType = typeof<ITelegramPreCheckoutWorkflow> then box workflow
                elif serviceType = typeof<ITelegramUpdateEventIngestor> then box ingestor
                else null

    type private FakeScope(provider: IServiceProvider) =
        interface IServiceScope with
            member _.ServiceProvider = provider

        interface IDisposable with
            member _.Dispose() = ()

    type private FakeScopeFactory(provider: IServiceProvider) =
        interface IServiceScopeFactory with
            member _.CreateScope() = new FakeScope(provider) :> IServiceScope

    let private createHostedService client state workflow ingestor =
        let provider = FakeProvider(state, workflow, ingestor) :> IServiceProvider
        let scopeFactory = FakeScopeFactory(provider) :> IServiceScopeFactory

        new TelegramLongPollingHostedService(
            client,
            scopeFactory,
            state,
            NullLogger<TelegramLongPollingHostedService>.Instance
        )

    let private stopAfterBlockingPoll (hosted: IHostedService) (client: ScriptedBotClient) =
        task {
            do! awaitSignal shortTimeout "The worker did not enter its cancellable long-poll fetch." client.BlockingFetch
            do! hosted.StopAsync(CancellationToken.None)
            do! awaitSignal shortTimeout "Stopping the worker did not cancel its outstanding Telegram long poll." client.CancellationObserved
        }

    [<Test>]
    let ``long polling deletes the webhook without dropping updates, uses shared ingress, advances by batch maximum, and stops cleanly`` () =
        task {
            let state = RecordingAdapterState()
            let ingestor = RecordingIngestor(false)
            let workflow = RecordingPreCheckoutWorkflow(false)
            let client = ScriptedBotClient([| Batch [| helpUpdate 101L; helpUpdate 100L |]; WaitForCancellation |])

            use worker =
                createHostedService
                    (client :> ITelegramBotClient)
                    (state :> ITelegramAdapterState)
                    (workflow :> ITelegramPreCheckoutWorkflow)
                    (ingestor :> ITelegramUpdateEventIngestor)

            let hosted = worker :> IHostedService
            do! hosted.StartAsync(CancellationToken.None)
            do! stopAfterBlockingPoll hosted client

            Assert.That(client.DeletedWith, Is.EqualTo(box [| false |]), "Long polling must delete the webhook with dropPendingUpdates=false.")
            Assert.That(client.FetchedBeforeDelete, Is.False, "getUpdates must not run before deleteWebhook succeeds.")
            Assert.That(client.Offsets, Is.EqualTo(box [| None; Some 102L |]), "The next offset must be the maximum processed update id plus one.")
            Assert.That(ingestor.Ingested |> Array.map fst, Is.EqualTo(box [| 100L; 101L |]))

            Assert.That(
                ingestor.Ingested |> Array.forall (fun (_, eventType) -> eventType = DomainEventType.TelegramCommandReceived),
                Is.True,
                "Polled typed updates must pass through ApiEndpoints.processTelegramUpdate rather than a parallel handler."
            )
        }

    [<Test>]
    let ``transient durable ingress failure retries the same update without acknowledging its offset`` () =
        task {
            let state = RecordingAdapterState()
            let ingestor = RecordingIngestor(true)
            let workflow = RecordingPreCheckoutWorkflow(false)
            let update = helpUpdate 701L
            let client = ScriptedBotClient([| Batch [| update |]; Batch [| update |]; WaitForCancellation |])

            use worker =
                createHostedService
                    (client :> ITelegramBotClient)
                    (state :> ITelegramAdapterState)
                    (workflow :> ITelegramPreCheckoutWorkflow)
                    (ingestor :> ITelegramUpdateEventIngestor)

            let hosted = worker :> IHostedService
            do! hosted.StartAsync(CancellationToken.None)
            do! awaitSignal retryTimeout "A transient ingest failure was not retried within the worker's bounded retry window." client.BlockingFetch
            do! hosted.StopAsync(CancellationToken.None)
            do! awaitSignal shortTimeout "Stopping the retrying worker did not cancel its outstanding Telegram long poll." client.CancellationObserved

            Assert.That(ingestor.Attempts, Is.EqualTo(2))
            Assert.That(client.Offsets, Is.EqualTo(box [| None; None; Some 702L |]), "The failed update must be fetched again before its offset is acknowledged.")
        }

    [<Test>]
    let ``transient pre-checkout failure retries the same update without acknowledging its offset`` () =
        task {
            let state = RecordingAdapterState()
            let ingestor = RecordingIngestor(false)
            let workflow = RecordingPreCheckoutWorkflow(true)
            let update = preCheckoutUpdate 801L
            let client = ScriptedBotClient([| Batch [| update |]; Batch [| update |]; WaitForCancellation |])

            use worker =
                createHostedService
                    (client :> ITelegramBotClient)
                    (state :> ITelegramAdapterState)
                    (workflow :> ITelegramPreCheckoutWorkflow)
                    (ingestor :> ITelegramUpdateEventIngestor)

            let hosted = worker :> IHostedService
            do! hosted.StartAsync(CancellationToken.None)
            do! awaitSignal retryTimeout "A transient pre-checkout failure was not retried within the worker's bounded retry window." client.BlockingFetch
            do! hosted.StopAsync(CancellationToken.None)
            do! awaitSignal shortTimeout "Stopping the retrying worker did not cancel its outstanding Telegram long poll." client.CancellationObserved

            Assert.That(workflow.Attempts, Is.EqualTo(2))
            Assert.That(client.Offsets, Is.EqualTo(box [| None; None; Some 802L |]), "The failed pre-checkout update must be fetched again before its offset is acknowledged.")
        }

    [<Test>]
    let ``Webhook mode does not create a long polling hosted service`` () =
        let services = ServiceCollection()
        TelegramLongPollingComposition.addTelegramLongPolling TelegramUpdateMode.Webhook services |> ignore

        use provider = services.BuildServiceProvider()

        Assert.That(
            provider.GetServices<IHostedService>() |> Seq.exists (fun hosted -> hosted :? TelegramLongPollingHostedService),
            Is.False,
            "Webhook mode must retain webhook ingress without starting a competing getUpdates poller."
        )
