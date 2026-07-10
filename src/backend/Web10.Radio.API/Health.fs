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
open Funogram.Types

[<RequireQualifiedAccess>]
module PersistedHeartbeatFreshness =
    let maxAge = TimeSpan.FromSeconds(30.0)

    let isFresh (nowUtc: DateTimeOffset) (heartbeatAtUtc: DateTimeOffset) =
        heartbeatAtUtc <= nowUtc && nowUtc - heartbeatAtUtc <= maxAge

type ApiHealthCheck() =
    interface IHealthCheck with
        member _.CheckHealthAsync(_context: HealthCheckContext, _cancellationToken: CancellationToken) =
            HealthCheckResult.Healthy("api reachable") |> Task.FromResult

[<RequireQualifiedAccess>]
module private ReadinessProbe =
    let timeout = TimeSpan.FromSeconds(5.0)
    let localMarker = ReadOnlyMemory<byte>([| 0uy |])

    let createTimeoutToken (cancellationToken: CancellationToken) =
        let linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        linked.CancelAfter(timeout)
        linked

type ITelegramIdentityProbe =
    abstract member IsAuthenticatedBotAsync: cancellationToken: CancellationToken -> Task<bool>

type FunogramTelegramIdentityProbe(config: BotConfig) =
    interface ITelegramIdentityProbe with
        member _.IsAuthenticatedBotAsync(cancellationToken: CancellationToken) =
            task {
                use timeout = ReadinessProbe.createTimeoutToken cancellationToken

                let! response =
                    Funogram.Telegram.Api.getMe
                    |> Funogram.Api.api config
                    |> fun workflow -> Async.StartAsTask(workflow, cancellationToken = timeout.Token)

                cancellationToken.ThrowIfCancellationRequested()

                return
                    match response with
                    | Ok identity -> identity.IsBot
                    | Error _ -> false
            }

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

type TelegramAdapterHealthCheck(identityProbe: ITelegramIdentityProbe) =
    interface IHealthCheck with
        member _.CheckHealthAsync(_context: HealthCheckContext, cancellationToken: CancellationToken) =
            task {
                try
                    let! authenticated = identityProbe.IsAuthenticatedBotAsync(cancellationToken)
                    cancellationToken.ThrowIfCancellationRequested()

                    if authenticated then
                        return HealthCheckResult.Healthy("telegram getMe authenticated bot identity")
                    else
                        return HealthCheckResult.Unhealthy("telegram getMe authentication failed")
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return! Task.FromCanceled<HealthCheckResult>(cancellationToken)
                | _ ->
                    return HealthCheckResult.Unhealthy("telegram getMe probe failed")
            }

type StorageHealthCheck(options: StorageOptions, s3: IS3ObjectEnumerator) =
    let checkLocalWriteability (cancellationToken: CancellationToken) =
        task {
            use timeout = ReadinessProbe.createTimeoutToken cancellationToken
            timeout.Token.ThrowIfCancellationRequested()

            let path = Path.Combine(options.LocalRoot, $".web10-readiness-{Guid.NewGuid():N}.tmp")
            let mutable created = false

            try
                do!
                    task {
                        use stream =
                            new FileStream(
                                path,
                                FileMode.CreateNew,
                                FileAccess.Write,
                                FileShare.None,
                                1,
                                FileOptions.Asynchronous
                            )

                        created <- true
                        do! stream.WriteAsync(ReadinessProbe.localMarker, timeout.Token)
                        do! stream.FlushAsync(timeout.Token)
                    }

                File.Delete(path)
                created <- false
            finally
                if created then
                    try
                        File.Delete(path)
                    with _ ->
                        ()
        }

    interface IHealthCheck with
        member _.CheckHealthAsync(_context: HealthCheckContext, cancellationToken: CancellationToken) =
            task {
                try
                    match options.Type with
                    | Local ->
                        if not (Directory.Exists(options.LocalRoot)) then
                            return HealthCheckResult.Unhealthy("local storage root does not exist")
                        else
                            do! checkLocalWriteability cancellationToken
                            cancellationToken.ThrowIfCancellationRequested()
                            return HealthCheckResult.Healthy("local storage create/write/delete succeeded")
                    | S3 ->
                        use timeout = ReadinessProbe.createTimeoutToken cancellationToken
                        do! s3.ProbeBucketAsync(options.S3Bucket, timeout.Token)
                        cancellationToken.ThrowIfCancellationRequested()
                        return HealthCheckResult.Healthy("S3 authenticated bucket list probe succeeded")
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return! Task.FromCanceled<HealthCheckResult>(cancellationToken)
                | _ ->
                    return
                        match options.Type with
                        | Local -> HealthCheckResult.Unhealthy("local storage create/write/delete failed")
                        | S3 -> HealthCheckResult.Unhealthy("S3 authenticated bucket list probe failed or timed out")
            }

type StreamNodeHeartbeatHealthCheck(dataSource: NpgsqlDataSource, clock: IClock) =
    [<Literal>]
    let latestHeartbeatSql = """SELECT "Status", "HeartbeatAtUtc", "FailureReason"
FROM "StreamNodeHeartbeats"
WHERE "IsDeleted" = false
ORDER BY "HeartbeatAtUtc" DESC, "CreatedAtUtc" DESC
LIMIT 1;"""

    interface IHealthCheck with
        member _.CheckHealthAsync(_context: HealthCheckContext, cancellationToken: CancellationToken) =
            task {
                try
                    use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                    use command = new NpgsqlCommand(latestHeartbeatSql, connection)
                    let! reader = command.ExecuteReaderAsync(cancellationToken)
                    use reader = reader
                    let! hasRow = reader.ReadAsync(cancellationToken)

                    if not hasRow then
                        return HealthCheckResult.Degraded("stream-node heartbeat not received yet")
                    else
                        let status = reader.GetString(0)
                        let heartbeatAtUtc = reader.GetFieldValue<DateTimeOffset>(1)
                        let hasFailure = not (reader.IsDBNull(2))

                        if not (PersistedHeartbeatFreshness.isFresh clock.UtcNow heartbeatAtUtc) then
                            return HealthCheckResult.Degraded("stream-node persisted heartbeat stale")
                        elif hasFailure then
                            return HealthCheckResult.Degraded("stream-node reported failure")
                        else
                            return
                                match status with
                                | "Live"
                                | "Starting" -> HealthCheckResult.Healthy("stream-node persisted heartbeat fresh")
                                | _ -> HealthCheckResult.Degraded($"stream-node status {status.ToLowerInvariant()}")
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return! Task.FromCanceled<HealthCheckResult>(cancellationToken)
                | ex ->
                    return HealthCheckResult.Unhealthy("stream-node persisted heartbeat unavailable", ex)
            }

module HealthComposition =
    let addHealthChecks (options: Web10Options) (services: IServiceCollection) : IServiceCollection =
        services.AddSingleton<StorageOptions>(options.Storage) |> ignore
        services |> S3StorageComposition.addS3ObjectStorage options.Storage |> ignore
        services.AddSingleton<StreamNodeHeartbeatState>() |> ignore
        services.AddSingleton<ITelegramIdentityProbe, FunogramTelegramIdentityProbe>() |> ignore

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
