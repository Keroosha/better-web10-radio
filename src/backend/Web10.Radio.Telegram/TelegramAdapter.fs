namespace Web10.Radio.Telegram

open Funogram.Telegram.Bot

type TelegramAdapterSnapshot =
    { IsConfigured: bool
      ChannelIdOrUsername: string
      LastUpdateId: int64 option
      LastError: string option }

type ITelegramAdapterState =
    abstract member Snapshot: unit -> TelegramAdapterSnapshot

type TelegramAdapterState(options: TelegramOptions) =
    interface ITelegramAdapterState with
        member _.Snapshot() =
            { IsConfigured = true
              ChannelIdOrUsername = options.ChannelIdOrUsername
              LastUpdateId = None
              LastError = None }

module FunogramConfig =
    let create (options: TelegramOptions) =
        { Config.defaultConfig with Token = options.BotToken }
