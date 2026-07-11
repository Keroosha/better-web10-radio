namespace Web10.Radio.Telegram

type TelegramUpdateMode =
    | Webhook
    | LongPolling

type TelegramOptions =
    { BotToken: string
      WebhookSecret: string
      ChannelIdOrUsername: string
      RequestPriceStars: int
      UpdateMode: TelegramUpdateMode
      SayPriceStars: int }
