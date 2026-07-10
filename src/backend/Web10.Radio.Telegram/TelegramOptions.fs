namespace Web10.Radio.Telegram

/// How the bot ingests Telegram updates. v0 MVP uses long polling so the bot works
/// without a public HTTPS webhook endpoint; Webhook remains available for production.
[<RequireQualifiedAccess>]
type TelegramUpdateMode =
    | Webhook
    | LongPolling

type TelegramOptions =
    { BotToken: string
      WebhookSecret: string
      ChannelIdOrUsername: string
      RequestPriceStars: int
      SayPriceStars: int
      UpdateMode: TelegramUpdateMode }
