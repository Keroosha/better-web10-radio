namespace Web10.Radio.Application

open System
open System.Diagnostics
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Diagnostics.HealthChecks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Diagnostics.HealthChecks
open Npgsql

[<RequireQualifiedAccess>]
module PersistedHeartbeatFreshness =
    let maxAge = TimeSpan.FromSeconds(30.0)

    let isFresh (nowUtc: DateTimeOffset) (heartbeatAtUtc: DateTimeOffset) =
        heartbeatAtUtc <= nowUtc && nowUtc - heartbeatAtUtc <= maxAge

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
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return! Task.FromCanceled<HealthCheckResult>(cancellationToken)
                | ex ->
                    return HealthCheckResult.Unhealthy("postgresql unreachable", ex)
            }

[<RequireQualifiedAccess>]
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

    /// Registers process liveness and tagged readiness endpoints. Individual
    /// services add their own checks; this helper keeps wire formatting equal.
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
