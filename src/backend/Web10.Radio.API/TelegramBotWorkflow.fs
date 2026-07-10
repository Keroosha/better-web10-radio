namespace Web10.Radio.API

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open FsToolkit.ErrorHandling
open Npgsql
open Web10.Radio.Database
open Web10.Radio.Database.Repositories
open Web10.Radio.Telegram

type private TelegramInteraction =
    { TelegramUpdateId: int64
      ChatId: int64
      TelegramUserId: int64
      DisplayName: string option
      LanguageCode: string option
      Command: string
      Argument: string option
      IsPrivateChat: bool }

type private TelegramCallbackInteraction =
    { TelegramUpdateId: int64
      ChatId: int64 option
      TelegramUserId: int64
      DisplayName: string option
      LanguageCode: string option
      CallbackQueryId: string
      RawCallbackData: string
      IsPrivateChat: bool }

type private DonationInvoice =
    { PaymentId: Guid
      Purpose: PaymentPurpose
      PurposeEntityId: Guid
      ChatId: int64
      Title: string
      Description: string
      PriceLabel: string
      AmountStars: int
      Currency: string
      ProviderToken: string }

[<RequireQualifiedAccess>]
module private TelegramWorkflowJson =
    let parseObject eventType payloadJson =
        if String.IsNullOrWhiteSpace payloadJson then
            Error(InvalidPayload(eventType, "payload must be a JSON object."))
        else
            try
                use document = JsonDocument.Parse(payloadJson)

                if document.RootElement.ValueKind = JsonValueKind.Object then
                    Ok(document.RootElement.Clone())
                else
                    Error(InvalidPayload(eventType, "payload must be a JSON object."))
            with :? JsonException as ex ->
                Error(InvalidPayload(eventType, ex.Message))

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then Some value else None

    let requiredString eventType name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.String ->
            match value.GetString() with
            | value when not (String.IsNullOrWhiteSpace value) -> Ok value
            | _ -> Error(InvalidPayload(eventType, sprintf "%s is required." name))
        | _ -> Error(InvalidPayload(eventType, sprintf "%s is required." name))

    let optionalString name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.String ->
            value.GetString() |> Option.ofObj
        | Some value when value.ValueKind = JsonValueKind.Null -> None
        | _ -> None

    let requiredInt64 eventType name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.Number ->
            let mutable parsed = 0L
            if value.TryGetInt64(&parsed) then Ok parsed else Error(InvalidPayload(eventType, sprintf "%s must be an integer." name))
        | _ -> Error(InvalidPayload(eventType, sprintf "%s is required." name))

    let requiredInt eventType name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.Number ->
            let mutable parsed = 0
            if value.TryGetInt32(&parsed) then Ok parsed else Error(InvalidPayload(eventType, sprintf "%s must be a 32-bit integer." name))
        | _ -> Error(InvalidPayload(eventType, sprintf "%s is required." name))

    let requiredBool eventType name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.True -> Ok true
        | Some value when value.ValueKind = JsonValueKind.False -> Ok false
        | _ -> Error(InvalidPayload(eventType, sprintf "%s is required." name))

    let requiredGuid eventType name element =
        result {
            let! text = requiredString eventType name element
            let mutable parsed = Guid.Empty
            do! Guid.TryParse(text, &parsed) |> Result.requireTrue (InvalidPayload(eventType, sprintf "%s must be a UUID." name))
            return parsed
        }

    let interaction eventType payloadJson =
        result {
            let! root = parseObject eventType payloadJson
            let! updateId = requiredInt64 eventType "telegramUpdateId" root
            let! chatId = requiredInt64 eventType "chatId" root
            let! userId = requiredInt64 eventType "telegramUserId" root
            let! command = requiredString eventType "command" root
            let! isPrivateChat = requiredBool eventType "isPrivateChat" root
            do! (updateId >= 0L) |> Result.requireTrue (InvalidPayload(eventType, "telegramUpdateId must be non-negative."))
            do! (chatId <> 0L) |> Result.requireTrue (InvalidPayload(eventType, "chatId must not be zero."))
            do! (userId > 0L) |> Result.requireTrue (InvalidPayload(eventType, "telegramUserId must be positive."))
            return
                { TelegramUpdateId = updateId
                  ChatId = chatId
                  TelegramUserId = userId
                  DisplayName = optionalString "displayName" root
                  LanguageCode = optionalString "languageCode" root
                  Command = command
                  Argument = optionalString "argument" root
                  IsPrivateChat = isPrivateChat }
        }

    let callback eventType payloadJson =
        result {
            let! root = parseObject eventType payloadJson
            let! updateId = requiredInt64 eventType "telegramUpdateId" root
            let! userId = requiredInt64 eventType "telegramUserId" root
            let! callbackQueryId = requiredString eventType "callbackQueryId" root
            let! rawCallbackData = requiredString eventType "rawCallbackData" root
            let! isPrivateChat = requiredBool eventType "isPrivateChat" root
            do! (updateId >= 0L) |> Result.requireTrue (InvalidPayload(eventType, "telegramUpdateId must be non-negative."))
            do! (userId > 0L) |> Result.requireTrue (InvalidPayload(eventType, "telegramUserId must be positive."))
            let chatId =
                match tryProperty "chatId" root with
                | Some value when value.ValueKind = JsonValueKind.Number ->
                    let mutable parsed = 0L
                    if value.TryGetInt64(&parsed) then Some parsed else None
                | _ -> None
            return
                { TelegramUpdateId = updateId
                  ChatId = chatId
                  TelegramUserId = userId
                  DisplayName = optionalString "displayName" root
                  LanguageCode = optionalString "languageCode" root
                  CallbackQueryId = callbackQueryId
                  RawCallbackData = rawCallbackData
                  IsPrivateChat = isPrivateChat }
        }

    let invoice eventType payloadJson =
        result {
            let! root = parseObject eventType payloadJson
            let! paymentId = requiredGuid eventType "paymentId" root
            let! purposeText = requiredString eventType "purpose" root
            let! purposeEntityId = requiredGuid eventType "purposeEntityId" root
            let! chatId = requiredInt64 eventType "chatId" root
            let! title = requiredString eventType "title" root
            let! description = requiredString eventType "description" root
            let! priceLabel = requiredString eventType "priceLabel" root
            let! amountStars = requiredInt eventType "amountStars" root
            let! currency = requiredString eventType "currency" root
            let providerToken =
                match tryProperty "providerToken" root with
                | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
                | _ -> "\u0000"
            let purpose =
                match purposeText with
                | "Request" -> Ok Request
                | "Say" -> Ok Say
                | _ -> Error(InvalidPayload(eventType, "purpose must be Request or Say."))
            let! purpose = purpose
            do! (chatId > 0L) |> Result.requireTrue (InvalidPayload(eventType, "chatId must be positive."))
            do! (amountStars > 0) |> Result.requireTrue (InvalidPayload(eventType, "amountStars must be positive."))
            do! (currency = "XTR") |> Result.requireTrue (InvalidPayload(eventType, "currency must be XTR."))
            do! (providerToken = "") |> Result.requireTrue (InvalidPayload(eventType, "providerToken must be empty."))
            return
                { PaymentId = paymentId
                  Purpose = purpose
                  PurposeEntityId = purposeEntityId
                  ChatId = chatId
                  Title = title
                  Description = description
                  PriceLabel = priceLabel
                  AmountStars = amountStars
                  Currency = currency
                  ProviderToken = providerToken }
        }

type private SearchClassification =
    | Confident of TrackSearchMatch
    | Suggestions of TrackSearchMatch list
    | NoMatch

[<RequireQualifiedAccess>]
module private TelegramSearch =
    let private normalized (value: string) =
        if isNull value then String.Empty else value.Trim()

    let classify query (matches: TrackSearchMatch list) =
        let query = normalized query
        let exact =
            matches
            |> List.filter (fun track ->
                String.Equals(normalized track.Title, query, StringComparison.OrdinalIgnoreCase)
                || String.Equals(normalized (TelegramText.trackDisplay track.Artist track.Title), query, StringComparison.OrdinalIgnoreCase))

        match exact with
        | [ track ] -> Confident track
        | _ :: _ :: _ -> Suggestions exact
        | [] ->
            match matches with
            | [ track ] when track.Similarity >= 0.70 -> Confident track
            | _ :: _ :: _ -> Suggestions matches
            | _ -> NoMatch

type ITelegramPreCheckoutWorkflow =
    abstract member HandleAsync:
        TelegramPreCheckoutInput -> CancellationToken -> Task<Result<unit, BackgroundWorkerError>>

type TelegramBotWorkflow
    (
        dataSource: NpgsqlDataSource,
        idGenerator: IIdGenerator,
        clock: IClock,
        options: TelegramOptions,
        telegramClient: ITelegramBotClient,
        adapterState: ITelegramAdapterState,
        logger: ILogger<TelegramBotWorkflow>
    ) =

    let locale languageCode = TelegramLocale.ofLanguageCode languageCode

    let mapRepository result = result |> Result.mapError RepositoryError

    let appendDerived
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (source: DomainEventEnvelope)
        eventType
        payloadJson
        cancellationToken =
        task {
            match DomainEventEnvelope.create idGenerator clock eventType "Web10.Radio.API.TelegramBot" (Some source.CorrelationId) (Some source.EventId) payloadJson with
            | Error error -> return Error(DomainEventError error)
            | Ok derived ->
                let! appended = OutboxEventRepository.appendInTransaction connection transaction (OutboxMapping.toOutboxEvent derived) cancellationToken
                return appended |> mapRepository
        }

    let invoicePayloadJson (order: PaymentOrderToCreate) chatId locale =
        let title, description, priceLabel =
            match order.Purpose with
            | Request -> TelegramText.requestInvoiceTitle locale, TelegramText.requestInvoiceDescription locale, TelegramText.requestInvoicePriceLabel locale
            | Say -> TelegramText.sayInvoiceTitle locale, TelegramText.sayInvoiceDescription locale, TelegramText.sayInvoicePriceLabel locale
            | Donation -> failwith "Donation invoices are not a Telegram bot workflow command."

        JsonSerializer.Serialize(
            {| paymentId = order.Id.ToString("D")
               purpose = (match order.Purpose with | Request -> "Request" | Say -> "Say" | Donation -> "Donation")
               purposeEntityId = order.PurposeEntityId |> Option.map (fun id -> id.ToString("D")) |> Option.defaultValue String.Empty
               chatId = chatId
               title = title
               description = description
               priceLabel = priceLabel
               amountStars = order.AmountStars
               currency = "XTR"
               providerToken = "" |}
        )

    let matchedPayload (requestId: Guid) (trackId: Guid) =
        JsonSerializer.Serialize({| trackRequestId = requestId.ToString("D"); trackId = trackId.ToString("D") |})

    let terminalTransport
        (envelope: DomainEventEnvelope)
        telegramUpdateId
        telegramUserId
        action
        paymentId
        (operation: Task<Result<unit, TelegramBotError>>) =
        task {
            let! result = operation
            match result with
            | Ok () -> return Ok ()
            | Error transportError when transportError.IsRetryable ->
                return Error(TelegramTransportError(transportError.Method, transportError.Description))
            | Error transportError ->
                adapterState.RecordError("Telegram outbound terminal failure.")
                logger.LogWarning(
                    "Telegram terminal outbound failure for event {eventId}, correlation {correlationId}, update {telegramUpdateId}, user {telegramUserId}, action {action}, payment {paymentId}, method {methodName}, code {errorCode}.",
                    envelope.EventId,
                    envelope.CorrelationId,
                    telegramUpdateId,
                    telegramUserId,
                    action,
                    paymentId,
                    transportError.Method,
                    transportError.ErrorCode
                )
                return Ok ()
        }

    let sendText envelope updateId userId action chatId text keyboard cancellationToken =
        terminalTransport envelope updateId userId action None (telegramClient.SendTextAsync(chatId, text, keyboard, cancellationToken))

    let answerCallback envelope callback action text cancellationToken =
        terminalTransport envelope callback.TelegramUpdateId callback.TelegramUserId action None (telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, text, cancellationToken))

    let preCheckoutUnavailable methodName description =
        adapterState.RecordError("telegram.pre_checkout_unavailable")
        TelegramTransportError(methodName, description)

    let confirmationKeyboard requestId locale =
        match TelegramCallback.encode (TelegramCallback.RequestConfirm requestId), TelegramCallback.encode (TelegramCallback.RequestCancel requestId) with
        | Ok confirm, Ok cancel ->
            Some [ [ { Text = TelegramText.confirm locale; CallbackData = confirm }; { Text = TelegramText.cancel locale; CallbackData = cancel } ] ]
        | _ -> None

    let selectionKeyboard requestId (tracks: TrackSearchMatch list) =
        tracks
        |> List.choose (fun track ->
            match TelegramCallback.encode (TelegramCallback.RequestSelect(requestId, track.Id)) with
            | Ok data -> Some [ { Text = TelegramText.trackDisplay track.Artist track.Title; CallbackData = data } ]
            | Error _ -> None)
        |> Some

    let songKeyboard (tracks: TrackSearchMatch list) =
        tracks
        |> List.choose (fun track ->
            match TelegramCallback.encode (TelegramCallback.SongSelect track.Id) with
            | Ok data -> Some [ { Text = TelegramText.trackDisplay track.Artist track.Title; CallbackData = data } ]
            | Error _ -> None)
        |> Some

    let sendConfirmation (envelope: DomainEventEnvelope) (interaction: TelegramInteraction) (requestId: Guid) cancellationToken =
        let currentLocale = locale interaction.LanguageCode
        sendText
            envelope
            interaction.TelegramUpdateId
            interaction.TelegramUserId
            "request-confirmation"
            interaction.ChatId
            (TelegramText.requestConfirmationPrompt currentLocale)
            (confirmationKeyboard requestId currentLocale)
            cancellationToken

    let tryCreateMatchedRequest (envelope: DomainEventEnvelope) (interaction: TelegramInteraction) (request: TrackRequestToCreate) (track: TrackSearchMatch) cancellationToken =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    let! created = TelegramCommandRepository.createMatchedTrackRequestInTransaction connection transaction request track.Id token
                    match created |> mapRepository with
                    | Error error -> return Error error
                    | Ok(CommandOutcome.Created _) ->
                        let! appended = appendDerived connection transaction envelope TrackRequestMatched (matchedPayload request.Id track.Id) token
                        return appended |> Result.map (fun () -> CommandOutcome.Created track.Id)
                    | Ok(CommandOutcome.AlreadyApplied _) -> return Ok(CommandOutcome.AlreadyApplied track.Id)
                    | Ok(CommandOutcome.Rejected reason) -> return Ok(CommandOutcome.Rejected reason)
                })
            cancellationToken

    let tryCreateNeedsReview (request: TrackRequestToCreate) cancellationToken =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    let! outcome = TelegramCommandRepository.createNeedsReviewTrackRequestInTransaction connection transaction request token
                    return outcome |> mapRepository
                })
            cancellationToken

    let handleTrackRequested (envelope: DomainEventEnvelope) (interaction: TelegramInteraction) (query: string) cancellationToken =
        task {
            let currentLocale = locale interaction.LanguageCode
            let query = if isNull query then String.Empty else query.Trim()

            if not interaction.IsPrivateChat then
                return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "request-private" interaction.ChatId (TelegramText.privateChatOnly currentLocale) None cancellationToken
            elif String.IsNullOrWhiteSpace query then
                return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "request-usage" interaction.ChatId (TelegramText.requestUsage currentLocale) None cancellationToken
            else
                let request =
                    { Id = envelope.EventId
                      TelegramUserId = Some interaction.TelegramUserId
                      DisplayName = interaction.DisplayName
                      Query = query
                      RequestedAtUtc = envelope.OccurredAtUtc
                      CorrelationId = envelope.CorrelationId }

                let! existing = TelegramCommandRepository.tryGetActiveRequest dataSource envelope.EventId cancellationToken
                match existing |> mapRepository with
                | Error error -> return Error error
                | Ok(Some snapshot) when snapshot.TelegramUserId <> Some interaction.TelegramUserId ->
                    return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "request-invalid" interaction.ChatId (TelegramText.invalidOrExpiredAction currentLocale) None cancellationToken
                | Ok(Some snapshot) when snapshot.Status = "Matched" && snapshot.MatchedTrackId.IsSome ->
                    return! sendConfirmation envelope interaction envelope.EventId cancellationToken
                | Ok(Some snapshot) when snapshot.Status <> "NeedsReview" ->
                    return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "request-invalid" interaction.ChatId (TelegramText.invalidOrExpiredAction currentLocale) None cancellationToken
                | _ ->
                    let! matches = TrackRepository.searchActive dataSource query 5 cancellationToken
                    match matches |> mapRepository with
                    | Error error -> return Error error
                    | Ok matches ->
                        match TelegramSearch.classify query matches with
                        | Confident track ->
                            let! outcome = tryCreateMatchedRequest envelope interaction request track cancellationToken
                            match outcome with
                            | Error error -> return Error error
                            | Ok(CommandOutcome.Rejected _) ->
                                return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "request-invalid" interaction.ChatId (TelegramText.invalidOrExpiredAction currentLocale) None cancellationToken
                            | Ok _ -> return! sendConfirmation envelope interaction envelope.EventId cancellationToken
                        | Suggestions tracks ->
                            let! outcome = tryCreateNeedsReview request cancellationToken
                            match outcome with
                            | Error error -> return Error error
                            | Ok(CommandOutcome.Rejected _) ->
                                return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "request-invalid" interaction.ChatId (TelegramText.invalidOrExpiredAction currentLocale) None cancellationToken
                            | Ok _ ->
                                return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "request-selection" interaction.ChatId (TelegramText.requestTrackSelectionPrompt currentLocale) (selectionKeyboard envelope.EventId tracks) cancellationToken
                        | NoMatch ->
                            let! outcome = tryCreateNeedsReview request cancellationToken
                            match outcome with
                            | Error error -> return Error error
                            | Ok(CommandOutcome.Rejected _) ->
                                return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "request-invalid" interaction.ChatId (TelegramText.invalidOrExpiredAction currentLocale) None cancellationToken
                            | Ok _ ->
                                return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "request-backlog" interaction.ChatId (TelegramText.unmatchedRequestBacklog currentLocale) None cancellationToken
        }

    let handleSay (envelope: DomainEventEnvelope) (interaction: TelegramInteraction) (text: string) cancellationToken =
        task {
            let currentLocale = locale interaction.LanguageCode
            let text = if isNull text then String.Empty else text.Trim()

            if not interaction.IsPrivateChat then
                return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "say-private" interaction.ChatId (TelegramText.privateChatOnly currentLocale) None cancellationToken
            elif String.IsNullOrWhiteSpace text then
                return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "say-usage" interaction.ChatId (TelegramText.sayUsage currentLocale) None cancellationToken
            else
                let message =
                    { Id = envelope.EventId
                      TelegramUserId = Some interaction.TelegramUserId
                      DisplayName = interaction.DisplayName |> Option.defaultValue "Telegram user"
                      Text = text
                      SubmittedAtUtc = envelope.OccurredAtUtc }

                let! outcome =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction token ->
                            task {
                                let! existing = TelegramCommandRepository.tryGetActiveOrderForPurposeInTransaction connection transaction Say message.Id token
                                match existing |> mapRepository with
                                | Error error -> return Error error
                                | Ok(Some order) -> return Ok(CommandOutcome.AlreadyApplied order)
                                | Ok None ->
                                    let paymentId = idGenerator.NewId()
                                    let order =
                                        { Id = paymentId
                                          TelegramUserId = interaction.TelegramUserId
                                          Purpose = Say
                                          PurposeEntityId = Some message.Id
                                          AmountStars = options.SayPriceStars
                                          InvoicePayload = paymentId.ToString("D")
                                          PayerDisplayName = Some message.DisplayName
                                          CreatedAtUtc = clock.UtcNow }
                                    let! created = TelegramCommandRepository.createSayWithPaymentInTransaction connection transaction message options.SayPriceStars order token
                                    match created |> mapRepository with
                                    | Error error -> return Error error
                                    | Ok(CommandOutcome.Created createdOrder) ->
                                        let! appended = appendDerived connection transaction envelope DonationInvoiceCreated (invoicePayloadJson createdOrder interaction.ChatId currentLocale) token
                                        return appended |> Result.map (fun () -> CommandOutcome.Created createdOrder)
                                    | Ok outcome -> return Ok outcome
                            })
                        cancellationToken

                match outcome with
                | Error error -> return Error error
                | Ok(CommandOutcome.Rejected _) -> return Error(StateTransitionRejected("create say payment", message.Id))
                | Ok _ -> return Ok ()
        }

    let handleSong (envelope: DomainEventEnvelope) (interaction: TelegramInteraction) cancellationToken =
        task {
            let currentLocale = locale interaction.LanguageCode
            let query = interaction.Argument |> Option.defaultValue String.Empty |> fun value -> value.Trim()

            if not (String.IsNullOrWhiteSpace query) && not interaction.IsPrivateChat then
                return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "song-private" interaction.ChatId (TelegramText.privateChatOnly currentLocale) None cancellationToken
            elif String.IsNullOrWhiteSpace query then
                let! currentSong = PlayerStateReadModel.loadCurrentSong dataSource clock cancellationToken
                match currentSong |> mapRepository with
                | Error error -> return Error error
                | Ok song when String.IsNullOrWhiteSpace song.FallbackText ->
                    return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "song-current" interaction.ChatId (TelegramText.nothingPlaying currentLocale) None cancellationToken
                | Ok song ->
                    let text =
                        if String.IsNullOrWhiteSpace song.ExternalUrl then song.FallbackText else song.ExternalUrl
                    return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "song-current" interaction.ChatId text None cancellationToken
            else
                let! matches = TrackRepository.searchActive dataSource query 5 cancellationToken
                match matches |> mapRepository with
                | Error error -> return Error error
                | Ok matches ->
                    match TelegramSearch.classify query matches with
                    | Confident track ->
                        return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "song-match" interaction.ChatId (TelegramText.trackLinkOrDisplay track.ExternalUrl track.Artist track.Title) None cancellationToken
                    | Suggestions tracks ->
                        return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "song-selection" interaction.ChatId (TelegramText.songSelectionPrompt currentLocale) (songKeyboard tracks) cancellationToken
                    | NoMatch ->
                        return! sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "song-not-found" interaction.ChatId (TelegramText.trackNotFound currentLocale) None cancellationToken
        }

    let handleCommand (envelope: DomainEventEnvelope) (interaction: TelegramInteraction) cancellationToken =
        let currentLocale = locale interaction.LanguageCode
        match interaction.Command.ToLowerInvariant() with
        | "/start" -> sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "start" interaction.ChatId (TelegramText.start currentLocale options.RequestPriceStars options.SayPriceStars) None cancellationToken
        | "/help" -> sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "help" interaction.ChatId (TelegramText.help currentLocale options.RequestPriceStars options.SayPriceStars) None cancellationToken
        | "/terms" -> sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "terms" interaction.ChatId (TelegramText.terms currentLocale options.RequestPriceStars options.SayPriceStars) None cancellationToken
        | "/paysupport" -> sendText envelope interaction.TelegramUpdateId interaction.TelegramUserId "paysupport" interaction.ChatId (TelegramText.paysupport currentLocale) None cancellationToken
        | "/song" -> handleSong envelope interaction cancellationToken
        | _ -> Task.FromResult(Error(InvalidPayload("TelegramCommandReceived", "command is not supported.")))

    let callbackSelection (envelope: DomainEventEnvelope) (callback: TelegramCallbackInteraction) (requestId: Guid) (trackId: Guid) cancellationToken =
        task {
            let currentLocale = locale callback.LanguageCode
            if not callback.IsPrivateChat then
                return Ok(TelegramText.invalidOrExpiredAction currentLocale, None)
            else
                let! result =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction token ->
                            task {
                                let! track = TrackRepository.tryGetActiveByIdInTransaction connection transaction trackId token
                                match track |> mapRepository with
                                | Error error -> return Error error
                                | Ok None -> return Ok(CommandOutcome.Rejected "track is unavailable")
                                | Ok(Some _) ->
                                    let! selected = TelegramCommandRepository.selectTrackInTransaction connection transaction requestId callback.TelegramUserId trackId clock.UtcNow token
                                    match selected |> mapRepository with
                                    | Error error -> return Error error
                                    | Ok(CommandOutcome.Created _) ->
                                        let! appended = appendDerived connection transaction envelope TrackRequestMatched (matchedPayload requestId trackId) token
                                        return appended |> Result.map (fun () -> CommandOutcome.Created ())
                                    | Ok(CommandOutcome.AlreadyApplied _) -> return Ok(CommandOutcome.AlreadyApplied ())
                                    | Ok(CommandOutcome.Rejected reason) -> return Ok(CommandOutcome.Rejected reason)
                            })
                        cancellationToken

                match result with
                | Error error -> return Error error
                | Ok(CommandOutcome.Created _)
                | Ok(CommandOutcome.AlreadyApplied _) ->
                    let followUp =
                        callback.ChatId
                        |> Option.map (fun chatId -> TelegramText.requestConfirmationPrompt currentLocale, confirmationKeyboard requestId currentLocale, chatId)
                    return Ok(String.Empty, followUp)
                | Ok(CommandOutcome.Rejected _) -> return Ok(TelegramText.invalidOrExpiredAction currentLocale, None)
        }

    let callbackConfirm (envelope: DomainEventEnvelope) (callback: TelegramCallbackInteraction) (requestId: Guid) cancellationToken =
        task {
            let currentLocale = locale callback.LanguageCode
            match callback.ChatId, callback.IsPrivateChat with
            | Some chatId, true ->
                let! result =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction token ->
                            task {
                                let! existing = TelegramCommandRepository.tryGetActiveOrderForPurposeInTransaction connection transaction Request requestId token
                                match existing |> mapRepository with
                                | Error error -> return Error error
                                | Ok(Some order) when order.TelegramUserId = callback.TelegramUserId -> return Ok(CommandOutcome.AlreadyApplied order)
                                | Ok(Some _) -> return Ok(CommandOutcome.Rejected "payment does not belong to callback user")
                                | Ok None ->
                                    let paymentId = idGenerator.NewId()
                                    let order =
                                        { Id = paymentId
                                          TelegramUserId = callback.TelegramUserId
                                          Purpose = Request
                                          PurposeEntityId = Some requestId
                                          AmountStars = options.RequestPriceStars
                                          InvoicePayload = paymentId.ToString("D")
                                          PayerDisplayName = callback.DisplayName
                                          CreatedAtUtc = clock.UtcNow }
                                    let! created = TelegramCommandRepository.createRequestPaymentInTransaction connection transaction requestId callback.TelegramUserId order token
                                    match created |> mapRepository with
                                    | Error error -> return Error error
                                    | Ok(CommandOutcome.Created createdOrder) ->
                                        let! appended = appendDerived connection transaction envelope DonationInvoiceCreated (invoicePayloadJson createdOrder chatId currentLocale) token
                                        return appended |> Result.map (fun () -> CommandOutcome.Created createdOrder)
                                    | Ok outcome -> return Ok outcome
                            })
                        cancellationToken

                match result with
                | Error error -> return Error error
                | Ok(CommandOutcome.Created _) -> return Ok String.Empty
                | Ok(CommandOutcome.AlreadyApplied _) -> return Ok(TelegramText.invoiceAlreadyCreated currentLocale)
                | Ok(CommandOutcome.Rejected _) -> return Ok(TelegramText.invalidOrExpiredAction currentLocale)
            | _ -> return Ok(TelegramText.invalidOrExpiredAction currentLocale)
        }

    let callbackCancel (callback: TelegramCallbackInteraction) (requestId: Guid) cancellationToken =
        task {
            let currentLocale = locale callback.LanguageCode
            if not callback.IsPrivateChat then
                return Ok(TelegramText.invalidOrExpiredAction currentLocale)
            else
                let! result =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction token ->
                            task {
                                let! outcome = TelegramCommandRepository.cancelTrackRequestInTransaction connection transaction requestId callback.TelegramUserId clock.UtcNow token
                                return outcome |> mapRepository
                            })
                        cancellationToken
                match result with
                | Error error -> return Error error
                | Ok(CommandOutcome.Created _)
                | Ok(CommandOutcome.AlreadyApplied _) -> return Ok String.Empty
                | Ok(CommandOutcome.Rejected _) -> return Ok(TelegramText.invalidOrExpiredAction currentLocale)
        }

    let callbackSong (callback: TelegramCallbackInteraction) (trackId: Guid) cancellationToken =
        task {
            let currentLocale = locale callback.LanguageCode
            if not callback.IsPrivateChat then
                return Ok(TelegramText.invalidOrExpiredAction currentLocale, None)
            else
                let! track = TrackRepository.tryGetActiveById dataSource trackId cancellationToken
                match track |> mapRepository with
                | Error error -> return Error error
                | Ok(Some track) ->
                    let followUp = callback.ChatId |> Option.map (fun chatId -> TelegramText.trackLinkOrDisplay track.ExternalUrl track.Artist track.Title, None, chatId)
                    return Ok(String.Empty, followUp)
                | Ok None -> return Ok(TelegramText.invalidOrExpiredAction currentLocale, None)
        }

    let handleCallback (envelope: DomainEventEnvelope) (callback: TelegramCallbackInteraction) cancellationToken =
        task {
            let currentLocale = locale callback.LanguageCode
            let! disposition =
                match TelegramCallback.tryDecode callback.RawCallbackData with
                | Error _ -> task { return Ok(TelegramText.invalidOrExpiredAction currentLocale, None) }
                | Ok(TelegramCallback.RequestSelect(requestId, trackId)) -> callbackSelection envelope callback requestId trackId cancellationToken
                | Ok(TelegramCallback.RequestConfirm requestId) ->
                    task {
                        let! answer = callbackConfirm envelope callback requestId cancellationToken
                        return answer |> Result.map (fun text -> text, None)
                    }
                | Ok(TelegramCallback.RequestCancel requestId) ->
                    task {
                        let! answer = callbackCancel callback requestId cancellationToken
                        return answer |> Result.map (fun text -> text, None)
                    }
                | Ok(TelegramCallback.SongSelect trackId) -> callbackSong callback trackId cancellationToken

            match disposition with
            | Error error -> return Error error
            | Ok(answerText, followUp) ->
                let answer = if String.IsNullOrWhiteSpace answerText then None else Some answerText
                let! answered = answerCallback envelope callback "callback" answer cancellationToken
                match answered with
                | Error error -> return Error error
                | Ok () ->
                    match followUp with
                    | None -> return Ok ()
                    | Some(text, keyboard, chatId) ->
                        return! sendText envelope callback.TelegramUpdateId callback.TelegramUserId "callback-follow-up" chatId text keyboard cancellationToken
        }

    member _.HandleInteractionAsync(envelope: DomainEventEnvelope, cancellationToken: CancellationToken) =
        task {
            let eventType = DomainEventType.toString envelope.EventType
            match envelope.EventType with
            | TrackRequested ->
                match TelegramWorkflowJson.interaction eventType envelope.PayloadJson with
                | Error error -> return Error error
                | Ok interaction ->
                    match TelegramWorkflowJson.parseObject eventType envelope.PayloadJson |> Result.bind (TelegramWorkflowJson.requiredString eventType "query") with
                    | Error error -> return Error error
                    | Ok query ->
                        logger.LogInformation("Telegram interaction event {eventId}, correlation {correlationId}, update {telegramUpdateId}, user {telegramUserId}, command {command}.", envelope.EventId, envelope.CorrelationId, interaction.TelegramUpdateId, interaction.TelegramUserId, "request")
                        return! handleTrackRequested envelope interaction query cancellationToken
            | SayMessageSubmitted ->
                match TelegramWorkflowJson.interaction eventType envelope.PayloadJson with
                | Error error -> return Error error
                | Ok interaction ->
                    match TelegramWorkflowJson.parseObject eventType envelope.PayloadJson |> Result.bind (TelegramWorkflowJson.requiredString eventType "text") with
                    | Error error -> return Error error
                    | Ok text ->
                        logger.LogInformation("Telegram interaction event {eventId}, correlation {correlationId}, update {telegramUpdateId}, user {telegramUserId}, command {command}.", envelope.EventId, envelope.CorrelationId, interaction.TelegramUpdateId, interaction.TelegramUserId, "say")
                        return! handleSay envelope interaction text cancellationToken
            | TelegramCommandReceived ->
                match TelegramWorkflowJson.interaction eventType envelope.PayloadJson with
                | Error error -> return Error error
                | Ok interaction ->
                    logger.LogInformation("Telegram interaction event {eventId}, correlation {correlationId}, update {telegramUpdateId}, user {telegramUserId}, command {command}.", envelope.EventId, envelope.CorrelationId, interaction.TelegramUpdateId, interaction.TelegramUserId, interaction.Command)
                    return! handleCommand envelope interaction cancellationToken
            | TelegramCallbackReceived ->
                match TelegramWorkflowJson.callback eventType envelope.PayloadJson with
                | Error error -> return Error error
                | Ok callback ->
                    logger.LogInformation("Telegram callback event {eventId}, correlation {correlationId}, update {telegramUpdateId}, user {telegramUserId}, action {action}.", envelope.EventId, envelope.CorrelationId, callback.TelegramUpdateId, callback.TelegramUserId, "callback")
                    return! handleCallback envelope callback cancellationToken
            | _ -> return Error(InvalidPayload(eventType, "event is not handled by TelegramBotWorkflow."))
        }

    member _.SendInvoiceAsync(envelope: DomainEventEnvelope, cancellationToken: CancellationToken) =
        task {
            let eventType = DomainEventType.toString envelope.EventType
            if envelope.EventType <> DonationInvoiceCreated then
                return Error(InvalidPayload(eventType, "event is not a DonationInvoiceCreated event."))
            else
                match TelegramWorkflowJson.invoice eventType envelope.PayloadJson with
                | Error error -> return Error error
                | Ok invoice ->
                    let expectedTitle, expectedDescription, expectedPriceLabel =
                        match invoice.Purpose with
                        | Request -> TelegramText.requestInvoiceTitle TelegramLocale.English, TelegramText.requestInvoiceDescription TelegramLocale.English, TelegramText.requestInvoicePriceLabel TelegramLocale.English
                        | Say -> TelegramText.sayInvoiceTitle TelegramLocale.English, TelegramText.sayInvoiceDescription TelegramLocale.English, TelegramText.sayInvoicePriceLabel TelegramLocale.English
                        | Donation -> String.Empty, String.Empty, String.Empty
                    let validFixedCopy =
                        let russian =
                            match invoice.Purpose with
                            | Request -> invoice.Title = TelegramText.requestInvoiceTitle TelegramLocale.Russian && invoice.Description = TelegramText.requestInvoiceDescription TelegramLocale.Russian && invoice.PriceLabel = TelegramText.requestInvoicePriceLabel TelegramLocale.Russian
                            | Say -> invoice.Title = TelegramText.sayInvoiceTitle TelegramLocale.Russian && invoice.Description = TelegramText.sayInvoiceDescription TelegramLocale.Russian && invoice.PriceLabel = TelegramText.sayInvoicePriceLabel TelegramLocale.Russian
                            | Donation -> false
                        russian || (invoice.Title = expectedTitle && invoice.Description = expectedDescription && invoice.PriceLabel = expectedPriceLabel)
                    if invoice.PurposeEntityId = Guid.Empty || invoice.PaymentId.ToString("D") = String.Empty || not validFixedCopy then
                        return Error(InvalidPayload(eventType, "invoice payload has invalid fixed fields."))
                    else
                        logger.LogInformation("Telegram invoice event {eventId}, correlation {correlationId}, payment {paymentId}.", envelope.EventId, envelope.CorrelationId, invoice.PaymentId)
                        let telegramInvoice =
                            { ChatId = invoice.ChatId
                              Title = invoice.Title
                              Description = invoice.Description
                              Payload = invoice.PaymentId.ToString("D")
                              Currency = invoice.Currency
                              ProviderToken = invoice.ProviderToken
                              PriceLabel = invoice.PriceLabel
                              AmountStars = invoice.AmountStars }
                        return! terminalTransport envelope 0L 0L "invoice" (Some invoice.PaymentId) (telegramClient.SendInvoiceAsync(telegramInvoice, cancellationToken))
        }

    member _.HandleAsync(input: TelegramPreCheckoutInput, cancellationToken: CancellationToken) =
        task {
            use deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            deadline.CancelAfter(TimeSpan.FromSeconds 8.0)

            try
                let currentLocale = locale input.LanguageCode
                let receivedAtUtc = clock.UtcNow

                logger.LogInformation(
                    "Telegram pre-checkout received for update {telegramUpdateId}, user {telegramUserId}.",
                    input.TelegramUpdateId,
                    input.TelegramUserId
                )

                let inboxPayload =
                    JsonSerializer.Serialize(
                        {| queryId = input.QueryId
                           telegramUserId = input.TelegramUserId
                           languageCode = input.LanguageCode
                           currency = input.Currency
                           amountStars = input.TotalAmount
                           invoicePayload = input.InvoicePayload |},
                        ApiJson.options
                    )

                let! validation =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction token ->
                            task {
                                let! decision =
                                    PaymentRepository.validatePreCheckoutInTransaction
                                        connection
                                        transaction
                                        input.TelegramUserId
                                        input.Currency
                                        input.TotalAmount
                                        input.InvoicePayload
                                        token

                                match decision |> mapRepository with
                                | Error error -> return Error error
                                | Ok decision ->
                                    let record =
                                        { Id = idGenerator.NewId()
                                          TelegramUpdateId = input.TelegramUpdateId
                                          EventType = "TelegramPreCheckoutQuery"
                                          ReceivedAtUtc = receivedAtUtc
                                          CorrelationId = None
                                          PayloadJson = inboxPayload }

                                    let! recorded = TelegramUpdateInboxRepository.tryRecordInTransaction connection transaction record token

                                    match recorded |> mapRepository with
                                    | Error error -> return Error error
                                    | Ok _ -> return Ok decision
                            })
                        deadline.Token

                match validation with
                | Error error -> return Error error
                | Ok decision ->
                    let answer, rejectionReason =
                        match decision with
                        | PreCheckoutDecision.Approved -> None, None
                        | PreCheckoutDecision.Rejected reason ->
                            Some(TelegramText.paymentDoesNotMatchExpectedOrder currentLocale), Some reason

                    match rejectionReason with
                    | Some reason ->
                        logger.LogWarning(
                            "Telegram pre-checkout rejected for update {telegramUpdateId}, user {telegramUserId}, reason {reason}.",
                            input.TelegramUpdateId,
                            input.TelegramUserId,
                            reason
                        )
                    | None -> ()

                    let! answered = telegramClient.AnswerPreCheckoutAsync(input.QueryId, answer, deadline.Token)

                    match answered with
                    | Ok () -> return Ok ()
                    | Error transportError ->
                        logger.LogWarning(
                            "Telegram pre-checkout answer failed for update {telegramUpdateId}, user {telegramUserId}, method {methodName}, code {errorCode}.",
                            input.TelegramUpdateId,
                            input.TelegramUserId,
                            transportError.Method,
                            transportError.ErrorCode
                        )

                        return Error(preCheckoutUnavailable transportError.Method transportError.Description)
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | :? OperationCanceledException ->
                logger.LogWarning(
                    "Telegram pre-checkout timed out for update {telegramUpdateId}, user {telegramUserId}.",
                    input.TelegramUpdateId,
                    input.TelegramUserId
                )

                return Error(preCheckoutUnavailable "answerPreCheckoutQuery" "Telegram pre-checkout processing timed out.")
        }
 

    interface ITelegramPreCheckoutWorkflow with
        member this.HandleAsync input cancellationToken = this.HandleAsync(input, cancellationToken)

    interface ITelegramBotWorkflow with
        member this.HandleInteractionAsync envelope cancellationToken = this.HandleInteractionAsync(envelope, cancellationToken)
        member this.SendInvoiceAsync envelope cancellationToken = this.SendInvoiceAsync(envelope, cancellationToken)
