namespace Web10.Radio.API

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Diagnostics.HealthChecks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open Npgsql
open Web10.Radio.Telegram

type ApiHealthCheck() =
    interface IHealthCheck with
        member _.CheckHealthAsync(_context: HealthCheckContext, _cancellationToken: CancellationToken) =
            HealthCheckResult.Healthy("api reachable") |> Task.FromResult

type PostgresHealthCheck(dataSource: NpgsqlDataSource) =
    interface IHealthCheck with
        member _.CheckHealthAsync(_context: HealthCheckContext, cancellationToken: CancellationToken) =
            task {
                try
                    let! connection = dataSource.OpenConnectionAsync(cancellationToken)
                    use connection = connection
                    use command = new NpgsqlCommand("SELECT 1", connection)
                    let! _ = command.ExecuteScalarAsync(cancellationToken)
                    return HealthCheckResult.Healthy("postgresql reachable")
                with ex ->
                    return HealthCheckResult.Unhealthy("postgresql unreachable", ex)
            }

type TelegramAdapterHealthCheck(state: ITelegramAdapterState) =
    interface IHealthCheck with
        member _.CheckHealthAsync(_context: HealthCheckContext, _cancellationToken: CancellationToken) =
            let snapshot = state.Snapshot()

            if snapshot.IsConfigured then
                HealthCheckResult.Healthy("telegram adapter configured")
            else
                HealthCheckResult.Unhealthy("telegram adapter not configured")
            |> Task.FromResult

type StorageHealthCheck(options: StorageOptions) =
    interface IHealthCheck with
        member _.CheckHealthAsync(_context: HealthCheckContext, _cancellationToken: CancellationToken) =
            match options.Type with
            | Local ->
                if Directory.Exists(options.LocalRoot) then
                    HealthCheckResult.Healthy("local storage root exists")
                else
                    HealthCheckResult.Unhealthy("WEB10_STORAGE__LOCAL_ROOT does not exist")
            | S3 ->
                HealthCheckResult.Degraded(
                    sprintf
                        "S3 bucket %s configured; connectivity check is implemented with the storage backend phase"
                        options.S3Bucket
                )
            |> Task.FromResult

type StreamNodeHeartbeatState() =
    let syncRoot = obj()
    let mutable lastHeartbeatUtc: DateTimeOffset option = None
    let mutable lastFailure: string option = None

    member _.LastHeartbeatUtc = lock syncRoot (fun () -> lastHeartbeatUtc)

    member _.LastFailure = lock syncRoot (fun () -> lastFailure)

    member _.RecordHeartbeat(heartbeatUtc: DateTimeOffset, failure: string option) =
        lock syncRoot (fun () ->
            lastHeartbeatUtc <- Some heartbeatUtc
            lastFailure <- failure)

type StreamNodeHeartbeatHealthCheck(state: StreamNodeHeartbeatState, clock: IClock) =
    interface IHealthCheck with
        member _.CheckHealthAsync(_context: HealthCheckContext, _cancellationToken: CancellationToken) =
            let result =
                match state.LastHeartbeatUtc, state.LastFailure with
                | None, _ ->
                    HealthCheckResult.Degraded("stream-node heartbeat not received yet")
                | Some _, Some failure ->
                    HealthCheckResult.Degraded(sprintf "stream-node reported failure: %s" failure)
                | Some heartbeatUtc, None when clock.UtcNow - heartbeatUtc <= TimeSpan.FromSeconds(30.0) ->
                    HealthCheckResult.Healthy("stream-node heartbeat fresh")
                | Some _, None ->
                    HealthCheckResult.Degraded("stream-node heartbeat stale")

            result |> Task.FromResult

module HealthComposition =
    let addHealthChecks (options: Web10Options) (services: IServiceCollection) : IServiceCollection =
        services.AddSingleton<StorageOptions>(options.Storage) |> ignore
        services.AddSingleton<StreamNodeHeartbeatState>() |> ignore

        services
            .AddHealthChecks()
            .AddCheck<ApiHealthCheck>("api", tags = [| "ready"; "live" |])
            .AddCheck<TelegramAdapterHealthCheck>("telegram-adapter", tags = [| "ready" |])
            .AddCheck<PostgresHealthCheck>("postgresql", tags = [| "ready" |])
            .AddCheck<StorageHealthCheck>("storage", tags = [| "ready" |])
            .AddCheck<StreamNodeHeartbeatHealthCheck>("stream-node-heartbeat", tags = [| "ready" |])
        |> ignore

        services

module HealthEndpoints =
    let private traceId (context: HttpContext) =
        let current = Activity.Current

        if isNull current then
            context.TraceIdentifier
        else
            let currentTraceId = current.TraceId.ToString()

            if String.IsNullOrWhiteSpace currentTraceId then
                context.TraceIdentifier
            else
                currentTraceId

    let private writeReadyResponse (context: HttpContext) (report: HealthReport) =
        task {
            context.Response.ContentType <- "application/json; charset=utf-8"
            use writer = new Utf8JsonWriter(context.Response.Body, JsonWriterOptions(Indented = false))

            writer.WriteStartObject()
            writer.WriteString("status", string report.Status)
            writer.WritePropertyName("checks")
            writer.WriteStartArray()

            for entry in report.Entries do
                writer.WriteStartObject()
                writer.WriteString("name", entry.Key)
                writer.WriteString("status", string entry.Value.Status)

                if isNull entry.Value.Description then
                    writer.WriteNull("description")
                else
                    writer.WriteString("description", entry.Value.Description)

                writer.WriteNumber("durationMs", entry.Value.Duration.TotalMilliseconds)
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteString("traceId", traceId context)
            writer.WriteEndObject()
            do! writer.FlushAsync(context.RequestAborted)
        }
        :> Task

    let mapHealthEndpoints (app: WebApplication) : unit =
        let liveOptions = HealthCheckOptions()
        liveOptions.Predicate <- fun _ -> false
        app.MapHealthChecks("/health/live", liveOptions) |> ignore

        let readyOptions = HealthCheckOptions()
        readyOptions.Predicate <- fun registration -> registration.Tags.Contains("ready")
        readyOptions.ResponseWriter <- Func<HttpContext, HealthReport, Task>(writeReadyResponse)
        readyOptions.ResultStatusCodes[HealthStatus.Healthy] <- StatusCodes.Status200OK
        readyOptions.ResultStatusCodes[HealthStatus.Degraded] <- StatusCodes.Status200OK
        readyOptions.ResultStatusCodes[HealthStatus.Unhealthy] <- StatusCodes.Status503ServiceUnavailable
        app.MapHealthChecks("/health/ready", readyOptions) |> ignore
