namespace Web10.Radio.Telegram

open System
open System.Threading
open System.Threading.Tasks
open Funogram
open Funogram.Types
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open Npgsql
open Web10.Radio.Application

[<RequireQualifiedAccess>]
module private TelegramReadinessProbe =
    let timeout = TimeSpan.FromSeconds(5.0)

    let createTimeoutToken (cancellationToken: CancellationToken) =
        let linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        linked.CancelAfter(timeout)
        linked

type ITelegramIdentityProbe =
    abstract member IsAuthenticatedBotAsync: CancellationToken -> Task<bool>

type FunogramTelegramIdentityProbe(config: BotConfig) =
    interface ITelegramIdentityProbe with
        member _.IsAuthenticatedBotAsync(cancellationToken: CancellationToken) =
            task {
                use timeout = TelegramReadinessProbe.createTimeoutToken cancellationToken
                try
                    let! response =
                        Funogram.Telegram.Api.getMe
                        |> Funogram.Api.api config
                        |> fun workflow -> Async.StartAsTask(workflow, cancellationToken = timeout.Token)
                    timeout.Token.ThrowIfCancellationRequested()
                    return response |> Result.isOk
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested -> return! Task.FromCanceled<bool>(cancellationToken)
                | _ -> return false
            }

type TelegramAdapterHealthCheck(identityProbe: ITelegramIdentityProbe) =
    interface IHealthCheck with
        member _.CheckHealthAsync(_context: HealthCheckContext, cancellationToken: CancellationToken) =
            task {
                try
                    let! authenticated = identityProbe.IsAuthenticatedBotAsync(cancellationToken)
                    if authenticated then return HealthCheckResult.Healthy("telegram getMe authenticated bot identity")
                    else return HealthCheckResult.Unhealthy("telegram getMe authentication failed")
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested -> return! Task.FromCanceled<HealthCheckResult>(cancellationToken)
                | ex -> return HealthCheckResult.Unhealthy("telegram getMe probe failed", ex)
            }

module TelegramHealthComposition =
    let addHealthChecks (services: IServiceCollection) =
        services
            .AddHealthChecks()
            .AddCheck<TelegramAdapterHealthCheck>("telegram-adapter", tags = [| "ready" |])
            .AddCheck<PostgresHealthCheck>("postgresql", tags = [| "ready" |])
        |> ignore
        services
