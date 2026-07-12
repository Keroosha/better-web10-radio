namespace Web10.Radio.Telegram

open System
open System.Buffers
open System.Diagnostics
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
open FsToolkit.ErrorHandling
open Funogram.Telegram.Types
open Web10.Radio.Application

type TelegramUpdateProcessingOutcome =
    | TelegramUpdateAccepted
    | TelegramUpdateRejected of message: string
    | TelegramPreCheckoutUnavailable of error: BackgroundWorkerError
    | TelegramIngestFailed of error: BackgroundWorkerError

type TelegramHealthDto =
    { IsConfigured: bool
      ChannelIdOrUsername: string
      LastUpdateId: int64 Nullable
      LastError: string }

[<RequireQualifiedAccess>]
module TelegramSecurity =
    let fixedTimeEqualsUtf8 (expected: string) (actual: string) =
        if isNull expected || isNull actual then false
        else
            let expectedBytes = System.Text.Encoding.UTF8.GetBytes expected
            let actualBytes = System.Text.Encoding.UTF8.GetBytes actual
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes)

[<RequireQualifiedAccess>]
module TelegramJson =
    [<Literal>]
    let JsonContentType = "application/json; charset=utf-8"
    [<Literal>]
    let ProblemContentType = "application/problem+json; charset=utf-8"
    let write (context: HttpContext) status contentType value =
        task {
            context.Response.StatusCode <- status
            context.Response.ContentType <- contentType
            do! JsonSerializer.SerializeAsync(context.Response.Body, value, DomainJson.options, context.RequestAborted)
        } :> Task

[<RequireQualifiedAccess>]
module TelegramProblems =
    type private ProblemDetails =
        { Type: string
          Title: string
          Status: int
          TraceId: string
          Code: string
          Message: string }
    let write (context: HttpContext) (status: int) (code: string) (title: string) (message: string) : Task =
        let current = Activity.Current
        let traceId = if isNull current then context.TraceIdentifier else current.TraceId.ToString()
        TelegramJson.write context status TelegramJson.ProblemContentType
            { Type = sprintf "https://web10.radio/problems/%s" (code.Replace('.', '-'))
              Title = title
              Status = status
              TraceId = traceId
              Code = code
              Message = message }

module TelegramEndpoints =
    let private writeOk (context: HttpContext) value =
        TelegramJson.write context StatusCodes.Status200OK TelegramJson.JsonContentType value

    [<Literal>]
    let TelegramWebhookMaxBodyBytes = 1_048_576

    type private TelegramMappedEvent =
        { EventType: DomainEventType
          PayloadJson: string }

    type private TelegramMappedUpdate =
        | Ignored
        | DurableEvent of TelegramMappedEvent
        | PreCheckout of TelegramPreCheckoutInput

    type private TelegramUserContext =
        { TelegramUserId: int64
          DisplayName: string option
          LanguageCode: string option }

    type private CallbackMessageContext =
        { ChatId: int64
          MessageId: int64 }

    let private webhookSecretHeader (context: HttpContext) =
        let mutable values = StringValues()

        if context.Request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", &values) && values.Count = 1 then
            Some values[0]
        else
            None

    let private tryCommandArgument (expectedCommand: string) (text: string) =
        if String.IsNullOrWhiteSpace text then
            None
        else
            let separatorIndex = text.IndexOfAny([| ' '; '\t'; '\r'; '\n' |])

            let commandToken, argument =
                if separatorIndex < 0 then
                    text, String.Empty
                else
                    text.Substring(0, separatorIndex), text.Substring(separatorIndex + 1).Trim()

            let commandWithoutBot =
                let botSeparatorIndex = commandToken.IndexOf('@')

                if botSeparatorIndex > 0
                   && botSeparatorIndex < commandToken.Length - 1
                   && commandToken.IndexOf('@', botSeparatorIndex + 1) < 0 then
                    commandToken.Substring(0, botSeparatorIndex)
                else
                    commandToken

            if String.Equals(commandWithoutBot, expectedCommand, StringComparison.OrdinalIgnoreCase) then Some argument else None

    let private telegramUserContext (user: User) =
        let displayName =
            match user.Username with
            | Some username when not (String.IsNullOrWhiteSpace username) -> Some username
            | _ ->
                [ Some user.FirstName; user.LastName ]
                |> List.choose id
                |> String.concat " "
                |> fun value -> if String.IsNullOrWhiteSpace value then None else Some value

        { TelegramUserId = user.Id
          DisplayName = displayName
          LanguageCode = user.LanguageCode }

    let private telegramActor (message: Message) =
        message.From |> Option.map telegramUserContext

    let private callbackMessageContext (callbackQuery: CallbackQuery) =
        callbackQuery.Message
        |> Option.map (function
            | MaybeInaccessibleMessage.Message message ->
                { ChatId = message.Chat.Id
                  MessageId = message.MessageId }
            | MaybeInaccessibleMessage.InaccessibleMessage message ->
                { ChatId = message.Chat.Id
                  MessageId = message.MessageId })

    let private isPositiveInt32 (value: int64) =
        value > 0L && value <= int64 Int32.MaxValue

    let private mapTelegramUpdate (update: Update) : Result<TelegramMappedUpdate, string> =
        match update.PreCheckoutQuery with
        | Some preCheckoutQuery when String.IsNullOrWhiteSpace preCheckoutQuery.Id ->
            Error "Telegram pre-checkout query id is required."
        | Some preCheckoutQuery when String.IsNullOrWhiteSpace preCheckoutQuery.InvoicePayload ->
            Error "Telegram pre-checkout invoice payload is required."
        | Some preCheckoutQuery when not (isPositiveInt32 preCheckoutQuery.TotalAmount) ->
            Error "Telegram pre-checkout amount must be a positive supported integer."
        | Some preCheckoutQuery ->
            let user = telegramUserContext preCheckoutQuery.From

            Ok(
                PreCheckout
                    { TelegramUpdateId = update.UpdateId
                      QueryId = preCheckoutQuery.Id
                      TelegramUserId = user.TelegramUserId
                      LanguageCode = user.LanguageCode
                      Currency = preCheckoutQuery.Currency
                      TotalAmount = int preCheckoutQuery.TotalAmount
                      InvoicePayload = preCheckoutQuery.InvoicePayload }
            )
        | None ->
            match update.Message |> Option.bind (fun message -> message.SuccessfulPayment |> Option.map (fun payment -> message, payment)) with
            | Some(message, payment) when String.IsNullOrWhiteSpace payment.InvoicePayload ->
                Error "Telegram payment invoice payload is required."
            | Some(message, payment) when String.IsNullOrWhiteSpace payment.TelegramPaymentChargeId ->
                Error "Telegram payment charge id is required."
            | Some(message, payment) when not (isPositiveInt32 payment.TotalAmount) ->
                Error "Telegram payment amount must be a positive supported integer."
            | Some(message, payment) ->
                let actor = telegramActor message

                let payload =
                    JsonSerializer.Serialize(
                        {| paymentId = payment.InvoicePayload
                           telegramPaymentChargeId = payment.TelegramPaymentChargeId
                           amountStars = int payment.TotalAmount
                           currency = payment.Currency
                           telegramUpdateId = update.UpdateId
                           telegramMessageId = message.MessageId
                           chatId = message.Chat.Id
                           telegramUserId = actor |> Option.map (fun value -> value.TelegramUserId)
                           displayName = actor |> Option.bind (fun value -> value.DisplayName)
                           languageCode = actor |> Option.bind (fun value -> value.LanguageCode) |},
                        DomainJson.options
                    )

                Ok(DurableEvent { EventType = DomainEventType.DonationPaid; PayloadJson = payload })
            | None ->
                match update.CallbackQuery with
                | Some callbackQuery when String.IsNullOrWhiteSpace callbackQuery.Id ->
                    Error "Telegram callback query id is required."
                | Some callbackQuery ->
                    let actor = telegramUserContext callbackQuery.From
                    let message = callbackMessageContext callbackQuery
                    let chatId = message |> Option.map (fun value -> value.ChatId)
                    let isPrivateChat = chatId = Some actor.TelegramUserId

                    let payload =
                        JsonSerializer.Serialize(
                            {| telegramUpdateId = update.UpdateId
                               telegramMessageId = message |> Option.map (fun value -> value.MessageId)
                               chatId = chatId
                               telegramUserId = actor.TelegramUserId
                               displayName = actor.DisplayName
                               languageCode = actor.LanguageCode
                               callbackQueryId = callbackQuery.Id
                               rawCallbackData = callbackQuery.Data
                               isPrivateChat = isPrivateChat |},
                            DomainJson.options
                        )

                    Ok(DurableEvent { EventType = DomainEventType.TelegramCallbackReceived; PayloadJson = payload })
                | None ->
                    match update.Message with
                    | Some message ->
                        match message.Text, telegramActor message with
                        | Some text, Some actor ->
                            let chatId = message.Chat.Id
                            let isPrivateChat = chatId = actor.TelegramUserId

                            let commandEvent eventType command argument =
                                let payload =
                                    JsonSerializer.Serialize(
                                        {| telegramUpdateId = update.UpdateId
                                           telegramMessageId = message.MessageId
                                           chatId = chatId
                                           telegramUserId = actor.TelegramUserId
                                           displayName = actor.DisplayName
                                           languageCode = actor.LanguageCode
                                           command = command
                                           argument = argument
                                           isPrivateChat = isPrivateChat |},
                                        DomainJson.options
                                    )

                                DurableEvent { EventType = eventType; PayloadJson = payload }

                            match
                                tryCommandArgument "/request" text,
                                tryCommandArgument "/say" text,
                                tryCommandArgument "/start" text,
                                tryCommandArgument "/help" text,
                                tryCommandArgument "/song" text,
                                tryCommandArgument "/terms" text,
                                tryCommandArgument "/paysupport" text
                            with
                            | Some query, _, _, _, _, _, _ ->
                                let payload =
                                    JsonSerializer.Serialize(
                                        {| telegramUpdateId = update.UpdateId
                                           telegramMessageId = message.MessageId
                                           chatId = chatId
                                           telegramUserId = actor.TelegramUserId
                                           displayName = actor.DisplayName
                                           languageCode = actor.LanguageCode
                                           command = "/request"
                                           argument = query
                                           query = query
                                           isPrivateChat = isPrivateChat |},
                                        DomainJson.options
                                    )

                                Ok(DurableEvent { EventType = DomainEventType.TrackRequested; PayloadJson = payload })
                            | _, Some submittedText, _, _, _, _, _ ->
                                let payload =
                                    JsonSerializer.Serialize(
                                        {| telegramUpdateId = update.UpdateId
                                           telegramMessageId = message.MessageId
                                           chatId = chatId
                                           telegramUserId = actor.TelegramUserId
                                           displayName = actor.DisplayName
                                           languageCode = actor.LanguageCode
                                           command = "/say"
                                           argument = submittedText
                                           text = submittedText
                                           isPrivateChat = isPrivateChat |},
                                        DomainJson.options
                                    )

                                Ok(DurableEvent { EventType = DomainEventType.SayMessageSubmitted; PayloadJson = payload })
                            | _, _, Some argument, _, _, _, _ ->
                                commandEvent DomainEventType.TelegramCommandReceived "/start" argument |> Ok
                            | _, _, _, Some argument, _, _, _ ->
                                commandEvent DomainEventType.TelegramCommandReceived "/help" argument |> Ok
                            | _, _, _, _, Some argument, _, _ ->
                                commandEvent DomainEventType.TelegramCommandReceived "/song" argument |> Ok
                            | _, _, _, _, _, Some argument, _ ->
                                commandEvent DomainEventType.TelegramCommandReceived "/terms" argument |> Ok
                            | _, _, _, _, _, _, Some argument ->
                                commandEvent DomainEventType.TelegramCommandReceived "/paysupport" argument |> Ok
                            | _ -> Ok Ignored
                        | _ -> Ok Ignored
                    | None -> Ok Ignored

    let private readBoundedBody (maximumBytes: int) (context: HttpContext) (buffer: byte array) =
        task {
            let mutable total = 0
            let mutable complete = false

            while not complete && total <= maximumBytes do
                let remaining = maximumBytes + 1 - total
                let! bytesRead = context.Request.Body.ReadAsync(buffer.AsMemory(total, remaining), context.RequestAborted)

                if bytesRead = 0 then
                    complete <- true
                else
                    total <- total + bytesRead

            return if total > maximumBytes then None else Some total
        }

    type private BoundedJsonBody =
        | BodyTooLarge
        | BodyInvalid
        | BodyParsed of JsonElement

    let private readJsonBody maximumBytes (context: HttpContext) =
        task {
            if context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > int64 maximumBytes then
                return BodyTooLarge
            else
                let buffer = ArrayPool<byte>.Shared.Rent(maximumBytes + 1)

                use _bufferLease =
                    { new IDisposable with
                        member _.Dispose() = ArrayPool<byte>.Shared.Return(buffer, clearArray = true) }

                let! length = readBoundedBody maximumBytes context buffer

                match length with
                | None -> return BodyTooLarge
                | Some bodyLength ->
                    try
                        use document = JsonDocument.Parse(buffer.AsMemory(0, bodyLength))
                        return BodyParsed(document.RootElement.Clone())
                    with :? JsonException ->
                        return BodyInvalid
        }

    let private hasExactProperties (expected: Set<string>) (root: JsonElement) =
        root.ValueKind = JsonValueKind.Object
        && (root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) = expected

    let private tryProperty (name: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty(name, &value) then Some value else None

    let private tryString (name: string) (root: JsonElement) =
        match tryProperty name root with
        | Some value when value.ValueKind = JsonValueKind.String ->
            let text = value.GetString()
            if isNull text then None else Some text
        | _ -> None

    let private tryNullableString (name: string) (root: JsonElement) =
        match tryProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Null -> Some None
        | Some value when value.ValueKind = JsonValueKind.String ->
            let text = value.GetString()
            if isNull text then None else Some(Some text)
        | _ -> None

    let private tryPositiveGuid (value: string) =
        let mutable parsed = Guid.Empty
        if Guid.TryParse(value, &parsed) && parsed <> Guid.Empty then Some parsed else None

    let private writeRequestTooLarge context =
        TelegramProblems.write context StatusCodes.Status413PayloadTooLarge "request.too_large" "Request body too large" "Request body exceeds the maximum allowed size."

    let private writeRepositoryFailure context =
        TelegramProblems.write context StatusCodes.Status500InternalServerError "repository.write_failed" "Repository write failed" "The requested change could not be persisted."

    let private writeDomainProblem context status code title message =
        TelegramProblems.write context status code title message

    let private parseGuidRoute routeValue (context: HttpContext) =
        match context.Request.RouteValues[routeValue] with
        | null -> None
        | value -> tryPositiveGuid (string value)

    /// Classify a parsed Telegram update and route it through the shared ingestion path
    /// (synchronous pre-checkout workflow, or durable-event inbox deduped by (updateId, eventType)).
    /// Reused verbatim by the webhook endpoint and the long-polling worker so payment/command
    /// handling never forks. Records adapter-state progress/errors as a side effect.
    let processTelegramUpdate (services: IServiceProvider) (update: Update) (cancellationToken: CancellationToken) : Task<TelegramUpdateProcessingOutcome> =
        task {
            use attempt = FlowTelemetry.start FlowTelemetry.TelegramUpdate
            FlowTelemetry.addTag "telegram.update_id" (box update.UpdateId) attempt

            let finish outcome =
                FlowTelemetry.finish outcome [] attempt |> ignore

            try
                let state = services.GetRequiredService<ITelegramAdapterState>()

                match mapTelegramUpdate update with
                | Error message ->
                    state.RecordError("request.invalid")
                    finish "rejected"
                    return TelegramUpdateRejected message
                | Ok Ignored ->
                    FlowTelemetry.addTag "event.type" (box "ignored") attempt
                    state.RecordUpdate(update.UpdateId)
                    finish "ignored"
                    return TelegramUpdateAccepted
                | Ok(PreCheckout preCheckout) ->
                    FlowTelemetry.addTag "event.type" (box "pre_checkout") attempt
                    let workflow = services.GetRequiredService<ITelegramPreCheckoutWorkflow>()
                    let! result = workflow.HandleAsync preCheckout cancellationToken

                    match result with
                    | Ok() ->
                        state.RecordUpdate(update.UpdateId)
                        finish "accepted"
                        return TelegramUpdateAccepted
                    | Error error ->
                        state.RecordError("telegram.pre_checkout_unavailable")
                        finish "error"
                        return TelegramPreCheckoutUnavailable error
                | Ok(DurableEvent event) ->
                    FlowTelemetry.addTag "event.type" (box (DomainEventType.toString event.EventType)) attempt
                    let ingestor = services.GetRequiredService<ITelegramUpdateEventIngestor>()

                    let! ingestResult =
                        ingestor.TryIngestAsync update.UpdateId event.EventType "Web10.Radio.Telegram" event.PayloadJson cancellationToken

                    match ingestResult with
                    | Ok true ->
                        state.RecordUpdate(update.UpdateId)
                        finish "accepted"
                        return TelegramUpdateAccepted
                    | Ok false ->
                        state.RecordUpdate(update.UpdateId)
                        finish "duplicate"
                        return TelegramUpdateAccepted
                    | Error error ->
                        state.RecordError("telegram.webhook.ingest_failed")
                        finish "error"
                        return TelegramIngestFailed error
            with error ->
                FlowTelemetry.finishError "error" [] error attempt |> ignore
                return raise error
        }

    let private telegramWebhook (context: HttpContext) =
        task {
            let telegramOptions = context.RequestServices.GetRequiredService<TelegramOptions>()
            let state = context.RequestServices.GetRequiredService<ITelegramAdapterState>()

            match webhookSecretHeader context with
            | None ->
                state.RecordError("telegram.webhook.secret_invalid")
                do!
                    TelegramProblems.write
                        context
                        StatusCodes.Status401Unauthorized
                        "telegram.webhook.secret_invalid"
                        "Telegram webhook unauthorized"
                        "Exactly one Telegram webhook secret token header is required."

                return StatusCodes.Status401Unauthorized
            | Some suppliedSecret when not (TelegramSecurity.fixedTimeEqualsUtf8 telegramOptions.WebhookSecret suppliedSecret) ->
                state.RecordError("telegram.webhook.secret_invalid")
                do!
                    TelegramProblems.write
                        context
                        StatusCodes.Status401Unauthorized
                        "telegram.webhook.secret_invalid"
                        "Telegram webhook unauthorized"
                        "Telegram webhook secret token is invalid."

                return StatusCodes.Status401Unauthorized
            | Some _ ->
                if context.Request.ContentLength.HasValue
                   && context.Request.ContentLength.Value > int64 TelegramWebhookMaxBodyBytes then
                    state.RecordError("request.too_large")

                    do!
                        TelegramProblems.write
                            context
                            StatusCodes.Status413PayloadTooLarge
                            "request.too_large"
                            "Request body too large"
                            "Telegram webhook body exceeds the maximum allowed size."

                    return StatusCodes.Status413PayloadTooLarge
                else
                    let buffer = ArrayPool<byte>.Shared.Rent(TelegramWebhookMaxBodyBytes + 1)
                    use _bufferLease =
                        { new IDisposable with
                            member _.Dispose() = ArrayPool<byte>.Shared.Return(buffer, clearArray = true) }


                    try
                        let! bodyLength = readBoundedBody TelegramWebhookMaxBodyBytes context buffer

                        match bodyLength with
                        | None ->
                            state.RecordError("request.too_large")

                            do!
                                TelegramProblems.write
                                    context
                                    StatusCodes.Status413PayloadTooLarge
                                    "request.too_large"
                                    "Request body too large"
                                    "Telegram webhook body exceeds the maximum allowed size."

                            return StatusCodes.Status413PayloadTooLarge
                        | Some length ->
                            match TelegramUpdateJson.tryParse buffer length with
                            | Error message ->
                                state.RecordError("request.invalid")
                                do! TelegramProblems.write context StatusCodes.Status400BadRequest "request.invalid" "Invalid request" message
                                return StatusCodes.Status400BadRequest
                            | Ok update ->
                                let! outcome = processTelegramUpdate context.RequestServices update context.RequestAborted

                                match outcome with
                                | TelegramUpdateAccepted ->
                                    context.Response.StatusCode <- StatusCodes.Status204NoContent
                                    return StatusCodes.Status204NoContent
                                | TelegramUpdateRejected message ->
                                    do! TelegramProblems.write context StatusCodes.Status400BadRequest "request.invalid" "Invalid request" message
                                    return StatusCodes.Status400BadRequest
                                | TelegramPreCheckoutUnavailable error ->
                                    do!
                                        TelegramProblems.write
                                            context
                                            StatusCodes.Status503ServiceUnavailable
                                            "telegram.pre_checkout_unavailable"
                                            "Telegram pre-checkout unavailable"
                                            (BackgroundWorkerError.toMessage error)

                                    return StatusCodes.Status503ServiceUnavailable
                                | TelegramIngestFailed error ->
                                    do!
                                        TelegramProblems.write
                                            context
                                            StatusCodes.Status500InternalServerError
                                            "telegram.webhook.ingest_failed"
                                            "Telegram webhook failed"
                                            (BackgroundWorkerError.toMessage error)

                                    return StatusCodes.Status500InternalServerError
                    with error ->
                        state.RecordError("telegram.webhook.unhandled")
                        return raise error
        }

    let private telegramHealth (context: HttpContext) =
        task {
            let state = context.RequestServices.GetRequiredService<ITelegramAdapterState>()
            let snapshot = state.Snapshot()
            let lastUpdateId =
                match snapshot.LastUpdateId with
                | Some value -> Nullable<int64>(value)
                | None -> Nullable<int64>()

            let dto: TelegramHealthDto =
                { IsConfigured = snapshot.IsConfigured
                  ChannelIdOrUsername = snapshot.ChannelIdOrUsername
                  LastUpdateId = lastUpdateId
                  LastError = snapshot.LastError |> Option.defaultValue null }

            do! writeOk context dto
            return StatusCodes.Status200OK
        }


    let mapTelegramEndpoints (app: WebApplication) =
        let requestDelegate (handler: HttpContext -> Task<int>) =
            RequestDelegate(fun context ->
                task {
                    let! _ = handler context
                    return ()
                }
                :> Task)
        let map (methodName: string) (route: string) (handler: HttpContext -> Task<int>) =
            app.MapMethods(route, [| methodName |], requestDelegate handler) |> ignore
        map "POST" "/api/v0/telegram/webhook" telegramWebhook
        map "GET" "/api/v0/telegram/health" telegramHealth
