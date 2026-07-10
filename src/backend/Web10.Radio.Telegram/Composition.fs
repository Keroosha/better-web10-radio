namespace Web10.Radio.Telegram

open Microsoft.Extensions.DependencyInjection

module TelegramComposition =
    let addTelegram (options: TelegramOptions) (services: IServiceCollection) : IServiceCollection =
        services
            .AddSingleton<TelegramOptions>(options)
            .AddSingleton<ITelegramAdapterState>(fun _ -> TelegramAdapterState(options) :> ITelegramAdapterState)
            .AddSingleton(FunogramConfig.create options)
            .AddSingleton<ITelegramBotClient>(fun serviceProvider ->
                FunogramTelegramBotClient(serviceProvider.GetRequiredService<Funogram.Types.BotConfig>()) :> ITelegramBotClient)
