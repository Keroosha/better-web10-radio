namespace Web10.Radio.Telegram

open System
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Funogram
open Funogram.Telegram
open Funogram.Telegram.Types

[<RequireQualifiedAccess>]
type TelegramLocale =
    | Russian
    | English

[<RequireQualifiedAccess>]
module TelegramLocale =
    let ofLanguageCode (languageCode: string option) : TelegramLocale =
        match languageCode with
        | Some value when String.Equals(value, "ru", StringComparison.OrdinalIgnoreCase) -> TelegramLocale.Russian
        | Some value when value.StartsWith("ru-", StringComparison.OrdinalIgnoreCase) -> TelegramLocale.Russian
        | _ -> TelegramLocale.English

type TelegramInlineButton =
    { Text: string
      CallbackData: string }

type TelegramStarsInvoice =
    { ChatId: int64
      Title: string
      Description: string
      Payload: string
      Currency: string
      ProviderToken: string
      PriceLabel: string
      AmountStars: int }

type TelegramBotError =
    { Method: string
      Description: string
      ErrorCode: int option
      IsRetryable: bool }

[<RequireQualifiedAccess>]
type TelegramCallback =
    | RequestSelect of requestId: Guid * trackId: Guid
    | RequestConfirm of requestId: Guid
    | RequestCancel of requestId: Guid
    | SongSelect of trackId: Guid

[<RequireQualifiedAccess>]
type TelegramCallbackCodecError =
    | InvalidAction

[<RequireQualifiedAccess>]
module TelegramCallback =
    let private maxCallbackDataUtf8Bytes = 64

    let private encodeGuid (value: Guid) =
        Convert.ToBase64String(value.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private isBase64UrlCharacter character =
        (character >= 'A' && character <= 'Z')
        || (character >= 'a' && character <= 'z')
        || (character >= '0' && character <= '9')
        || character = '-'
        || character = '_'

    let private tryDecodeGuid (value: string) =
        if isNull value || value.Length <> 22 || not (value |> Seq.forall isBase64UrlCharacter) then
            None
        else
            let base64 = value.Replace('-', '+').Replace('_', '/') + "=="

            try
                let bytes = Convert.FromBase64String base64

                if bytes.Length <> 16 then
                    None
                else
                    let guid = Guid bytes

                    if String.Equals(encodeGuid guid, value, StringComparison.Ordinal) then
                        Some guid
                    else
                        None
            with :? FormatException -> None

    let encode callback =
        let callbackData =
            match callback with
            | TelegramCallback.RequestSelect(requestId, trackId) ->
                $"rq:s:{encodeGuid requestId}:{encodeGuid trackId}"
            | TelegramCallback.RequestConfirm requestId -> $"rq:c:{encodeGuid requestId}"
            | TelegramCallback.RequestCancel requestId -> $"rq:x:{encodeGuid requestId}"
            | TelegramCallback.SongSelect trackId -> $"sg:s:{encodeGuid trackId}"

        if Encoding.UTF8.GetByteCount callbackData <= maxCallbackDataUtf8Bytes then
            Ok callbackData
        else
            Error TelegramCallbackCodecError.InvalidAction

    let tryDecode (callbackData: string) =
        if isNull callbackData || Encoding.UTF8.GetByteCount callbackData > maxCallbackDataUtf8Bytes then
            Error TelegramCallbackCodecError.InvalidAction
        else
            match callbackData.Split([| ':' |], StringSplitOptions.None) with
            | [| "rq"; "s"; requestId; trackId |] ->
                match tryDecodeGuid requestId, tryDecodeGuid trackId with
                | Some decodedRequestId, Some decodedTrackId ->
                    Ok(TelegramCallback.RequestSelect(decodedRequestId, decodedTrackId))
                | _ -> Error TelegramCallbackCodecError.InvalidAction
            | [| "rq"; "c"; requestId |] ->
                match tryDecodeGuid requestId with
                | Some decodedRequestId -> Ok(TelegramCallback.RequestConfirm decodedRequestId)
                | None -> Error TelegramCallbackCodecError.InvalidAction
            | [| "rq"; "x"; requestId |] ->
                match tryDecodeGuid requestId with
                | Some decodedRequestId -> Ok(TelegramCallback.RequestCancel decodedRequestId)
                | None -> Error TelegramCallbackCodecError.InvalidAction
            | [| "sg"; "s"; trackId |] ->
                match tryDecodeGuid trackId with
                | Some decodedTrackId -> Ok(TelegramCallback.SongSelect decodedTrackId)
                | None -> Error TelegramCallbackCodecError.InvalidAction
            | _ -> Error TelegramCallbackCodecError.InvalidAction

type ITelegramBotClient =
    abstract member SendTextAsync:
        chatId: int64 *
        text: string *
        keyboard: TelegramInlineButton list list option *
        CancellationToken -> Task<Result<unit, TelegramBotError>>

    abstract member SendInvoiceAsync:
        invoice: TelegramStarsInvoice * CancellationToken -> Task<Result<unit, TelegramBotError>>

    abstract member AnswerCallbackAsync:
        callbackQueryId: string *
        text: string option *
        CancellationToken -> Task<Result<unit, TelegramBotError>>

    abstract member AnswerPreCheckoutAsync:
        preCheckoutQueryId: string *
        errorMessage: string option *
        CancellationToken -> Task<Result<unit, TelegramBotError>>

    abstract member GetUpdatesAsync:
        offset: int64 option * CancellationToken -> Task<Result<Update array, TelegramBotError>>

    abstract member DeleteWebhookAsync:
        dropPendingUpdates: bool * CancellationToken -> Task<Result<unit, TelegramBotError>>


[<RequireQualifiedAccess>]
module TelegramText =
    let private commandList locale requestPriceStars sayPriceStars =
        match locale with
        | TelegramLocale.Russian ->
            sprintf
                "/request <название> — заказать трек (%d ⭐)\n/say <текст> — сообщение на экран (%d ⭐)\n/song [название] — текущий трек или ссылка\n/terms — условия оплаты\n/paysupport — поддержка по платежам\n/help — помощь"
                requestPriceStars
                sayPriceStars
        | TelegramLocale.English ->
            sprintf
                "/request <title> — request a track (%d ⭐)\n/say <text> — put a message on screen (%d ⭐)\n/song [title] — current track or link\n/terms — payment terms\n/paysupport — payment support\n/help — help"
                requestPriceStars
                sayPriceStars

    let start locale requestPriceStars sayPriceStars =
        match locale with
        | TelegramLocale.Russian ->
            "Добро пожаловать в Web10.Radio.\n\n" + commandList locale requestPriceStars sayPriceStars
        | TelegramLocale.English ->
            "Welcome to Web10.Radio.\n\n" + commandList locale requestPriceStars sayPriceStars

    let help locale requestPriceStars sayPriceStars =
        commandList locale requestPriceStars sayPriceStars

    let terms locale requestPriceStars sayPriceStars =
        match locale with
        | TelegramLocale.Russian ->
            sprintf
                "Цифровые услуги оплачиваются Telegram Stars. Заказ трека стоит %d ⭐, сообщение на экран — %d ⭐. Услуга активируется только после сообщения Telegram об успешной оплате. Возвраты и споры: /paysupport."
                requestPriceStars
                sayPriceStars
        | TelegramLocale.English ->
            sprintf
                "Digital services are paid with Telegram Stars. A track request costs %d ⭐ and an on-screen message costs %d ⭐. The service is activated only after Telegram reports a successful payment. Refunds and disputes: /paysupport."
                requestPriceStars
                sayPriceStars

    let paysupport locale =
        match locale with
        | TelegramLocale.Russian ->
            "По вопросам оплаты и возврата напишите @netscapedidnothingwrong. Укажите дату, сумму в Stars и описание операции; не отправляйте платёжные данные или коды доступа."
        | TelegramLocale.English ->
            "For payment or refund support, contact @netscapedidnothingwrong. Include the date, Stars amount, and operation description; do not send payment credentials or access codes."

    let requestUsage locale =
        match locale with
        | TelegramLocale.Russian -> "Использование: /request <название>."
        | TelegramLocale.English -> "Usage: /request <title>."

    let sayUsage locale =
        match locale with
        | TelegramLocale.Russian -> "Использование: /say <текст>."
        | TelegramLocale.English -> "Usage: /say <text>."

    let trackNotFound locale =
        match locale with
        | TelegramLocale.Russian -> "Трек не найден."
        | TelegramLocale.English -> "Track not found."

    let requestSentForReview locale =
        match locale with
        | TelegramLocale.Russian -> "Запрос отправлен на проверку администратору."
        | TelegramLocale.English -> "The request was sent for admin review."

    let confirm locale =
        match locale with
        | TelegramLocale.Russian -> "Подтвердить"
        | TelegramLocale.English -> "Confirm"

    let cancel locale =
        match locale with
        | TelegramLocale.Russian -> "Отмена"
        | TelegramLocale.English -> "Cancel"

    let invalidOrExpiredAction locale =
        match locale with
        | TelegramLocale.Russian -> "Действие недоступно или устарело."
        | TelegramLocale.English -> "This action is unavailable or expired."

    let nothingPlaying locale =
        match locale with
        | TelegramLocale.Russian -> "Сейчас ничего не играет."
        | TelegramLocale.English -> "Nothing is playing now."

    let paymentDoesNotMatchExpectedOrder locale =
        match locale with
        | TelegramLocale.Russian -> "Платёж не соответствует ожидаемому заказу."
        | TelegramLocale.English -> "The payment does not match the expected order."

    let privateChatOnly locale =
        match locale with
        | TelegramLocale.Russian -> "Откройте личный чат с ботом для этой команды."
        | TelegramLocale.English -> "Open a private chat with the bot for this command."

    let unmatchedRequestBacklog locale =
        match locale with
        | TelegramLocale.Russian ->
            "Совпадение не найдено. Запрос сохранён без оплаты; автоматическая обработка недоступна."
        | TelegramLocale.English ->
            "No match was found. The request was saved without payment; automatic processing is unavailable."

    let invoiceAlreadyCreated locale =
        match locale with
        | TelegramLocale.Russian -> "Счёт уже создан."
        | TelegramLocale.English -> "The invoice has already been created."

    let requestInvoiceTitle locale =
        match locale with
        | TelegramLocale.Russian -> "Заказ трека"
        | TelegramLocale.English -> "Track request"

    let requestInvoiceDescription locale =
        match locale with
        | TelegramLocale.Russian -> "Заказ трека в Web10.Radio."
        | TelegramLocale.English -> "Track request in Web10.Radio."

    let requestInvoicePriceLabel = requestInvoiceTitle

    let sayInvoiceTitle locale =
        match locale with
        | TelegramLocale.Russian -> "Сообщение на экран"
        | TelegramLocale.English -> "On-screen message"

    let sayInvoiceDescription locale =
        match locale with
        | TelegramLocale.Russian -> "Публикация сообщения на экране Web10.Radio."
        | TelegramLocale.English -> "Publish a message on the Web10.Radio screen."

    let sayInvoicePriceLabel = sayInvoiceTitle

    let trackDisplay artist title =
        artist + " — " + title

    let trackLinkOrDisplay (externalUrl: string option) artist title =
        match externalUrl with
        | Some url when not (String.IsNullOrWhiteSpace url) -> url
        | _ -> trackDisplay artist title

    let requestTrackSelectionPrompt locale =
        match locale with
        | TelegramLocale.Russian -> "Выберите трек."
        | TelegramLocale.English -> "Choose a track."

    let requestConfirmationPrompt locale =
        match locale with
        | TelegramLocale.Russian -> "Подтвердите заказ трека."
        | TelegramLocale.English -> "Confirm the track request."

    let songSelectionPrompt locale =
        match locale with
        | TelegramLocale.Russian -> "Выберите трек."
        | TelegramLocale.English -> "Choose a track."

module private TelegramBotError =
    let create methodName description errorCode isRetryable =
        { Method = methodName
          Description = description
          ErrorCode = errorCode
          IsRetryable = isRetryable }

    let localInvoiceValidation =
        create "sendInvoice" "Telegram Stars invoice validation failed." None false

    let api methodName (error: Funogram.Types.ApiResponseError) =
        let code = error.ErrorCode

        create
            methodName
            "Telegram API request failed."
            (Some code)
            (code = -1 || code = 429 || code >= 500)

    let transportException methodName (exceptionValue: exn) =
        let retryable =
            match exceptionValue with
            | :? HttpRequestException
            | :? TimeoutException
            | :? TaskCanceledException -> true
            | _ -> false

        create methodName "Telegram transport request failed." None retryable

module private TelegramInvoiceValidation =
    let isNonBlankWithin minimum maximum (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length >= minimum && value.Length <= maximum

    let validate (invoice: TelegramStarsInvoice) =
        result {
            if invoice.Currency <> "XTR" then
                return! Error TelegramBotError.localInvoiceValidation
            elif invoice.ProviderToken <> "" then
                return! Error TelegramBotError.localInvoiceValidation
            elif invoice.AmountStars <= 0 then
                return! Error TelegramBotError.localInvoiceValidation
            elif not (isNonBlankWithin 1 32 invoice.Title) then
                return! Error TelegramBotError.localInvoiceValidation
            elif not (isNonBlankWithin 1 255 invoice.Description) then
                return! Error TelegramBotError.localInvoiceValidation
            elif String.IsNullOrWhiteSpace invoice.Payload || Encoding.UTF8.GetByteCount(invoice.Payload) > 128 then
                return! Error TelegramBotError.localInvoiceValidation
            elif String.IsNullOrWhiteSpace invoice.PriceLabel then
                return! Error TelegramBotError.localInvoiceValidation
            else
                return ()
        }

type FunogramTelegramBotClient(config: Funogram.Types.BotConfig) =
    let execute methodName request (cancellationToken: CancellationToken) =
        task {
            try
                let! response =
                    Async.StartAsTask(
                        (request |> Funogram.Api.api config),
                        cancellationToken = cancellationToken
                    )

                cancellationToken.ThrowIfCancellationRequested()

                return
                    match response with
                    | Ok _ -> Ok ()
                    | Error error -> Error(TelegramBotError.api methodName error)
            with exceptionValue ->
                cancellationToken.ThrowIfCancellationRequested()
                return Error(TelegramBotError.transportException methodName exceptionValue)
        }

    let executeResult methodName request (cancellationToken: CancellationToken) : Task<Result<'T, TelegramBotError>> =
        task {
            try
                let! response =
                    Async.StartAsTask(
                        (request |> Funogram.Api.api config),
                        cancellationToken = cancellationToken
                    )

                cancellationToken.ThrowIfCancellationRequested()

                return
                    match response with
                    | Ok value -> Ok value
                    | Error error -> Error(TelegramBotError.api methodName error)
            with exceptionValue ->
                cancellationToken.ThrowIfCancellationRequested()
                return Error(TelegramBotError.transportException methodName exceptionValue)
        }

    let mapKeyboard (keyboard: TelegramInlineButton list list) =
        keyboard
        |> List.map (fun row ->
            row
            |> List.map (fun button -> InlineKeyboardButton.Create(button.Text, callbackData = button.CallbackData))
            |> List.toArray)
        |> List.toArray
        |> InlineKeyboardMarkup.Create
        |> Markup.InlineKeyboardMarkup

    let passThrough operation =
        taskResult {
            return! operation
        }

    interface ITelegramBotClient with
        member _.SendTextAsync(chatId, text, keyboard, cancellationToken) =
            let request =
                match keyboard with
                | Some rows -> Req.SendMessage.Make(chatId, text = text, replyMarkup = mapKeyboard rows)
                | None -> Req.SendMessage.Make(chatId, text = text)

            execute "sendMessage" request cancellationToken |> passThrough

        member _.SendInvoiceAsync(invoice, cancellationToken) =
            match TelegramInvoiceValidation.validate invoice with
            | Error validationError -> Task.FromResult(Error validationError)
            | Ok () ->
                let price = LabeledPrice.Create(invoice.PriceLabel, int64 invoice.AmountStars)

                let request =
                    Req.SendInvoice.Make(
                        invoice.ChatId,
                        invoice.Currency,
                        invoice.Payload,
                        invoice.Description,
                        [| price |],
                        invoice.Title,
                        providerToken = invoice.ProviderToken
                    )

                execute "sendInvoice" request cancellationToken |> passThrough

        member _.AnswerCallbackAsync(callbackQueryId, text, cancellationToken) =
            let request = Req.AnswerCallbackQuery.Make(callbackQueryId, ?text = text)
            execute "answerCallbackQuery" request cancellationToken |> passThrough

        member _.AnswerPreCheckoutAsync(preCheckoutQueryId, errorMessage, cancellationToken) =
            let request =
                match errorMessage with
                | Some message -> Funogram.Telegram.Api.answerPreCheckoutQueryError preCheckoutQueryId message
                | None -> Funogram.Telegram.Api.answerPreCheckoutQuery preCheckoutQueryId

            execute "answerPreCheckoutQuery" request cancellationToken |> passThrough

        member _.GetUpdatesAsync(offset, cancellationToken) =
            let request = Req.GetUpdates.Make(?offset = offset, timeout = 30L)
            executeResult "getUpdates" request cancellationToken

        member _.DeleteWebhookAsync(dropPendingUpdates, cancellationToken) =
            let request = Req.DeleteWebhook.Make(dropPendingUpdates = dropPendingUpdates)
            execute "deleteWebhook" request cancellationToken |> passThrough

