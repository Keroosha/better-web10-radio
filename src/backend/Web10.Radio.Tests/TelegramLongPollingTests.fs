namespace Web10.Radio.Tests

open System
open System.Collections.Concurrent
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging.Abstractions
open NUnit.Framework
open Funogram.Telegram.Types
open Web10.Radio.API
open Web10.Radio.Telegram

module TelegramLongPollingTests =

    let private parseUpdate (json: string) =
        let bytes = Encoding.UTF8.GetBytes json

        match TelegramUpdateJson.tryParse bytes bytes.Length with
        | Ok update -> update
        | Error message -> failwithf "invalid test update json: %s" message

    let private helpUpdate (updateId: int64) =
        parseUpdate (
            sprintf
                """{"update_id":%d,"message":{"message_id":7,"date":1783400400,"chat":{"id":500,"type":"private"},"from":{"id":500,"is_bot":false,"first_name":"Poller"},"text":"/help"}}"""
                updateId
        )

    type private RecordingIngestor() =
        let ingested = ConcurrentQueue<int64 * DomainEventType>()
        member _.Ingested = ingested.ToArray()

        interface ITelegramUpdateEventIngestor with
            member _.TryIngestAsync telegramUpdateId eventType _producer _payloadJson _cancellationToken =
                ingested.Enqueue(telegramUpdateId, eventType)
                Task.FromResult(Ok true)

    type private StubPreCheckout() =
        interface ITelegramPreCheckoutWorkflow with
            member _.HandleAsync _input _cancellationToken = Task.FromResult(Ok())

    type private StubAdapterState() =
        interface ITelegramAdapterState with
            member _.Snapshot() =
                { IsConfigured = true
                  ChannelIdOrUsername = "@web10_test"
                  LastUpdateId = None
                  LastError = None }

            member _.RecordUpdate _ = ()
            member _.RecordError _ = ()

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

    /// Returns the whole batch on the first poll; on the second poll it signals completion and
    /// blocks until cancelled (mimicking a real long poll), so the test can assert deterministically.
    type private ScriptedBotClient(batch: Update array) =
        let offsets = ConcurrentQueue<int64 option>()
        let mutable deleteCount = 0
        let mutable getUpdatesCount = 0
        let mutable getBeforeDelete = false
        let processed = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        member _.Offsets = offsets.ToArray()
        member _.DeleteCount = deleteCount
        member _.GetUpdatesBeforeDelete = getBeforeDelete
        member _.Processed = processed.Task

        interface ITelegramBotClient with
            member _.SendTextAsync(_chatId, _text, _keyboard, _cancellationToken) = Task.FromResult(Ok())
            member _.SendInvoiceAsync(_invoice, _cancellationToken) = Task.FromResult(Ok())
            member _.AnswerCallbackAsync(_callbackQueryId, _text, _cancellationToken) = Task.FromResult(Ok())
            member _.AnswerPreCheckoutAsync(_preCheckoutQueryId, _errorMessage, _cancellationToken) = Task.FromResult(Ok())

            member _.DeleteWebhookAsync(_dropPendingUpdates, _cancellationToken) =
                Interlocked.Increment(&deleteCount) |> ignore
                Task.FromResult(Ok())

            member _.GetUpdatesAsync(offset, _timeoutSeconds, cancellationToken) =
                if deleteCount = 0 then
                    getBeforeDelete <- true

                offsets.Enqueue offset

                if Interlocked.Increment(&getUpdatesCount) = 1 then
                    Task.FromResult(Ok batch)
                else
                    processed.TrySetResult() |> ignore

                    task {
                        do! Task.Delay(Timeout.Infinite, cancellationToken)
                        return Ok(Array.empty<Update>)
                    }

    [<Test>]
    let ``long polling deletes the webhook first, routes each update through the shared path, and advances the offset`` () =
        task {
            let batch = [| helpUpdate 100L; helpUpdate 101L |]
            let ingestor = RecordingIngestor()
            let provider = FakeProvider(StubAdapterState(), StubPreCheckout(), ingestor) :> IServiceProvider
            let scopeFactory = FakeScopeFactory(provider) :> IServiceScopeFactory
            let client = ScriptedBotClient(batch)

            use worker =
                new TelegramLongPollingHostedService(
                    client,
                    StubAdapterState(),
                    scopeFactory,
                    NullLogger<TelegramLongPollingHostedService>.Instance
                )

            let hosted = worker :> IHostedService

            do! hosted.StartAsync(CancellationToken.None)
            let! completed = Task.WhenAny(client.Processed, Task.Delay(TimeSpan.FromSeconds(5.0)))
            do! hosted.StopAsync(CancellationToken.None)

            Assert.That(completed, Is.EqualTo(client.Processed), "the worker did not reach its second poll in time")
            Assert.That(client.DeleteCount, Is.EqualTo(1))
            Assert.That(client.GetUpdatesBeforeDelete, Is.False, "getUpdates ran before deleteWebhook")

            let ingested = ingestor.Ingested
            Assert.That(ingested |> Array.map fst, Is.EqualTo(box [| 100L; 101L |]))
            Assert.That(ingested |> Array.forall (fun (_, eventType) -> eventType = DomainEventType.TelegramCommandReceived), Is.True)

            let offsets = client.Offsets
            Assert.That(offsets.Length, Is.GreaterThanOrEqualTo(2))
            Assert.That(offsets[0], Is.EqualTo(None))
            Assert.That(offsets[1], Is.EqualTo(Some 102L))
        }
