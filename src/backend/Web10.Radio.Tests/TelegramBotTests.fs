namespace Web10.Radio.Tests

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Database.Repositories
open Web10.Radio.Telegram

module TelegramBotTests =
    type private FixedClock(nowUtc: DateTimeOffset) =
        interface IClock with
            member _.UtcNow = nowUtc

    type private SequentialIdGenerator() =
        let mutable next = 0

        interface IIdGenerator with
            member _.NewId() =
                next <- next + 1
                Guid(next, 0s, 0s, [| 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy |])

    type private TestAdapterState() =
        let errors = ResizeArray<string>()

        member _.Errors = List.ofSeq errors

        interface ITelegramAdapterState with
            member _.Snapshot() =
                { IsConfigured = true
                  ChannelIdOrUsername = "@web10_test"
                  LastUpdateId = None
                  LastError = errors |> Seq.tryLast }

            member _.RecordUpdate _ = ()
            member _.RecordError message = errors.Add message

    type private SentText =
        { ChatId: int64
          Text: string
          Keyboard: TelegramInlineButton list list option }

    type private CallbackAnswer =
        { QueryId: string
          Text: string option }

    type private PreCheckoutAnswer =
        { QueryId: string
          ErrorMessage: string option }

    type private CapturingTelegramClient() =
        let texts = ResizeArray<SentText>()
        let invoices = ResizeArray<TelegramStarsInvoice>()
        let callbacks = ResizeArray<CallbackAnswer>()
        let preCheckouts = ResizeArray<PreCheckoutAnswer>()
        let mutable textResult = Ok ()
        let mutable invoiceResult = Ok ()
        let mutable callbackResult = Ok ()
        let mutable preCheckoutResult = Ok ()

        member _.Texts = List.ofSeq texts
        member _.Invoices = List.ofSeq invoices
        member _.Callbacks = List.ofSeq callbacks
        member _.PreCheckouts = List.ofSeq preCheckouts
        member _.TextResult with get () = textResult and set value = textResult <- value
        member _.InvoiceResult with get () = invoiceResult and set value = invoiceResult <- value
        member _.CallbackResult with get () = callbackResult and set value = callbackResult <- value
        member _.PreCheckoutResult with get () = preCheckoutResult and set value = preCheckoutResult <- value

        interface ITelegramBotClient with
            member _.SendTextAsync(chatId, text, keyboard, _) =
                texts.Add { ChatId = chatId; Text = text; Keyboard = keyboard }
                Task.FromResult textResult

            member _.SendInvoiceAsync(invoice, _) =
                invoices.Add invoice
                Task.FromResult invoiceResult

            member _.AnswerCallbackAsync(queryId, text, _) =
                callbacks.Add { QueryId = queryId; Text = text }
                Task.FromResult callbackResult

            member _.AnswerPreCheckoutAsync(queryId, errorMessage, _) =
                preCheckouts.Add { QueryId = queryId; ErrorMessage = errorMessage }
                Task.FromResult preCheckoutResult

            member _.GetUpdatesAsync(_offset, _timeoutSeconds, _) = Task.FromResult(Ok [||])

            member _.DeleteWebhookAsync(_dropPendingUpdates, _) = Task.FromResult(Ok())

    type private CapturingLogger(entries: ConcurrentQueue<string>) =
        interface ILogger with
            member _.BeginScope<'TState>(_state: 'TState) : IDisposable = null
            member _.IsEnabled _ = true

            member _.Log<'TState>(_: LogLevel, _: EventId, state: 'TState, error: exn, formatter: Func<'TState, exn, string>) =
                entries.Enqueue(formatter.Invoke(state, error))

    type private CapturingLoggerProvider(entries: ConcurrentQueue<string>) =
        interface ILoggerProvider with
            member _.CreateLogger _ = CapturingLogger(entries) :> ILogger
            member _.Dispose() = ()

    let private nowUtc = DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero)

    let private options requestPrice sayPrice =
        { BotToken = "test-bot-token-that-must-not-reach-logs"
          WebhookSecret = "test-webhook-secret"
          ChannelIdOrUsername = "@web10_test"
          RequestPriceStars = requestPrice
          SayPriceStars = sayPrice
          UpdateMode = TelegramUpdateMode.Webhook }

    let private createWorkflow (connectionString: string) requestPrice sayPrice client adapter logger =
        let dataSource = NpgsqlDataSource.Create(connectionString)
        let workflow =
            TelegramBotWorkflow(
                dataSource,
                SequentialIdGenerator() :> IIdGenerator,
                FixedClock(nowUtc) :> IClock,
                options requestPrice sayPrice,
                client :> ITelegramBotClient,
                adapter :> ITelegramAdapterState,
                logger
            )

        dataSource, workflow

    let private envelope eventId eventType payloadJson =
        { EventId = eventId
          EventType = eventType
          OccurredAtUtc = nowUtc
          Producer = "web10.radio.tests"
          CorrelationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
          CausationId = None
          PayloadJson = payloadJson }

    let private interactionPayloadWithDisplayName updateId chatId userId displayName languageCode command argument query text =
        JsonSerializer.Serialize(
            {| telegramUpdateId = updateId
               chatId = chatId
               telegramUserId = userId
               displayName = displayName |> Option.toObj
               languageCode = languageCode |> Option.toObj
               command = command
               argument = argument |> Option.toObj
               isPrivateChat = true
               query = query
               text = text |}
        )

    let private interactionPayload updateId chatId userId languageCode command argument query text =
        interactionPayloadWithDisplayName updateId chatId userId (Some "Test user") languageCode command argument query text

    let private callbackPayload updateId chatId userId languageCode queryId callbackData =
        JsonSerializer.Serialize(
            {| telegramUpdateId = updateId
               chatId = match chatId with | Some value -> Nullable value | None -> Nullable()
               telegramUserId = userId
               languageCode = languageCode |> Option.toObj
               callbackQueryId = queryId
               rawCallbackData = callbackData
               isPrivateChat = true |}
        )

    let private callbackPayloadWithDisplayName updateId chatId userId displayName languageCode queryId callbackData =
        JsonSerializer.Serialize(
            {| telegramUpdateId = updateId
               chatId = match chatId with | Some value -> Nullable value | None -> Nullable()
               telegramUserId = userId
               displayName = displayName |> Option.toObj
               languageCode = languageCode |> Option.toObj
               callbackQueryId = queryId
               rawCallbackData = callbackData
               isPrivateChat = true |}
        )

    let private assertOk description result =
        match result with
        | Ok () -> ()
        | Error error -> Assert.Fail(sprintf "Expected %s to succeed, but got %A." description error)

    let private execute connectionString sql (parameters: (string * obj) list) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand(sql, connection)
            parameters |> List.iter (fun (name, value) -> command.Parameters.AddWithValue(name, value) |> ignore)
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    let private scalarInt connectionString sql (parameters: (string * obj) list) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand(sql, connection)
            parameters |> List.iter (fun (name, value) -> command.Parameters.AddWithValue(name, value) |> ignore)
            let! value = command.ExecuteScalarAsync()
            return Convert.ToInt32 value
        }

    let private scalarString connectionString sql (parameters: (string * obj) list) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand(sql, connection)
            parameters |> List.iter (fun (name, value) -> command.Parameters.AddWithValue(name, value) |> ignore)
            let! value = command.ExecuteScalarAsync()
            return Convert.ToString value
        }

    let private invoiceEnvelope connectionString =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            use command =
                new NpgsqlCommand(
                    """SELECT "Id", "Payload"::text
FROM "OutboxEvents"
WHERE "EventType" = 'DonationInvoiceCreated' AND "IsDeleted" = false
ORDER BY "OccurredAtUtc", "Id"
LIMIT 1;""",
                    connection
                )

            use! reader = command.ExecuteReaderAsync()
            let! found = reader.ReadAsync()
            Assert.That(found, Is.True, "The interaction must append a durable invoice event before any invoice is sent.")
            return envelope (reader.GetGuid 0) DonationInvoiceCreated (reader.GetString 1)
        }

    let private seedTrack connectionString trackId title artist isDeleted =
        execute
            connectionString
            """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @Title, @Artist, @IsDeleted, @At, @At);"""
            [ "Id", box trackId
              "Title", box title
              "Artist", box artist
              "IsDeleted", box isDeleted
              "At", box nowUtc ]

    let private seedRequest connectionString requestId userId trackId status =
        execute
            connectionString
            """INSERT INTO "TrackRequests" ("Id", "TelegramUserId", "DisplayName", "Query", "MatchedTrackId", "Status", "RequestedAtUtc", "CorrelationId", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @UserId, 'Test user', 'test', @TrackId, @Status, @At, @CorrelationId, false, @At, @At);"""
            [ "Id", box requestId
              "UserId", box userId
              "TrackId", trackId |> Option.map box |> Option.defaultValue DBNull.Value
              "Status", box status
              "At", box nowUtc
              "CorrelationId", box (Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")) ]

    [<Test>]
    let ``localized informational commands use configured prices and English fallback`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let client = CapturingTelegramClient()
                let adapter = TestAdapterState()
                let dataSource, workflow = createWorkflow connectionString 137 73 client adapter NullLogger<TelegramBotWorkflow>.Instance
                use dataSource = dataSource
                let ruStart = "Добро пожаловать в Web10.Radio.\n\n/request <название> — заказать трек (137 ⭐)\n/say <текст> — сообщение на экран (73 ⭐)\n/song [название] — текущий трек или ссылка\n/terms — условия оплаты\n/paysupport — поддержка по платежам\n/help — помощь"
                let ruHelp = "/request <название> — заказать трек (137 ⭐)\n/say <текст> — сообщение на экран (73 ⭐)\n/song [название] — текущий трек или ссылка\n/terms — условия оплаты\n/paysupport — поддержка по платежам\n/help — помощь"
                let ruTerms = "Цифровые услуги оплачиваются Telegram Stars. Заказ трека стоит 137 ⭐, сообщение на экран — 73 ⭐. Услуга активируется только после сообщения Telegram об успешной оплате. Возвраты и споры: /paysupport."
                let ruSupport = "По вопросам оплаты и возврата напишите @netscapedidnothingwrong. Укажите дату, сумму в Stars и описание операции; не отправляйте платёжные данные или коды доступа."
                let enStart = "Welcome to Web10.Radio.\n\n/request <title> — request a track (137 ⭐)\n/say <text> — put a message on screen (73 ⭐)\n/song [title] — current track or link\n/terms — payment terms\n/paysupport — payment support\n/help — help"
                let enHelp = "/request <title> — request a track (137 ⭐)\n/say <text> — put a message on screen (73 ⭐)\n/song [title] — current track or link\n/terms — payment terms\n/paysupport — payment support\n/help — help"
                let enTerms = "Digital services are paid with Telegram Stars. A track request costs 137 ⭐ and an on-screen message costs 73 ⭐. The service is activated only after Telegram reports a successful payment. Refunds and disputes: /paysupport."
                let enSupport = "For payment or refund support, contact @netscapedidnothingwrong. Include the date, Stars amount, and operation description; do not send payment credentials or access codes."
                let commands =
                    [ "/start", Some "ru-RU"; "/help", Some "ru-RU"; "/terms", Some "ru-RU"; "/paysupport", Some "ru-RU"
                      "/start", Some "en-US"; "/help", Some "en-US"; "/terms", Some "en-US"; "/paysupport", Some "en-US"
                      "/start", None; "/help", None; "/terms", None; "/paysupport", None ]

                for index, (command, languageCode) in commands |> List.indexed do
                    let eventId = Guid(index + 1, 0s, 0s, [| 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy |])
                    let payload = interactionPayload (int64 (index + 1)) 41L 41L languageCode command None "" ""
                    let! result = workflow.HandleInteractionAsync(envelope eventId TelegramCommandReceived payload, CancellationToken.None)
                    assertOk command result

                let sent = client.Texts
                Assert.That(
                    sent |> List.map _.Text,
                    Is.EqualTo(box [ ruStart; ruHelp; ruTerms; ruSupport; enStart; enHelp; enTerms; enSupport; enStart; enHelp; enTerms; enSupport ]))
            })

    [<Test>]
    let ``callback codec roundtrips every exact form within Telegram limit and rejects malformed input`` () =
        let requestId = Guid.Parse("11111111-2222-3333-4444-555555555555")
        let trackId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
        let cases =
            [ TelegramCallback.RequestSelect(requestId, trackId), "rq:s:ERERESIiMzNERFVVVVVVVQ:qqqqqru7zMzd3e7u7u7u7g"
              TelegramCallback.RequestConfirm requestId, "rq:c:ERERESIiMzNERFVVVVVVVQ"
              TelegramCallback.RequestCancel requestId, "rq:x:ERERESIiMzNERFVVVVVVVQ"
              TelegramCallback.SongSelect trackId, "sg:s:qqqqqru7zMzd3e7u7u7u7g" ]

        for callback, expected in cases do
            match TelegramCallback.encode callback with
            | Error error -> Assert.Fail(sprintf "Expected %A to encode, but got %A." callback error)
            | Ok encoded ->
                Assert.That(encoded, Is.EqualTo expected)
                Assert.That(Encoding.UTF8.GetByteCount encoded, Is.LessThanOrEqualTo 64)
                match TelegramCallback.tryDecode encoded with
                | Ok decoded -> Assert.That(decoded, Is.EqualTo callback)
                | Error error -> Assert.Fail(sprintf "Encoded callback %s did not decode: %A." encoded error)

        let malformed =
            [ ""
              "rq:s:not-a-guid:not-a-guid"
              "rq:c:AAAAAAAAAAAAAAAAAAAAAA:extra"
              "rq:x:AAAAAAAAAAAAAAAAAAAAA"
              "sg:s:AAAAAAAAAAAAAAAAAAAAAA:extra"
              "rq:q:AAAAAAAAAAAAAAAAAAAAAA"
              String.replicate 65 "x" ]

        malformed
        |> List.iter (fun value ->
            Assert.DoesNotThrow(fun () -> TelegramCallback.tryDecode value |> ignore)
            match TelegramCallback.tryDecode value with
            | Error TelegramCallbackCodecError.InvalidAction -> ()
            | actual -> Assert.Fail(sprintf "Malformed callback %s must be rejected, but got %A." value actual))

    [<Test>]
    let ``trigram search excludes deleted records orders ties and keeps a low confidence request unpaid`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let massiveId = Guid.Parse("10000000-0000-0000-0000-000000000001")
                let deletedId = Guid.Parse("10000000-0000-0000-0000-000000000002")
                let tieA = Guid.Parse("10000000-0000-0000-0000-000000000003")
                let tieB = Guid.Parse("10000000-0000-0000-0000-000000000004")
                let lowId = Guid.Parse("10000000-0000-0000-0000-000000000005")
                do! seedTrack connectionString massiveId "Teardrop" "Massive Attack" false
                do! seedTrack connectionString deletedId "Teardrop" "Masive Atack" true
                do! seedTrack connectionString tieB "Tie" "B artist" false
                do! seedTrack connectionString tieA "Tie" "A artist" false
                do! seedTrack connectionString lowId "abcdefghij" "isolated" false
                do! (execute
                    connectionString
                    """INSERT INTO "TrackLinks" ("Id", "TrackId", "Kind", "Url", "IsPrimary", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@DeletedLink, @TrackId, 'external', 'https://deleted.example', true, true, @At, @At),
       (@LiveLink, @TrackId, 'external', 'https://live.example', false, false, @At, @At);"""
                    [ "DeletedLink", box (Guid.Parse("20000000-0000-0000-0000-000000000001"))
                      "LiveLink", box (Guid.Parse("20000000-0000-0000-0000-000000000002"))
                      "TrackId", box massiveId
                      "At", box nowUtc ])

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! typo = TrackRepository.searchActive dataSource "masive atack teardrop" 5 CancellationToken.None
                match typo with
                | Error error -> Assert.Fail(sprintf "Search failed: %A" error)
                | Ok results ->
                    Assert.That(results.Head.Id, Is.EqualTo massiveId)
                    Assert.That(results.Head.ExternalUrl, Is.EqualTo(Some "https://live.example"))
                    Assert.That(results |> List.exists (fun item -> item.Id = deletedId), Is.False)

                let! ties = TrackRepository.searchActive dataSource "tie" 5 CancellationToken.None
                match ties with
                | Error error -> Assert.Fail(sprintf "Tie search failed: %A" error)
                | Ok results -> Assert.That(results |> List.map _.Id, Is.EqualTo(box [ tieA; tieB ]))

                let client = CapturingTelegramClient()
                let adapter = TestAdapterState()
                let workflowDataSource, workflow = createWorkflow connectionString 100 50 client adapter NullLogger<TelegramBotWorkflow>.Instance
                use workflowDataSource = workflowDataSource
                let requestId = Guid.Parse("30000000-0000-0000-0000-000000000001")
                let payload = interactionPayload 30L 50L 50L (Some "en") "/request" None "abcxefghij" ""
                let! result = workflow.HandleInteractionAsync(envelope requestId TrackRequested payload, CancellationToken.None)
                assertOk "low confidence request" result
                let! requestStatus = scalarString connectionString "SELECT \"Status\" FROM \"TrackRequests\" WHERE \"Id\" = @Id;" [ "Id", box requestId ]
                let! paymentCount = scalarInt connectionString "SELECT count(*) FROM \"Payments\" WHERE \"PurposeEntityId\" = @Id;" [ "Id", box requestId ]
                Assert.That(requestStatus, Is.EqualTo "NeedsReview")
                Assert.That(paymentCount, Is.EqualTo 0)
                Assert.That(client.Texts |> List.last |> _.Text, Is.EqualTo "No match was found. The request was saved without payment; automatic processing is unavailable.")
            })

    [<Test>]
    let ``callbacks answer exactly once and protect selection ownership state and stale actions`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let owner = 77L
                let firstTrack = Guid.Parse("40000000-0000-0000-0000-000000000001")
                let secondTrack = Guid.Parse("40000000-0000-0000-0000-000000000002")
                let requestId = Guid.Parse("40000000-0000-0000-0000-000000000003")
                let cancelId = Guid.Parse("40000000-0000-0000-0000-000000000004")
                do! seedTrack connectionString firstTrack "First" "Artist" false
                do! seedTrack connectionString secondTrack "Second" "Artist" false
                do! seedRequest connectionString requestId owner None "NeedsReview"
                do! seedRequest connectionString cancelId owner None "NeedsReview"

                let client = CapturingTelegramClient()
                let adapter = TestAdapterState()
                let dataSource, workflow = createWorkflow connectionString 100 50 client adapter NullLogger<TelegramBotWorkflow>.Instance
                use dataSource = dataSource
                let dispatch queryId userId raw =
                    task {
                        let before = client.Callbacks.Length
                        let payload = callbackPayload (int64 (before + 1)) (Some owner) userId (Some "en") queryId raw
                        let! result = workflow.HandleInteractionAsync(envelope (Guid.NewGuid()) TelegramCallbackReceived payload, CancellationToken.None)
                        assertOk queryId result
                        Assert.That(client.Callbacks.Length, Is.EqualTo(before + 1), sprintf "%s must receive one callback acknowledgement." queryId)
                    }

                let encode callback =
                    match TelegramCallback.encode callback with
                    | Ok value -> value
                    | Error error -> Assert.Fail(sprintf "Could not encode callback: %A" error); String.Empty

                do! dispatch "select" owner (encode (TelegramCallback.RequestSelect(requestId, firstTrack)))
                do! dispatch "second-selection" owner (encode (TelegramCallback.RequestSelect(requestId, secondTrack)))
                do! dispatch "wrong-owner" 78L (encode (TelegramCallback.RequestSelect(requestId, firstTrack)))
                do! dispatch "cancel" owner (encode (TelegramCallback.RequestCancel cancelId))
                do! dispatch "song" owner (encode (TelegramCallback.SongSelect firstTrack))
                do! dispatch "malformed" owner "rq:c:not-a-guid"
                do! dispatch "stale" owner (encode (TelegramCallback.RequestCancel(Guid.Parse("40000000-0000-0000-0000-000000000099"))))

                let! matched = scalarString connectionString "SELECT \"MatchedTrackId\"::text FROM \"TrackRequests\" WHERE \"Id\" = @Id;" [ "Id", box requestId ]
                let! cancelled = scalarString connectionString "SELECT \"Status\" FROM \"TrackRequests\" WHERE \"Id\" = @Id;" [ "Id", box cancelId ]
                Assert.That(matched, Is.EqualTo(firstTrack.ToString()))
                Assert.That(cancelled, Is.EqualTo "Rejected")
                Assert.That(client.Callbacks |> List.find (fun answer -> answer.QueryId = "second-selection") |> _.Text, Is.EqualTo(Some "This action is unavailable or expired."))
                Assert.That(client.Callbacks |> List.find (fun answer -> answer.QueryId = "wrong-owner") |> _.Text, Is.EqualTo(Some "This action is unavailable or expired."))
            })

    [<Test>]
    let ``request confirmation and say create one durable invoice each and send only from invoice dispatch`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = Guid.Parse("50000000-0000-0000-0000-000000000001")
                let requestId = Guid.Parse("50000000-0000-0000-0000-000000000002")
                let sayId = Guid.Parse("50000000-0000-0000-0000-000000000003")
                let userId = 91L
                let hugeMetadata = String.replicate 512 "T"
                do! seedTrack connectionString trackId hugeMetadata "Bounded artist" false

                let client = CapturingTelegramClient()
                let adapter = TestAdapterState()
                let dataSource, workflow = createWorkflow connectionString 100 50 client adapter NullLogger<TelegramBotWorkflow>.Instance
                use dataSource = dataSource
                let requestPayload = interactionPayload 1L userId userId (Some "en") "/request" None hugeMetadata ""
                let! requested = workflow.HandleInteractionAsync(envelope requestId TrackRequested requestPayload, CancellationToken.None)
                assertOk "matched request" requested
                let confirmData =
                    match TelegramCallback.encode (TelegramCallback.RequestConfirm requestId) with
                    | Ok value -> value
                    | Error error -> Assert.Fail(sprintf "%A" error); String.Empty
                let confirmPayload = callbackPayload 2L (Some userId) userId (Some "en") "confirm-first" confirmData
                let! confirmed = workflow.HandleInteractionAsync(envelope (Guid.Parse("50000000-0000-0000-0000-000000000004")) TelegramCallbackReceived confirmPayload, CancellationToken.None)
                assertOk "first confirmation" confirmed
                let duplicatePayload = callbackPayload 3L (Some userId) userId (Some "en") "confirm-duplicate" confirmData
                let! duplicate = workflow.HandleInteractionAsync(envelope (Guid.Parse("50000000-0000-0000-0000-000000000005")) TelegramCallbackReceived duplicatePayload, CancellationToken.None)
                assertOk "duplicate confirmation" duplicate

                let sayPayload = interactionPayload 4L userId userId (Some "en") "/say" None "" "raw say text that must not appear in invoice copy"
                let! said = workflow.HandleInteractionAsync(envelope sayId SayMessageSubmitted sayPayload, CancellationToken.None)
                assertOk "say payment creation" said

                let! invoiceEvents = scalarInt connectionString "SELECT count(*) FROM \"OutboxEvents\" WHERE \"EventType\" = 'DonationInvoiceCreated' AND \"IsDeleted\" = false;" []
                let! requestQueue = scalarInt connectionString "SELECT count(*) FROM \"PlaybackQueue\" WHERE \"TrackRequestId\" = @Id AND \"IsDeleted\" = false;" [ "Id", box requestId ]
                let! requestPayment = scalarString connectionString "SELECT \"Currency\" || ':' || \"ProviderToken\" || ':' || \"AmountStars\"::text FROM \"Payments\" WHERE \"PurposeEntityId\" = @Id;" [ "Id", box requestId ]
                let! sayAmount = scalarInt connectionString "SELECT \"AmountStars\" FROM \"SayMessages\" WHERE \"Id\" = @Id;" [ "Id", box sayId ]
                let! sayStatus = scalarString connectionString "SELECT \"Status\" FROM \"SayMessages\" WHERE \"Id\" = @Id;" [ "Id", box sayId ]
                Assert.That(invoiceEvents, Is.EqualTo 2)
                Assert.That(requestQueue, Is.EqualTo 0)
                Assert.That(requestPayment, Is.EqualTo "XTR::100")
                Assert.That(sayAmount, Is.EqualTo 50)
                Assert.That(sayStatus, Is.EqualTo "PendingPayment")
                Assert.That(client.Invoices, Is.Empty, "Creating an order must not send an invoice inline.")
                Assert.That(client.Callbacks |> List.find (fun callback -> callback.QueryId = "confirm-duplicate") |> _.Text, Is.EqualTo(Some "The invoice has already been created."))

                let! requestInvoice = invoiceEnvelope connectionString
                let! dispatched = workflow.SendInvoiceAsync(requestInvoice, CancellationToken.None)
                assertOk "durable request invoice dispatch" dispatched
                let invoice = client.Invoices |> List.exactlyOne
                Assert.That(invoice.Currency, Is.EqualTo "XTR")
                Assert.That(invoice.ProviderToken, Is.EqualTo "")
                Assert.That(invoice.AmountStars, Is.EqualTo 100)
                Assert.That(invoice.Title, Is.EqualTo "Track request")
                Assert.That(invoice.Description, Does.Not.Contain hugeMetadata)
                Assert.That(invoice.Description, Does.Not.Contain "raw say text")
            })

    [<Test>]
    let ``configured say price reaches payment order and invoice while retryable and terminal transport outcomes stay distinct`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let client = CapturingTelegramClient()
                let adapter = TestAdapterState()
                let logEntries = ConcurrentQueue<string>()
                use loggerFactory = LoggerFactory.Create(fun builder -> builder.AddProvider(new CapturingLoggerProvider(logEntries)) |> ignore)
                let dataSource, workflow = createWorkflow connectionString 125 73 client adapter (loggerFactory.CreateLogger<TelegramBotWorkflow>())
                use dataSource = dataSource
                let sayId = Guid.Parse("60000000-0000-0000-0000-000000000001")
                let rawSay = "secret raw say content"
                let sayPayload = interactionPayload 10L 66L 66L (Some "ru-RU") "/say" None "" rawSay
                let! created = workflow.HandleInteractionAsync(envelope sayId SayMessageSubmitted sayPayload, CancellationToken.None)
                assertOk "non-default say creation" created
                let! amount = scalarInt connectionString "SELECT \"AmountStars\" FROM \"Payments\" WHERE \"PurposeEntityId\" = @Id;" [ "Id", box sayId ]
                let! sayAmount = scalarInt connectionString "SELECT \"AmountStars\" FROM \"SayMessages\" WHERE \"Id\" = @Id;" [ "Id", box sayId ]
                Assert.That(amount, Is.EqualTo 73)
                Assert.That(sayAmount, Is.EqualTo 73)

                let! invoiceEvent = invoiceEnvelope connectionString
                client.InvoiceResult <- Error { Method = "sendInvoice"; Description = "retryable"; ErrorCode = Some 503; IsRetryable = true }
                let! retryable = workflow.SendInvoiceAsync(invoiceEvent, CancellationToken.None)
                match retryable with
                | Error(TelegramTransportError("sendInvoice", "retryable")) -> ()
                | actual -> Assert.Fail(sprintf "Retryable transport must remain retryable, but got %A." actual)

                client.InvoiceResult <- Error { Method = "sendInvoice"; Description = "terminal"; ErrorCode = Some 400; IsRetryable = false }
                let! terminal = workflow.SendInvoiceAsync(invoiceEvent, CancellationToken.None)
                assertOk "terminal invoice failure" terminal
                Assert.That(adapter.Errors, Is.Not.Empty)

                client.InvoiceResult <- Ok ()
                let! later = workflow.SendInvoiceAsync(invoiceEvent, CancellationToken.None)
                assertOk "later invoice invocation" later
                let invoice = client.Invoices |> List.last
                Assert.That(invoice.AmountStars, Is.EqualTo 73)
                Assert.That(invoice.Title, Is.EqualTo "Сообщение на экран")
                Assert.That(invoice.Description, Is.EqualTo "Публикация сообщения на экране Web10.Radio.")
                let logs = logEntries |> Seq.toList |> String.concat "\n"
                Assert.That(logs, Does.Not.Contain "test-bot-token-that-must-not-reach-logs")
                Assert.That(logs, Does.Not.Contain rawSay)
                Assert.That(logs, Does.Not.Contain "\"paymentId\"")
                Assert.That(logs, Does.Not.Contain "\"providerToken\"")
                Assert.That(logs, Does.Not.Contain "Публикация сообщения на экране Web10.Radio.")
            })

    [<Test>]
    let ``request callback actor and say message preserve distinct payer display-name snapshots in payment orders`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let trackId = Guid.Parse("70000000-0000-0000-0000-000000000001")
                let requestId = Guid.Parse("70000000-0000-0000-0000-000000000002")
                let sayId = Guid.Parse("70000000-0000-0000-0000-000000000003")
                let userId = 700L
                let requestMessageSnapshot = "Request message snapshot"
                let callbackActorSnapshot = "Callback actor snapshot"
                let sayMessageSnapshot = "Say message snapshot"
                do! seedTrack connectionString trackId requestMessageSnapshot "Snapshot artist" false

                let client = CapturingTelegramClient()
                let adapter = TestAdapterState()
                let dataSource, workflow = createWorkflow connectionString 100 50 client adapter NullLogger<TelegramBotWorkflow>.Instance
                use dataSource = dataSource

                let requestPayload =
                    interactionPayloadWithDisplayName
                        1L
                        userId
                        userId
                        (Some requestMessageSnapshot)
                        (Some "en")
                        "/request"
                        None
                        requestMessageSnapshot
                        ""

                let! requested = workflow.HandleInteractionAsync(envelope requestId TrackRequested requestPayload, CancellationToken.None)
                assertOk "matched request with its message snapshot" requested

                let confirmation =
                    match TelegramCallback.encode (TelegramCallback.RequestConfirm requestId) with
                    | Ok value -> value
                    | Error error -> Assert.Fail(sprintf "%A" error); String.Empty

                let confirmationPayload =
                    callbackPayloadWithDisplayName
                        2L
                        (Some userId)
                        userId
                        (Some callbackActorSnapshot)
                        (Some "en")
                        "snapshot-confirmation"
                        confirmation

                let! confirmed =
                    workflow.HandleInteractionAsync(
                        envelope (Guid.Parse("70000000-0000-0000-0000-000000000004")) TelegramCallbackReceived confirmationPayload,
                        CancellationToken.None
                    )

                assertOk "request confirmation with the callback actor snapshot" confirmed

                let sayPayload =
                    interactionPayloadWithDisplayName
                        3L
                        userId
                        userId
                        (Some sayMessageSnapshot)
                        (Some "en")
                        "/say"
                        None
                        ""
                        "persist this display name with the payment"

                let! said = workflow.HandleInteractionAsync(envelope sayId SayMessageSubmitted sayPayload, CancellationToken.None)
                assertOk "say payment with its message snapshot" said

                let! requestPayerDisplayName =
                    scalarString
                        connectionString
                        "SELECT \"PayerDisplayName\" FROM \"Payments\" WHERE \"Purpose\" = 'Request' AND \"PurposeEntityId\" = @Id AND \"IsDeleted\" = false;"
                        [ "Id", box requestId ]

                let! sayPayerDisplayName =
                    scalarString
                        connectionString
                        "SELECT \"PayerDisplayName\" FROM \"Payments\" WHERE \"Purpose\" = 'Say' AND \"PurposeEntityId\" = @Id AND \"IsDeleted\" = false;"
                        [ "Id", box sayId ]

                Assert.That(requestPayerDisplayName, Is.EqualTo(callbackActorSnapshot), "The request payment must snapshot the confirming callback actor, not the earlier request message identity.")
                Assert.That(sayPayerDisplayName, Is.EqualTo(sayMessageSnapshot), "The say payment must snapshot the submitted message actor.")
            })
