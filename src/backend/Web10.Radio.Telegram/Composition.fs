namespace Web10.Radio.Telegram

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Web10.Radio.Application
open Web10.Radio.Database
open Web10.Radio.Database.Repositories

module TelegramComposition =
    let addTelegram (options: TelegramOptions) (services: IServiceCollection) : IServiceCollection =
        services
            .AddSingleton<TelegramOptions>(options)
            .AddSingleton<ITelegramAdapterState>(fun _ -> TelegramAdapterState(options) :> ITelegramAdapterState)
            .AddSingleton(FunogramConfig.create options)
            .AddSingleton<ITelegramBotClient>(fun serviceProvider ->
                FunogramTelegramBotClient(serviceProvider.GetRequiredService<Funogram.Types.BotConfig>()) :> ITelegramBotClient)

    let addTelegramApplication (options: TelegramServiceOptions) (services: IServiceCollection) : IServiceCollection =
        services
        |> DatabaseComposition.addDatabase options.Postgres
        |> ApplicationComposition.addApplicationServices
        |> addTelegram options.Telegram
        |> fun configured ->
            configured.AddSingleton<ITelegramIdentityProbe, FunogramTelegramIdentityProbe>() |> ignore
            configured
        |> TelegramHealthComposition.addHealthChecks
        |> fun configured ->
            configured
                .AddSingleton<TelegramBotWorkflow>()
                .AddSingleton<ITelegramBotWorkflow>(fun provider -> provider.GetRequiredService<TelegramBotWorkflow>() :> ITelegramBotWorkflow)
                .AddSingleton<ITelegramPreCheckoutWorkflow>(fun provider -> provider.GetRequiredService<TelegramBotWorkflow>() :> ITelegramPreCheckoutWorkflow)
                .AddSingleton<ITelegramUpdateEventIngestor, TelegramUpdateEventIngestor>()
                .AddSingleton<IDomainEventPublisher, TelegramDomainEventPublisher>()
                .AddSingleton<TelegramDomainEventDispatcher>()
                .AddSingleton<IDomainEventDispatcher>(fun provider -> provider.GetRequiredService<TelegramDomainEventDispatcher>() :> IDomainEventDispatcher)
                .AddSingleton<OutboxRelayHostedService>(fun provider ->
                    OutboxRelayHostedService(
                        OutboxAudience.Telegram,
                        provider.GetRequiredService<Npgsql.NpgsqlDataSource>(),
                        provider.GetRequiredService<IDomainEventDispatcher>(),
                        provider.GetRequiredService<TimeProvider>(),
                        provider.GetRequiredService<ILogger<OutboxRelayHostedService>>()))
                .AddHostedService(fun provider -> provider.GetRequiredService<OutboxRelayHostedService>())
                |> ignore
            configured

