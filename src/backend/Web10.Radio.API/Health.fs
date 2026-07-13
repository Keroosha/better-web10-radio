namespace Web10.Radio.API

open Web10.Radio.Application
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


type StorageHealthCheck(options: StorageOptions, s3: IS3ObjectStorage) =
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
                        do! s3.ProbeBucketAsync(S3ClientScope.ConfiguredDefault, options.S3Bucket, timeout.Token)
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

type StreamNodeHeartbeatHealthCheck(dataSource: NpgsqlDataSource, timeProvider: TimeProvider) =
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

                        if not (PersistedHeartbeatFreshness.isFresh (timeProvider.GetUtcNow()) heartbeatAtUtc) then
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

        services
            .AddHealthChecks()
            .AddCheck<ApiHealthCheck>("api", tags = [| "ready"; "live" |])
            .AddCheck<PostgresHealthCheck>("postgresql", tags = [| "ready" |])
            .AddCheck<StorageHealthCheck>("storage", tags = [| "ready" |])
            .AddCheck<StreamNodeHeartbeatHealthCheck>("stream-node-heartbeat", tags = [| "ready" |])
        |> ignore

        services

