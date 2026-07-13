namespace Web10.Radio.Telegram

open System
open System.Diagnostics
open System.Diagnostics.Metrics
open System.Threading

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace

module FlowTelemetry =
    [<Literal>]
    let ActivitySourceName = "Web10.Radio.Telegram"

    [<Literal>]
    let MeterName = "Web10.Radio.Telegram"

    [<Literal>]
    let TelegramUpdateActivityName = "telegram.update"

    [<Literal>]
    let PaymentCompleteActivityName = "payment.complete"

    [<Literal>]
    let PaymentInvoiceActivityName = "payment.invoice"

    [<Literal>]
    let PaymentPreCheckoutActivityName = "payment.pre_checkout"

    [<Literal>]
    let QueueClaimActivityName = "queue.claim"

    [<Literal>]
    let LibraryScanActivityName = "library.scan"

    [<Literal>]
    let StreamNodeHeartbeatActivityName = "stream-node.heartbeat"

    [<Literal>]
    let StreamNodePlaybackLeaseActivityName = "stream-node.playback.lease"

    [<Literal>]
    let StreamNodePlaybackCompletionActivityName = "stream-node.playback.completion"

    let ActivitySource = new ActivitySource(ActivitySourceName)
    let Meter = new Meter(MeterName)

    let TelegramUpdates = Meter.CreateCounter<int64>("web10.radio.telegram.updates")
    let Payments = Meter.CreateCounter<int64>("web10.radio.payments")
    let QueueClaims = Meter.CreateCounter<int64>("web10.radio.queue.claims")
    let LibraryScans = Meter.CreateCounter<int64>("web10.radio.library.scans")
    let StreamNodeHeartbeats = Meter.CreateCounter<int64>("web10.radio.stream_node.heartbeats")
    let StreamNodeCallbacks = Meter.CreateCounter<int64>("web10.radio.stream_node.callbacks")

    type MetricTag = private MetricTag of key: string * value: string

    let private allowedOutcomes =
        set [
            "ignored"
            "accepted"
            "duplicate"
            "rejected"
            "error"
            "completed"
            "sent"
            "terminal_failure"
            "retryable_error"
            "approved"
            "transport_error"
            "timeout"
            "claimed"
            "empty"
            "failed"
            "invalid"
            "stale"
        ]

    let private createMetricTag key allowedValues value =
        if String.IsNullOrWhiteSpace(value) || not (Set.contains value allowedValues) then
            invalidArg (nameof value) $"Unsupported {key} metric tag value."

        MetricTag(key, value)

    let stage value = createMetricTag "stage" (set [ "complete"; "invoice"; "pre_checkout" ]) value
    let kind value = createMetricTag "kind" (set [ "lease"; "completion" ]) value

    let status value =
        createMetricTag "status" (set [ "starting"; "live"; "degraded"; "restarting"; "failed"; "offline" ]) value

    let storage value = createMetricTag "storage" (set [ "local"; "s3" ]) value

    type FlowInstrument = private FlowInstrument of activityName: string * counter: Counter<int64>

    let TelegramUpdate = FlowInstrument(TelegramUpdateActivityName, TelegramUpdates)
    let PaymentComplete = FlowInstrument(PaymentCompleteActivityName, Payments)
    let PaymentInvoice = FlowInstrument(PaymentInvoiceActivityName, Payments)
    let PaymentPreCheckout = FlowInstrument(PaymentPreCheckoutActivityName, Payments)
    let QueueClaim = FlowInstrument(QueueClaimActivityName, QueueClaims)
    let LibraryScan = FlowInstrument(LibraryScanActivityName, LibraryScans)
    let StreamNodeHeartbeat = FlowInstrument(StreamNodeHeartbeatActivityName, StreamNodeHeartbeats)
    let StreamNodePlaybackLease = FlowInstrument(StreamNodePlaybackLeaseActivityName, StreamNodeCallbacks)
    let StreamNodePlaybackCompletion = FlowInstrument(StreamNodePlaybackCompletionActivityName, StreamNodeCallbacks)

    type FlowAttempt internal (activity: Activity, counter: Counter<int64>) =
        let mutable terminalState = 0

        member private _.SetTagCore(tagName: string, value: obj) =
            if not (isNull activity) then
                activity.SetTag(tagName, value) |> ignore

        member private _.DisposeActivity() =
            if not (isNull activity) then
                activity.Dispose()

        member this.SetTag(tagName: string, value: obj) =
            if Volatile.Read(&terminalState) = 0 then
                this.SetTagCore(tagName, value)

        member this.SetOutcome(outcome: string) =
            if Volatile.Read(&terminalState) = 0 then
                this.SetTagCore("web10.outcome", box outcome)

        member this.SetError(error: exn) =
            if Volatile.Read(&terminalState) = 0 && not (isNull error) then
                if not (isNull activity) then
                    activity.SetStatus(ActivityStatusCode.Error) |> ignore

                this.SetTagCore("error.type", box (error.GetType().FullName))

        member internal this.TryFinish(outcome: string, metricTags: MetricTag list) =
            if Interlocked.CompareExchange(&terminalState, 1, 0) = 0 then
                try
                    this.SetTagCore("web10.outcome", box outcome)

                    let mutable tags = TagList()
                    tags.Add("outcome", box outcome)

                    for MetricTag(key, value) in metricTags do
                        tags.Add(key, box value)

                    counter.Add(1L, &tags)
                    true
                finally
                    this.DisposeActivity()
            else
                false

        interface IDisposable with
            member this.Dispose() =
                if Interlocked.CompareExchange(&terminalState, 2, 0) = 0 then
                    this.DisposeActivity()

    let start (FlowInstrument(activityName, counter)) =
        new FlowAttempt(ActivitySource.StartActivity(activityName, ActivityKind.Internal), counter)

    let startRoot (FlowInstrument(activityName, counter)) =
        new FlowAttempt(ActivitySource.StartActivity(activityName, ActivityKind.Consumer, ActivityContext()), counter)

    let addTag tagName value (attempt: FlowAttempt) = attempt.SetTag(tagName, value)
    let setOutcome outcome (attempt: FlowAttempt) = attempt.SetOutcome(outcome)
    let setError error (attempt: FlowAttempt) = attempt.SetError(error)

    let private validateOutcome outcome =
        if String.IsNullOrWhiteSpace(outcome) || not (Set.contains outcome allowedOutcomes) then
            invalidArg (nameof outcome) "Unsupported metric outcome."

    let finish outcome (metricTags: MetricTag list) (attempt: FlowAttempt) =
        validateOutcome outcome
        attempt.TryFinish(outcome, metricTags)

    let finishError outcome (metricTags: MetricTag list) error (attempt: FlowAttempt) =
        validateOutcome outcome
        attempt.SetError(error)
        attempt.TryFinish(outcome, metricTags)

module ObservabilityComposition =
    let addObservability (otel: OtelOptions option) (_environment: IHostEnvironment) (services: IServiceCollection) : IServiceCollection =
        match otel with
        | None -> services
        | Some otel ->
            services
                .AddOpenTelemetry()
                .ConfigureResource(fun resource -> resource.AddService(serviceName = "Web10.Radio.Telegram") |> ignore)
                .WithTracing(fun tracing ->
                    tracing
                        .AddSource(FlowTelemetry.ActivitySourceName)
                        .AddAspNetCoreInstrumentation()
                        .AddOtlpExporter(fun exporter -> exporter.Endpoint <- otel.ExporterOtlpEndpoint)
                    |> ignore)
                .WithMetrics(fun metrics ->
                    metrics
                        .AddAspNetCoreInstrumentation()
                        .AddMeter(FlowTelemetry.MeterName)
                        .AddOtlpExporter(fun exporter -> exporter.Endpoint <- otel.ExporterOtlpEndpoint)
                    |> ignore)
            |> ignore

            services
