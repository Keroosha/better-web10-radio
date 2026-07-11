namespace Web10.Radio.Tests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.Mvc.Testing
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Hosting
open Npgsql
open NUnit.Framework

module ApiContractTests =
    [<Literal>]
    let private WebhookSecret = "test-webhook-secret"

    [<Literal>]
    let private AdminUsername = "test-admin"

    [<Literal>]
    let private AdminPassword = "test-admin-password-1234567890"

    type private SseEvent =
        { Name: string
          Data: string }

    let private configurationPairs connectionString tempRoot =
        let strippedPairs =
            [ "POSTGRES:CONNECTION_STRING", connectionString
              "TELEGRAM:BOT_TOKEN", "123456:AbcdefghijklmnopQRSTuvwx"
              "TELEGRAM:WEBHOOK_SECRET", WebhookSecret
              "TELEGRAM:CHANNEL_ID_OR_USERNAME", "@netscapedidnothingwrong"
              "TELEGRAM:REQUEST_PRICE_STARS", "100"
              "TELEGRAM:SAY_PRICE_STARS", "50"
              "STREAM:RTMP_URL", "rtmps://dc4-1.rtmp.t.me/s/"
              "STREAM:RTMP_KEY", "rtmp-key-Secret_12345"
              "STREAM:STAGE_URL", "https://stage.web10.radio/"
              "STREAM:CALLBACK_TOKEN", "stream-callback-token-Secret_123456"
              "STORAGE:TYPE", "Local"
              "STORAGE:LOCAL_ROOT", Path.Combine(tempRoot, "library")
              "OTEL:EXPORTER_OTLP_ENDPOINT", "http://localhost:4317"
              "ADMIN:USERNAME", AdminUsername
              "ADMIN:PASSWORD", AdminPassword
              "DATA_PROTECTION:KEY_RING_PATH", Path.Combine(tempRoot, "keys") ]

        let web10Mirrors =
            strippedPairs
            |> List.map (fun (key, value) -> sprintf "WEB10_%s" (key.Replace(":", "__")), value)

        strippedPairs @ web10Mirrors
        |> List.map (fun (key, value) -> KeyValuePair<string, string>(key, value))

    type private AdminSession =
        { Username: string
          CsrfToken: string }

    type private FixedClock(nowUtc: DateTimeOffset) =
        interface Web10.Radio.API.IClock with
            member _.UtcNow = nowUtc


    type private AuthenticatedTelegramIdentityProbe() =
        interface Web10.Radio.API.ITelegramIdentityProbe with
            member _.IsAuthenticatedBotAsync(_cancellationToken) = Task.FromResult(true)
    type private RecordingPreCheckoutWorkflow(result: Result<unit, Web10.Radio.API.BackgroundWorkerError>) =
        let inputs = ConcurrentQueue<Web10.Radio.API.TelegramPreCheckoutInput>()

        member _.Inputs = inputs.ToArray()

        interface Web10.Radio.API.ITelegramPreCheckoutWorkflow with
            member _.HandleAsync input _cancellationToken =
                inputs.Enqueue(input)
                Task.FromResult(result)
    type private RecordingTelegramBotClient() =
        let sentTexts = ConcurrentQueue<int64 * string>()
        let sentInvoices = ConcurrentQueue<Web10.Radio.Telegram.TelegramStarsInvoice>()
        let ok () : Task<Result<unit, Web10.Radio.Telegram.TelegramBotError>> = Task.FromResult(Ok())

        member _.SentTexts = sentTexts.ToArray()
        member _.SentInvoices = sentInvoices.ToArray()

        interface Web10.Radio.Telegram.ITelegramBotClient with
            member _.SendTextAsync(chatId, text, _keyboard, _cancellationToken) =
                sentTexts.Enqueue(chatId, text)
                ok ()

            member _.SendInvoiceAsync(invoice, _cancellationToken) =
                sentInvoices.Enqueue(invoice)
                ok ()

            member _.AnswerCallbackAsync(_callbackQueryId, _text, _cancellationToken) = ok ()

            member _.AnswerPreCheckoutAsync(_preCheckoutQueryId, _errorMessage, _cancellationToken) = ok ()

            member _.GetUpdatesAsync(_offset, _cancellationToken) = Task.FromResult(Ok(Array.empty<Funogram.Telegram.Types.Update>))

            member _.DeleteWebhookAsync(_dropPendingUpdates, _cancellationToken) = ok ()


    type private CapturedLog =
        { Level: LogLevel
          Message: string
          Exception: exn option }

    type private CapturingLogger(entries: ConcurrentQueue<CapturedLog>) =
        interface ILogger with
            member _.BeginScope<'TState>(_state: 'TState) : IDisposable = null
            member _.IsEnabled(_level: LogLevel) = true

            member _.Log<'TState>(level: LogLevel, _eventId: EventId, state: 'TState, error: exn, formatter: Func<'TState, exn, string>) =
                entries.Enqueue(
                    { Level = level
                      Message = formatter.Invoke(state, error)
                      Exception = Option.ofObj error }
                )

    type private CapturingLoggerProvider(entries: ConcurrentQueue<CapturedLog>) =
        interface ILoggerProvider with
            member _.CreateLogger(_categoryName) = CapturingLogger(entries) :> ILogger
            member _.Dispose() = ()

    type private ControlledPlayerEventsDelay() =
        let signals = new SemaphoreSlim(0)

        member _.Advance() = signals.Release() |> ignore

        interface Web10.Radio.API.IPlayerEventsDelay with
            member _.WaitForNextSnapshotAsync(cancellationToken) = signals.WaitAsync(cancellationToken)
    type private StartedResponseFeature(statusCode: int) =
        let mutable currentStatusCode = statusCode
        let mutable reasonPhrase: string = null
        let mutable headers: IHeaderDictionary = HeaderDictionary()
        let mutable body: Stream = new MemoryStream()

        interface IHttpResponseFeature with
            member _.StatusCode with get () = currentStatusCode and set value = currentStatusCode <- value
            member _.ReasonPhrase with get () = reasonPhrase and set value = reasonPhrase <- value
            member _.Headers with get () = headers and set value = headers <- value
            member _.Body with get () = body and set value = body <- value
            member _.HasStarted = true
            member _.OnStarting(_, _) = ()
            member _.OnCompleted(_, _) = ()

    let private createFactory
        connectionString
        tempRoot
        (clock: Web10.Radio.API.IClock option)
        (loggerProvider: ILoggerProvider option)
        (eventsDelay: Web10.Radio.API.IPlayerEventsDelay option)
        =
        let pairs = configurationPairs connectionString tempRoot

        (new WebApplicationFactory<Web10.Radio.API.ApiProgramMarker>())
            .WithWebHostBuilder(fun builder ->
                pairs |> List.iter (fun pair -> builder.UseSetting(pair.Key, pair.Value) |> ignore)

                builder.ConfigureAppConfiguration(fun _ configurationBuilder ->
                    configurationBuilder.AddInMemoryCollection(pairs) |> ignore)
                |> ignore

                match clock with
                | Some value ->
                    builder.ConfigureServices(fun services ->
                        services.AddSingleton<Web10.Radio.API.IClock>(value) |> ignore)
                    |> ignore
                | None -> ()

                match eventsDelay with
                | Some value ->
                    builder.ConfigureServices(fun services ->
                        services.AddSingleton<Web10.Radio.API.IPlayerEventsDelay>(value) |> ignore)
                    |> ignore
                | None -> ()

                match loggerProvider with
                | Some provider ->
                    builder.ConfigureLogging(fun logging -> logging.AddProvider(provider) |> ignore)
                    |> ignore
                | None -> ())

    let private withApiClient (work: string -> HttpClient -> Task<'T>) =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let tempRoot = Directory.CreateTempSubdirectory("web10-radio-api-tests-")
                let factory = createFactory connectionString tempRoot.FullName None None None
                let client = factory.CreateClient(WebApplicationFactoryClientOptions(HandleCookies = true))

                try
                    let! result = work connectionString client
                    return result
                finally
                    client.Dispose()
                    factory.Dispose()
                    tempRoot.Delete(true)
            })

    let private withApiClientAndEventsDelay
        nowUtc
        (delay: ControlledPlayerEventsDelay)
        (work: string -> HttpClient -> Task<'T>)
        =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let tempRoot = Directory.CreateTempSubdirectory("web10-radio-api-tests-")
                let clock = FixedClock(nowUtc) :> Web10.Radio.API.IClock
                let factory =
                    createFactory
                        connectionString
                        tempRoot.FullName
                        (Some clock)
                        None
                        (Some(delay :> Web10.Radio.API.IPlayerEventsDelay))

                let client = factory.CreateClient(WebApplicationFactoryClientOptions(HandleCookies = true))

                try
                    return! work connectionString client
                finally
                    client.Dispose()
                    factory.Dispose()
                    tempRoot.Delete(true)
            })

    let private withApiClientAt nowUtc (work: string -> string -> HttpClient -> Task<'T>) =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let tempRoot = Directory.CreateTempSubdirectory("web10-radio-api-tests-")
                let clock = FixedClock(nowUtc) :> Web10.Radio.API.IClock
                let factory = createFactory connectionString tempRoot.FullName (Some clock) None None
                let client = factory.CreateClient(WebApplicationFactoryClientOptions(HandleCookies = true))

                try
                    let! result = work connectionString tempRoot.FullName client
                    return result
                finally
                    client.Dispose()
                    factory.Dispose()
                    tempRoot.Delete(true)
            })

    let private withApiClientWithServices
        (configureServices: IServiceCollection -> unit)
        (work: string -> string -> WebApplicationFactory<Web10.Radio.API.ApiProgramMarker> -> HttpClient -> Task<'T>)
        =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let tempRoot = Directory.CreateTempSubdirectory("web10-radio-api-tests-")

                let factory =
                    (createFactory connectionString tempRoot.FullName None None None)
                        .WithWebHostBuilder(fun builder ->
                            builder.ConfigureServices(fun services -> configureServices services) |> ignore)

                let client = factory.CreateClient(WebApplicationFactoryClientOptions(HandleCookies = true))

                try
                    return! work connectionString tempRoot.FullName factory client
                finally
                    client.Dispose()
                    factory.Dispose()
                    tempRoot.Delete(true)
            })

    let private jsonDocument (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()

            try
                return JsonDocument.Parse(body)
            with ex ->
                return raise (AssertionException(sprintf "Expected JSON response but got: %s" body, ex))
        }

    let private jsonProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &value) then
            value
        else
            raise (AssertionException(sprintf "Expected JSON property '%s' on %O." name element))

    let private stringProperty (name: string) element =
        (jsonProperty name element).GetString()

    let private intProperty (name: string) element =
        (jsonProperty name element).GetInt32()

    let private boolProperty (name: string) element =
        (jsonProperty name element).GetBoolean()

    let private arrayLengthProperty (name: string) element =
        (jsonProperty name element).GetArrayLength()

    let private valueKindProperty (name: string) element =
        (jsonProperty name element).ValueKind


    let private assertProblemCode (expectedCode: string) (response: HttpResponseMessage) =
        task {
            let! document = jsonDocument response
            use document = document
            Assert.That(document.RootElement |> stringProperty "code", Is.EqualTo(expectedCode))
        }

    let private postJson (client: HttpClient) (uri: string) (body: string) (secret: string option) =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, uri)
            request.Content <- new StringContent(body, Encoding.UTF8, "application/json")

            match secret with
            | Some value -> request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", value)
            | None -> ()

            return! client.SendAsync(request)
        }

    let private sendWebhook (client: HttpClient) (body: string) (secrets: string list) =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, "/api/v0/telegram/webhook")
            request.Content <- new StringContent(body, Encoding.UTF8, "application/json")

            for secret in secrets do
                request.Headers.TryAddWithoutValidation("X-Telegram-Bot-Api-Secret-Token", secret) |> ignore

            return! client.SendAsync(request)
        }

    let private sendAdminRequest (client: HttpClient) (csrfToken: string option) (method': HttpMethod) (uri: string) =
        task {
            use request = new HttpRequestMessage(method', uri)

            if method' <> HttpMethod.Get then
                request.Content <- new StringContent("{}", Encoding.UTF8, "application/json")

            match csrfToken with
            | Some value -> request.Headers.TryAddWithoutValidation("X-CSRF-Token", value) |> ignore
            | None -> ()

            return! client.SendAsync(request)
        }

    let private sendAdminJson
        (client: HttpClient)
        (csrfToken: string option)
        (uri: string)
        (body: string)
        =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, uri)
            request.Content <- new StringContent(body, Encoding.UTF8, "application/json")

            match csrfToken with
            | Some value -> request.Headers.TryAddWithoutValidation("X-CSRF-Token", value) |> ignore
            | None -> ()

            return! client.SendAsync(request)
        }

    let private sendAdminJsonWithMethod
        (client: HttpClient)
        (csrfToken: string option)
        (method': HttpMethod)
        (uri: string)
        (body: string)
        =
        task {
            use request = new HttpRequestMessage(method', uri)
            request.Content <- new StringContent(body, Encoding.UTF8, "application/json")

            match csrfToken with
            | Some value -> request.Headers.TryAddWithoutValidation("X-CSRF-Token", value) |> ignore
            | None -> ()

            return! client.SendAsync(request)
        }

    let private sendAdminLogin (client: HttpClient) username password =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, "/api/v0/admin/auth/login")
            request.Content <- new StringContent(sprintf "{\"username\":%s,\"password\":%s}" (JsonSerializer.Serialize(username)) (JsonSerializer.Serialize(password)), Encoding.UTF8, "application/json")
            return! client.SendAsync(request)
        }

    let private loginAdmin (client: HttpClient) =
        task {
            let! response = sendAdminLogin client AdminUsername AdminPassword
            use response = response
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "The configured bootstrap administrator must establish a session.")
            let! document = jsonDocument response
            use document = document
            let session =
                { Username = document.RootElement |> stringProperty "username"
                  CsrfToken = document.RootElement |> stringProperty "csrfToken" }
            Assert.That(session.Username, Is.EqualTo(AdminUsername))
            Assert.That(String.IsNullOrWhiteSpace(session.CsrfToken), Is.False, "A successful login must issue a nonempty synchronizer token.")
            return session
        }

    let private sendPlaybackCallback
        (client: HttpClient)
        (uri: string)
        (body: string)
        (authorizations: string list)
        =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, uri)
            request.Content <- new StringContent(body, Encoding.UTF8, "application/json")

            for authorization in authorizations do
                request.Headers.TryAddWithoutValidation("Authorization", authorization) |> ignore

            return! client.SendAsync(request)
        }

    let private typedRequestUpdate updateId query =
        sprintf
            """{"update_id":%d,"message":{"message_id":7,"date":1783400400,"chat":{"id":500,"type":"private"},"from":{"id":500,"is_bot":false,"first_name":"Regression"},"text":"/request %s"}}"""
            updateId
            query


    let private typedSayUpdate updateId text =
        sprintf
            """{"update_id":%d,"message":{"message_id":8,"date":1783400401,"chat":{"id":501,"type":"private"},"from":{"id":501,"is_bot":false,"first_name":"Relay"},"text":"/say %s"}}"""
            updateId
            text

    let private typedSuccessfulPaymentUpdate updateId paymentId chargeId amountStars =
        sprintf
            """{"update_id":%d,"message":{"message_id":9,"date":1783400402,"chat":{"id":502,"type":"private"},"from":{"id":502,"is_bot":false,"first_name":"Payer"},"successful_payment":{"currency":"XTR","total_amount":%d,"invoice_payload":"%O","telegram_payment_charge_id":"%s"}}}"""
            updateId
            amountStars
            paymentId
            chargeId
    let private typedPrivateCommandUpdate updateId userId languageCode command =
        sprintf
            """{"update_id":%d,"message":{"message_id":10,"date":1783400403,"chat":{"id":%d,"type":"private"},"from":{"id":%d,"is_bot":false,"first_name":"Locale","language_code":"%s"},"text":"%s"}}"""
            updateId
            userId
            userId
            languageCode
            command

    let private typedGroupCommandUpdate updateId userId command =
        sprintf
            """{"update_id":%d,"message":{"message_id":11,"date":1783400404,"chat":{"id":-100502,"type":"group"},"from":{"id":%d,"is_bot":false,"first_name":"Group"},"text":"%s"}}"""
            updateId
            userId
            command

    let private typedCallbackUpdate updateId userId languageCode callbackData =
        sprintf
            """{"update_id":%d,"callback_query":{"id":"callback-%d","from":{"id":%d,"is_bot":false,"first_name":"Callback","language_code":"%s"},"message":{"message_id":12,"date":1783400405,"chat":{"id":%d,"type":"private"}},"data":"%s"}}"""
            updateId
            updateId
            userId
            languageCode
            userId
            callbackData

    let private typedPreCheckoutUpdate updateId userId languageCode amountStars payload =
        sprintf
            """{"update_id":%d,"pre_checkout_query":{"id":"precheckout-%d","from":{"id":%d,"is_bot":false,"first_name":"Checkout","language_code":"%s"},"currency":"XTR","total_amount":%d,"invoice_payload":"%s"}}"""
            updateId
            updateId
            userId
            languageCode
            amountStars
            payload
    let private paddedTypedRequestUpdate updateId targetBytes =
        let prefix =
            sprintf
                "{\"update_id\":%d,\"message\":{\"message_id\":7,\"date\":1783400400,\"chat\":{\"id\":500,\"type\":\"private\"},\"from\":{\"id\":500,\"is_bot\":false,\"first_name\":\"Regression\"},\"text\":\"/request at-limit\"},\"padding\":\""
                updateId

        let suffix = "\"}"
        let paddingLength = targetBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix)

        if paddingLength < 0 then
            raise (AssertionException(sprintf "Cannot form a %d-byte valid Telegram update." targetBytes))

        prefix + String.replicate paddingLength "x" + suffix

    let private countOutboxRows (connectionString: string) (eventType: string) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT count(*)
FROM "OutboxEvents"
WHERE "EventType" = @EventType
  AND "IsDeleted" = false;""",
                    connection
                )

            command.Parameters.AddWithValue("EventType", eventType) |> ignore
            let! count = command.ExecuteScalarAsync()
            return Convert.ToInt32(count)
        }

    let private seedBootstrapAdmin connectionString =
        task {
            let nowUtc = DateTimeOffset.UtcNow
            let user: Web10.Radio.Database.Repositories.AdminUser =
                { Id = Guid.NewGuid()
                  Username = AdminUsername
                  NormalizedUsername = AdminUsername.ToUpperInvariant()
                  PasswordHash = ""
                  CreatedAtUtc = nowUtc
                  UpdatedAtUtc = nowUtc }
            let passwordHash = PasswordHasher<Web10.Radio.Database.Repositories.AdminUser>().HashPassword(user, AdminPassword)
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand("INSERT INTO \"AdminUsers\" (\"Id\", \"Username\", \"NormalizedUsername\", \"PasswordHash\", \"IsDeleted\", \"CreatedAtUtc\", \"UpdatedAtUtc\") VALUES (@Id, @Username, @NormalizedUsername, @PasswordHash, false, @NowUtc, @NowUtc);", connection)
            command.Parameters.AddWithValue("Id", user.Id) |> ignore
            command.Parameters.AddWithValue("Username", user.Username) |> ignore
            command.Parameters.AddWithValue("NormalizedUsername", user.NormalizedUsername) |> ignore
            command.Parameters.AddWithValue("PasswordHash", passwordHash) |> ignore
            command.Parameters.AddWithValue("NowUtc", nowUtc) |> ignore
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    let private countTrackRequestRows (connectionString: string) (query: string) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand("""SELECT count(*) FROM "TrackRequests" WHERE "Query" = @Query AND "IsDeleted" = false;""", connection)
            command.Parameters.AddWithValue("Query", query) |> ignore
            let! count = command.ExecuteScalarAsync()
            return Convert.ToInt32(count)
        }

    let private countSayMessageRows (connectionString: string) (text: string) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand("""SELECT count(*) FROM "SayMessages" WHERE "Text" = @Text AND "IsDeleted" = false;""", connection)
            command.Parameters.AddWithValue("Text", text) |> ignore
            let! count = command.ExecuteScalarAsync()
            return Convert.ToInt32(count)
        }
    let private countActivePaymentRows (connectionString: string) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand("SELECT count(*) FROM \"Payments\" WHERE \"IsDeleted\" = false;", connection)
            let! count = command.ExecuteScalarAsync()
            return Convert.ToInt32(count)
        }

    let private seedDonationPayment connectionString paymentId amountStars =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """INSERT INTO "Payments" ("Id", "TelegramUserId", "Purpose", "AmountStars", "Currency", "TelegramInvoicePayload", "Status", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@PaymentId, 502, 'Donation', @AmountStars, 'XTR', @InvoicePayload, 'InvoiceCreated', false, @NowUtc, @NowUtc);""",
                    connection
                )

            command.Parameters.AddWithValue("PaymentId", paymentId) |> ignore
            command.Parameters.AddWithValue("AmountStars", amountStars) |> ignore
            command.Parameters.AddWithValue("InvoicePayload", string paymentId) |> ignore
            command.Parameters.AddWithValue("NowUtc", DateTimeOffset(2026, 7, 10, 22, 30, 0, TimeSpan.Zero)) |> ignore
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }
    let private seedSayMessage
        connectionString
        (messageId: Guid)
        (telegramUserId: int64 option)
        displayName
        text
        amountStars
        (color: string option)
        status
        (submittedAtUtc: DateTimeOffset)
        (paidAtUtc: DateTimeOffset option)
        isDeleted
        =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """INSERT INTO "SayMessages" (
    "Id", "TelegramUserId", "DisplayName", "Text", "AmountStars", "Color", "Status", "SubmittedAtUtc", "PaidAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (
    @Id, @TelegramUserId, @DisplayName, @Text, @AmountStars, @Color, @Status, @SubmittedAtUtc, @PaidAtUtc, @IsDeleted, @SubmittedAtUtc, @SubmittedAtUtc
);""",
                    connection
                )

            command.Parameters.AddWithValue("Id", messageId) |> ignore
            command.Parameters.AddWithValue("TelegramUserId", telegramUserId |> Option.map box |> Option.defaultValue DBNull.Value) |> ignore
            command.Parameters.AddWithValue("DisplayName", displayName) |> ignore
            command.Parameters.AddWithValue("Text", text) |> ignore
            command.Parameters.AddWithValue("AmountStars", amountStars) |> ignore
            command.Parameters.AddWithValue("Color", color |> Option.map box |> Option.defaultValue DBNull.Value) |> ignore
            command.Parameters.AddWithValue("Status", status) |> ignore
            command.Parameters.AddWithValue("SubmittedAtUtc", submittedAtUtc) |> ignore
            command.Parameters.AddWithValue("PaidAtUtc", paidAtUtc |> Option.map box |> Option.defaultValue DBNull.Value) |> ignore
            command.Parameters.AddWithValue("IsDeleted", isDeleted) |> ignore
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    let private seedPaidSayPayment connectionString (messageId: Guid) amountStars (paidAtUtc: DateTimeOffset) =
        task {
            let paymentId = Guid.NewGuid()
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """INSERT INTO "Payments" (
    "Id", "TelegramUserId", "Purpose", "PurposeEntityId", "AmountStars", "Currency", "ProviderToken", "TelegramInvoicePayload", "TelegramPaymentChargeId", "Status", "PaidAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (
    @Id, 7001, 'Say', @PurposeEntityId, @AmountStars, 'XTR', '', @InvoicePayload, @ChargeId, 'Paid', @PaidAtUtc, false, @PaidAtUtc, @PaidAtUtc
);""",
                    connection
                )

            command.Parameters.AddWithValue("Id", paymentId) |> ignore
            command.Parameters.AddWithValue("PurposeEntityId", messageId) |> ignore
            command.Parameters.AddWithValue("AmountStars", amountStars) |> ignore
            command.Parameters.AddWithValue("InvoicePayload", paymentId.ToString("D")) |> ignore
            command.Parameters.AddWithValue("ChargeId", "paid-say-" + paymentId.ToString("N")) |> ignore
            command.Parameters.AddWithValue("PaidAtUtc", paidAtUtc) |> ignore
            let! _ = command.ExecuteNonQueryAsync()
            return paymentId
        }

    let private readSayModerationState connectionString (messageId: Guid) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT
    "Status",
    "ModerationReason",
    (SELECT "Status" FROM "Payments" WHERE "PurposeEntityId" = @Id AND "Purpose" = 'Say' AND "IsDeleted" = false),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'SayMessageModerated' AND "Payload" ->> 'sayMessageId' = @IdText AND "IsDeleted" = false)
FROM "SayMessages"
WHERE "Id" = @Id;""",
                    connection
                )

            command.Parameters.AddWithValue("Id", messageId) |> ignore
            command.Parameters.AddWithValue("IdText", messageId.ToString("D")) |> ignore
            use! reader = command.ExecuteReaderAsync()
            let! found = reader.ReadAsync()

            if not found then
                return raise (AssertionException(sprintf "Expected SayMessages row %O." messageId))
            else
                return
                    reader.GetString(0),
                    (if reader.IsDBNull 1 then None else Some(reader.GetString 1)),
                    (if reader.IsDBNull 2 then None else Some(reader.GetString 2)),
                    reader.GetInt64(3)
        }


    let private seedHeartbeat connectionString status heartbeatAtUtc =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """INSERT INTO "StreamNodeHeartbeats" (
    "Id", "Status", "HeartbeatAtUtc", "FailureReason", "Metadata", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (@Id, @Status, @HeartbeatAtUtc, NULL, '{"bitrateKbps":192}'::jsonb, false, @HeartbeatAtUtc, @HeartbeatAtUtc);""",
                    connection
                )

            command.Parameters.AddWithValue("Id", Guid.NewGuid()) |> ignore
            command.Parameters.AddWithValue("Status", status) |> ignore
            command.Parameters.AddWithValue("HeartbeatAtUtc", heartbeatAtUtc) |> ignore
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    let private seedCachedPlayingTrack connectionString cacheRoot (startedAtUtc: DateTimeOffset) =
        task {
            Directory.CreateDirectory(cacheRoot) |> ignore
            let cachePath = Path.Combine(cacheRoot, "live-track.mp3")
            let bytes = Encoding.ASCII.GetBytes("0123456789")
            do! File.WriteAllBytesAsync(cachePath, bytes)

            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            let trackId = Guid.NewGuid()
            let fileId = Guid.NewGuid()
            let queueItemId = Guid.NewGuid()

            use command =
                new NpgsqlCommand(
                    """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@TrackId, 'Live range track', 'Regression', false, @NowUtc, @NowUtc);
INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "CachePath", "ContentType", "SizeBytes", "IsCached", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@FileId, @TrackId, 'live-track.mp3', @CachePath, 'audio/mpeg', 10, true, false, @NowUtc, @NowUtc);
INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "RequestedAtUtc", "StartedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@QueueItemId, @TrackId, 'admin', 'Playing', @NowUtc, @NowUtc, @ClaimOwner, 1, @ClaimLeaseExpiresAtUtc, false, @NowUtc, @NowUtc);""",
                    connection
                )

            command.Parameters.AddWithValue("TrackId", trackId) |> ignore
            command.Parameters.AddWithValue("FileId", fileId) |> ignore
            command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
            command.Parameters.AddWithValue("CachePath", cachePath) |> ignore
            command.Parameters.AddWithValue("NowUtc", startedAtUtc) |> ignore
            command.Parameters.AddWithValue("ClaimOwner", Guid.NewGuid()) |> ignore
            command.Parameters.AddWithValue("ClaimLeaseExpiresAtUtc", startedAtUtc.AddMinutes(1.0)) |> ignore
            let! _ = command.ExecuteNonQueryAsync()
            return bytes
        }

    let private countInboxRows (connectionString: string) (telegramUpdateId: int64) (eventType: string) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT count(*)
FROM "TelegramUpdateInbox"
WHERE "TelegramUpdateId" = @TelegramUpdateId
  AND "EventType" = @EventType
  AND "IsDeleted" = false;""",
                    connection
                )

            command.Parameters.AddWithValue("TelegramUpdateId", telegramUpdateId) |> ignore
            command.Parameters.AddWithValue("EventType", eventType) |> ignore
            let! count = command.ExecuteScalarAsync()
            return Convert.ToInt32(count)
        }

    let private countAllInboxRows (connectionString: string) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT count(*)
FROM "TelegramUpdateInbox"
WHERE "IsDeleted" = false;""",
                    connection
                )

            let! count = command.ExecuteScalarAsync()
            return Convert.ToInt32(count)
        }

    let private assertNoWebhookEffects connectionString =
        task {
            let! inboxCount = countAllInboxRows connectionString
            let! outboxCount = countOutboxRows connectionString "TrackRequested"
            Assert.That(inboxCount, Is.EqualTo(0), "Rejected webhook must not create an inbox record.")
            Assert.That(outboxCount, Is.EqualTo(0), "Rejected webhook must not create a durable domain event.")
        }

    let private seedAdminReadModels connectionString =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use socialCommand =
                new NpgsqlCommand(
                    """INSERT INTO "SocialLinks" (
    "Id", "Kind", "Name", "Handle", "Url", "Glyph", "Color", "QrImageUrl", "IsFeatured", "Position"
)
VALUES (@SocialLinkId, 'telegram', 'Telegram', NULL, 'https://t.me/web10radio', NULL, NULL, NULL, true, 2);""",
                    connection
                )

            socialCommand.Parameters.AddWithValue("SocialLinkId", Guid.Parse("01920000-0000-7000-8000-00000000a001")) |> ignore
            let! _ = socialCommand.ExecuteNonQueryAsync()

            use goalCommand =
                new NpgsqlCommand(
                    """INSERT INTO "DonationGoals" (
    "Id", "Title", "GoalStars", "RaisedStars", "IsActive"
)
VALUES (@DonationGoalId, 'Keep Web10.Radio live', 1000, 250, true);""",
                    connection
                )

            goalCommand.Parameters.AddWithValue("DonationGoalId", Guid.Parse("01920000-0000-7000-8000-00000000a002")) |> ignore
            let! _ = goalCommand.ExecuteNonQueryAsync()
            return ()
        }

    let private seedSseFragments connectionString nowUtc =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            let trackId = Guid.NewGuid()
            let queueItemId = Guid.NewGuid()
            let sayMessageId = Guid.NewGuid()

            use command =
                new NpgsqlCommand(
                    """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@TrackId, 'SSE queue track', 'SSE artist', false, @NowUtc, @NowUtc);
INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "RequestedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@QueueItemId, @TrackId, 'admin', 'Queued', @NowUtc, false, @NowUtc, @NowUtc);
INSERT INTO "SayMessages" ("Id", "DisplayName", "Text", "AmountStars", "Color", "Status", "SubmittedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@SayMessageId, 'SSE sender', 'SSE moderation payload', 100, '#e0439a', 'Approved', @NowUtc, false, @NowUtc, @NowUtc);""",
                    connection
                )

            command.Parameters.AddWithValue("TrackId", trackId) |> ignore
            command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
            command.Parameters.AddWithValue("SayMessageId", sayMessageId) |> ignore
            command.Parameters.AddWithValue("NowUtc", nowUtc) |> ignore
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    let private readSseEvent (reader: StreamReader) (cancellationToken: CancellationToken) =
        task {
            let dataLines = ResizeArray<string>()
            let mutable eventName = ""
            let mutable isComplete = false

            while not isComplete do
                let! line = (reader.ReadLineAsync()).WaitAsync(cancellationToken)

                if isNull line then
                    raise (AssertionException("SSE stream ended before an event was emitted."))
                elif line = "" then
                    if eventName <> "" || dataLines.Count > 0 then
                        isComplete <- true
                elif line.StartsWith("event:", StringComparison.Ordinal) then
                    eventName <- line.Substring("event:".Length).TrimStart()
                elif line.StartsWith("data:", StringComparison.Ordinal) then
                    dataLines.Add(line.Substring("data:".Length).TrimStart())

            return
                { Name = eventName
                  Data = String.concat "\n" dataLines }
        }

    let private readFirstSseEvent (stream: Stream) (cancellationToken: CancellationToken) =
        task {
            use reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true)
            return! readSseEvent reader cancellationToken
        }

    [<Test>]
    let ``GET player stream without live cached track returns stream unavailable problem`` () =
        withApiClient (fun _ client ->
            task {
                let! response = client.GetAsync("/api/v0/player/stream")
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable))
                do! assertProblemCode "stream.unavailable" response
            })



    let private formerlyPlaceholderAdminRoutes =
        [ HttpMethod.Post, "/api/v0/admin/library/scan", "{}"
          HttpMethod.Get, "/api/v0/admin/library/scan/01920000-0000-7000-8000-000000000101", ""
          HttpMethod.Get, "/api/v0/admin/tracks", ""
          HttpMethod.Post, "/api/v0/admin/playback/queue", "{\"trackId\":\"01920000-0000-7000-8000-000000000102\"}"
          HttpMethod.Put, "/api/v0/admin/social-links", "[]"
          HttpMethod.Put, "/api/v0/admin/donation-goal", "{\"title\":\"Goal\",\"goalStars\":100}"
          HttpMethod.Get, "/api/v0/admin/playlists", ""
          HttpMethod.Post, "/api/v0/admin/playlists", "{\"name\":\"Mix\",\"description\":null,\"isActive\":false}"
          HttpMethod.Put, "/api/v0/admin/playlists/01920000-0000-7000-8000-000000000103", "{\"name\":\"Mix\",\"description\":null,\"isActive\":false}"
          HttpMethod.Get, "/api/v0/admin/playlists/01920000-0000-7000-8000-000000000103/items", ""
          HttpMethod.Post, "/api/v0/admin/playlists/01920000-0000-7000-8000-000000000103/items", "{\"trackId\":\"01920000-0000-7000-8000-000000000102\"}"
          HttpMethod.Put, "/api/v0/admin/playlists/01920000-0000-7000-8000-000000000103/items", "{\"items\":[]}"
          HttpMethod.Get, "/api/v0/admin/storage", ""
          HttpMethod.Put, "/api/v0/admin/storage", "{\"additionalBackends\":[]}"
          HttpMethod.Get, "/api/v0/admin/stream-node/status", ""
          HttpMethod.Post, "/api/v0/admin/stream-node/start", "{}"
          HttpMethod.Post, "/api/v0/admin/stream-node/stop", "{}"
          HttpMethod.Post, "/api/v0/admin/stream-node/restart", "{}" ]

    let private outboxPayloadText connectionString eventType =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT "Payload" ->> 'query'
FROM "OutboxEvents"
WHERE "EventType" = @EventType
  AND "IsDeleted" = false
ORDER BY "OccurredAtUtc" ASC
LIMIT 1;""",
                    connection
                )

            command.Parameters.AddWithValue("EventType", eventType) |> ignore
            let! value = command.ExecuteScalarAsync()
            return if isNull value || value = box DBNull.Value then null else string value
        }
    let private outboxPayloadForUpdate connectionString eventType telegramUpdateId =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT "Payload"::text
FROM "OutboxEvents"
WHERE "EventType" = @EventType
  AND "Payload" ->> 'telegramUpdateId' = @TelegramUpdateId
  AND "IsDeleted" = false
LIMIT 1;""",
                    connection
                )

            command.Parameters.AddWithValue("EventType", eventType) |> ignore
            command.Parameters.AddWithValue("TelegramUpdateId", string telegramUpdateId) |> ignore
            let! value = command.ExecuteScalarAsync()

            if isNull value || value = box DBNull.Value then
                return raise (AssertionException(sprintf "Expected %s outbox payload for Telegram update %d." eventType telegramUpdateId))
            else
                return string value
        }

    [<Test>]
    let ``admin login persists only a hashed eight-hour cookie session restores without rotation and logout revokes it`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 15, 0, 0, TimeSpan.Zero)

        withApiClientAt nowUtc (fun connectionString _ client ->
            task {
                let! login = sendAdminLogin client AdminUsername AdminPassword
                use login = login
                Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! loginDocument = jsonDocument login
                use loginDocument = loginDocument
                let csrfToken = loginDocument.RootElement |> stringProperty "csrfToken"
                Assert.That(loginDocument.RootElement |> stringProperty "username", Is.EqualTo(AdminUsername))
                let setCookie = login.Headers.GetValues("Set-Cookie") |> Seq.find (fun value -> value.StartsWith("web10_admin_session=", StringComparison.Ordinal))
                let cookieAttributes =
                    setCookie.Split(';')
                    |> Seq.skip 1
                    |> Seq.map (fun value -> value.Trim().ToLowerInvariant())
                    |> Set.ofSeq
                Assert.That(cookieAttributes.Contains("httponly"), Is.True)
                Assert.That(cookieAttributes.Contains("samesite=strict"), Is.True)
                Assert.That(cookieAttributes.Contains("path=/api/v0/admin"), Is.True)
                Assert.That(cookieAttributes.Contains("max-age=28800"), Is.True)
                Assert.That(cookieAttributes.Contains("secure"), Is.False, "Development permits the localhost HTTP test host.")
                let token = setCookie.Split(';').[0].Split('=').[1]
                let tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use persisted = new NpgsqlCommand("SELECT encode(\"TokenHash\", 'hex'), EXTRACT(EPOCH FROM (\"ExpiresAtUtc\" - \"CreatedAtUtc\"))::bigint FROM \"AdminSessions\" WHERE \"IsDeleted\" = false;", connection)
                use! persistedReader = persisted.ExecuteReaderAsync()
                let! found = persistedReader.ReadAsync()
                Assert.That(found, Is.True)
                Assert.That(persistedReader.GetString(0), Is.EqualTo(tokenHash), "The bearer cookie value must never be stored as the authentication credential.")
                Assert.That(persistedReader.GetInt64(1), Is.EqualTo(28800L), "Sessions have a fixed eight-hour lifetime.")
                do! persistedReader.CloseAsync()

                let! restored = client.GetAsync("/api/v0/admin/auth/session")
                use restored = restored
                Assert.That(restored.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(restored.Headers.Contains("Set-Cookie"), Is.False, "Session probing must not rotate an opaque session.")
                let! restoredDocument = jsonDocument restored
                use restoredDocument = restoredDocument
                Assert.That(restoredDocument.RootElement |> stringProperty "csrfToken", Is.EqualTo(csrfToken))

                use expire = new NpgsqlCommand("UPDATE \"AdminSessions\" SET \"ExpiresAtUtc\" = @ExpiredAtUtc WHERE \"IsDeleted\" = false;", connection)
                expire.Parameters.AddWithValue("ExpiredAtUtc", nowUtc.AddTicks(-1L)) |> ignore
                let! _ = expire.ExecuteNonQueryAsync()
                let! expired = client.GetAsync("/api/v0/admin/auth/session")
                use expired = expired
                Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
                do! assertProblemCode "admin.auth.required" expired

                let! secondSession = loginAdmin client
                let! logout = sendAdminRequest client (Some secondSession.CsrfToken) HttpMethod.Post "/api/v0/admin/auth/logout"
                use logout = logout
                Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                Assert.That(logout.Headers.GetValues("Set-Cookie") |> Seq.exists (fun value -> value.Contains("Max-Age=0", StringComparison.Ordinal)), Is.True)
                use revoked = new NpgsqlCommand("SELECT count(*) FROM \"AdminSessions\" WHERE \"RevokedAtUtc\" IS NOT NULL AND \"IsDeleted\" = false;", connection)
                let! revokedCount = revoked.ExecuteScalarAsync()
                Assert.That(Convert.ToInt32(revokedCount), Is.EqualTo(1), "Logout must revoke the active server-side session.")
                let! afterLogout = client.GetAsync("/api/v0/admin/auth/session")
                use afterLogout = afterLogout
                Assert.That(afterLogout.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
            })

    [<Test>]
    let ``admin login keeps invalid credentials indistinguishable and rejects malformed or oversized bodies`` () =
        withApiClient (fun _ client ->
            task {
                let attempts = [ "missing user", "wrong-password"; "wrong-user", AdminPassword ]
                let responses = ResizeArray<HttpStatusCode * string * string * string>()

                for username, password in attempts do
                    let! response = sendAdminLogin client username password
                    use response = response
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
                    let! document = jsonDocument response
                    use document = document
                    let problem = document.RootElement
                    let code = problem |> stringProperty "code"
                    Assert.That(code, Is.EqualTo("admin.auth.invalid_credentials"))
                    responses.Add((response.StatusCode, code, problem |> stringProperty "title", problem |> stringProperty "message"))

                Assert.That(responses.[0], Is.EqualTo(responses.[1]), "Unknown usernames and wrong passwords must expose the same status and public problem details, excluding per-request trace IDs.")

                for malformed in [ "[]"; "{\"username\":\"admin\",\"password\":\"password-1234\",\"extra\":true}"; "{\"username\":\"   \",\"password\":\"password-1234\"}" ] do
                    use malformedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v0/admin/auth/login")
                    malformedRequest.Content <- new StringContent(malformed, Encoding.UTF8, "application/json")
                    let! malformedResponse = client.SendAsync(malformedRequest)
                    use malformedResponse = malformedResponse
                    Assert.That(malformedResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                    do! assertProblemCode "admin.auth.request_invalid" malformedResponse

                let oversized = "{\"username\":\"admin\",\"password\":\"" + String.replicate 4096 "x" + "\"}"
                use oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v0/admin/auth/login")
                oversizedRequest.Content <- new StringContent(oversized, Encoding.UTF8, "application/json")
                let! oversizedResponse = client.SendAsync(oversizedRequest)
                use oversizedResponse = oversizedResponse
                Assert.That(oversizedResponse.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge))
                do! assertProblemCode "request.too_large" oversizedResponse
            })

    [<Test>]
    let ``admin session is the only credential and every formerly-placeholder route enforces CSRF before returning a domain response`` () =
        withApiClient (fun _ client ->
            task {
                for method', uri, body in formerlyPlaceholderAdminRoutes do
                    let! anonymous = sendAdminJsonWithMethod client None method' uri (if body = "" then "{}" else body)
                    use anonymous = anonymous
                    Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), sprintf "%O %s must require a session cookie." method' uri)
                    do! assertProblemCode "admin.auth.required" anonymous

                use bearerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v0/admin/social-links")
                bearerRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer legacy-admin-token") |> ignore
                let! bearer = client.SendAsync(bearerRequest)
                use bearer = bearer
                Assert.That(bearer.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "Authorization headers must not provide a bearer compatibility path.")
                do! assertProblemCode "admin.auth.required" bearer

                let! session = loginAdmin client

                for method', uri, body in formerlyPlaceholderAdminRoutes do
                    if method' <> HttpMethod.Get then
                        let! csrfRejected = sendAdminJsonWithMethod client None method' uri body
                        use csrfRejected = csrfRejected
                        Assert.That(csrfRejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), sprintf "%O %s must reject a missing synchronizer token." method' uri)
                        do! assertProblemCode "admin.auth.csrf_invalid" csrfRejected

                    let! domainResponse = sendAdminJsonWithMethod client (Some session.CsrfToken) method' uri (if body = "" then "{}" else body)
                    use domainResponse = domainResponse
                    Assert.That(domainResponse.StatusCode, Is.Not.EqualTo(HttpStatusCode.NotImplemented), sprintf "%O %s must be mapped to a domain handler." method' uri)
                    Assert.That(domainResponse.StatusCode, Is.Not.EqualTo(HttpStatusCode.Unauthorized), sprintf "%O %s must accept the active session." method' uri)
                    Assert.That(domainResponse.StatusCode, Is.Not.EqualTo(HttpStatusCode.Forbidden), sprintf "%O %s must accept the valid synchronizer token." method' uri)

                for uri in [ sprintf "/api/v0/admin/say-messages/%O/approve" (Guid.NewGuid()); sprintf "/api/v0/admin/say-messages/%O/reject" (Guid.NewGuid()) ] do
                    let! csrfRejected = sendAdminJson client None uri "{}"
                    use csrfRejected = csrfRejected
                    Assert.That(csrfRejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "Say moderation is an admin mutation and must participate in the global CSRF matrix.")
                    do! assertProblemCode "admin.auth.csrf_invalid" csrfRejected
            })

    [<Test>]
    let ``say list requires one exact lowercase moderation status and exposes only paid-pending DTOs`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 18, 0, 0, TimeSpan.Zero)

        withApiClient (fun connectionString client ->
            task {
                let unpaidId = Guid.NewGuid()
                let pendingId = Guid.NewGuid()

                do!
                    seedSayMessage
                        connectionString
                        unpaidId
                        (Some 7000L)
                        "Unpaid"
                        "must not be listed"
                        50
                        (Some "#000000")
                        "PendingPayment"
                        nowUtc
                        None
                        false

                do!
                    seedSayMessage
                        connectionString
                        pendingId
                        None
                        "Awaiting moderation"
                        "paid message"
                        73
                        None
                        "PaidPendingModeration"
                        (nowUtc.AddMinutes(1.0))
                        (Some(nowUtc.AddSeconds(30.0)))
                        false

                let! session = loginAdmin client
                let csrf = Some session.CsrfToken

                for uri in
                    [ "/api/v0/admin/say-messages"
                      "/api/v0/admin/say-messages?status=Pending"
                      "/api/v0/admin/say-messages?status=pending&status=approved"
                      "/api/v0/admin/say-messages?status=unknown" ] do
                    let! invalid = sendAdminRequest client csrf HttpMethod.Get uri
                    use invalid = invalid
                    Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), uri)
                    do! assertProblemCode "say.status.invalid" invalid

                let! response = sendAdminRequest client csrf HttpMethod.Get "/api/v0/admin/say-messages?status=pending"
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! document = jsonDocument response
                use document = document
                Assert.That(document.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array))
                Assert.That(document.RootElement.GetArrayLength(), Is.EqualTo(1), "PendingPayment is not eligible for moderation listing.")
                let message = document.RootElement.[0]
                Assert.That(message |> stringProperty "id", Is.EqualTo(pendingId.ToString("D")))
                Assert.That(message |> valueKindProperty "telegramUserId", Is.EqualTo(JsonValueKind.Null))
                Assert.That(message |> stringProperty "displayName", Is.EqualTo("Awaiting moderation"))
                Assert.That(message |> stringProperty "text", Is.EqualTo("paid message"))
                Assert.That(message |> intProperty "amountStars", Is.EqualTo(73))
                Assert.That(message |> valueKindProperty "color", Is.EqualTo(JsonValueKind.Null))
                Assert.That(message |> stringProperty "status", Is.EqualTo("pending"))
                Assert.That(message |> stringProperty "submittedAtUtc", Is.EqualTo("2026-07-10T18:01:00.0000000Z"))
                Assert.That(message |> stringProperty "paidAtUtc", Is.EqualTo("2026-07-10T18:00:30.0000000Z"))
                Assert.That(message |> valueKindProperty "moderatedAtUtc", Is.EqualTo(JsonValueKind.Null))
                Assert.That(message |> valueKindProperty "moderationReason", Is.EqualTo(JsonValueKind.Null))
            })

    [<Test>]
    let ``say approval and rejection reject malformed exact bodies before state transition`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 18, 30, 0, TimeSpan.Zero)

        withApiClient (fun connectionString client ->
            task {
                let messageId = Guid.NewGuid()

                do!
                    seedSayMessage
                        connectionString
                        messageId
                        (Some 7001L)
                        "Validation"
                        "body validation"
                        50
                        None
                        "PaidPendingModeration"
                        nowUtc
                        (Some nowUtc)
                        false

                let! session = loginAdmin client
                let csrf = Some session.CsrfToken
                let approveUri = sprintf "/api/v0/admin/say-messages/%O/approve" messageId
                let rejectUri = sprintf "/api/v0/admin/say-messages/%O/reject" messageId
                let oversized = "\"" + String.replicate 2049 "x" + "\""

                for uri, body in
                    [ approveUri, "[]"
                      approveUri, "{\"extra\":true}"
                      approveUri, oversized
                      rejectUri, "{}"
                      rejectUri, "{\"reason\":\"\"}"
                      rejectUri, "{\"reason\":\"valid\",\"extra\":true}"
                      rejectUri, sprintf "{\"reason\":\"%s\"}" (String.replicate 501 "x")
                      "/api/v0/admin/say-messages/not-a-guid/approve", "{}"
                      "/api/v0/admin/say-messages/00000000-0000-0000-0000-000000000000/reject", "{\"reason\":\"valid\"}" ] do
                    let! invalid = sendAdminJson client csrf uri body
                    use invalid = invalid
                    Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), uri)
                    do! assertProblemCode "say.request.invalid" invalid

                let! state, _, _, eventCount = readSayModerationState connectionString messageId
                Assert.That(state, Is.EqualTo("PaidPendingModeration"), "Invalid bodies must not moderate the message.")
                Assert.That(eventCount, Is.EqualTo(0L), "Invalid bodies must not append a moderation event.")
            })
    [<Test>]
    let ``say moderation is idempotent per target appends one exact audit event and only approval reaches player state`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 19, 0, 0, TimeSpan.Zero)

        withApiClientAt nowUtc (fun connectionString _ client ->
            task {
                let approvedId = Guid.NewGuid()
                let rejectedId = Guid.NewGuid()
                let deletedId = Guid.NewGuid()
                let! session = loginAdmin client
                let csrf = Some session.CsrfToken

                for messageId, displayName, text, isDeleted in
                    [ approvedId, "Approved sender", "visible after moderation", false
                      rejectedId, "Rejected sender", "hidden after moderation", false
                      deletedId, "Deleted sender", "deleted moderation", true ] do
                    do!
                        seedSayMessage
                            connectionString
                            messageId
                            (Some 7001L)
                            displayName
                            text
                            50
                            None
                            "PaidPendingModeration"
                            nowUtc
                            (Some nowUtc)
                            isDeleted

                let! _ = seedPaidSayPayment connectionString approvedId 50 nowUtc
                let! _ = seedPaidSayPayment connectionString rejectedId 50 nowUtc

                let! missing =
                    sendAdminJson
                        client
                        csrf
                        (sprintf "/api/v0/admin/say-messages/%O/approve" (Guid.NewGuid()))
                        "{}"

                use missing = missing
                Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
                do! assertProblemCode "say.not_found" missing

                let! deleted =
                    sendAdminJson
                        client
                        csrf
                        (sprintf "/api/v0/admin/say-messages/%O/reject" deletedId)
                        "{\"reason\":\"gone\"}"

                use deleted = deleted
                Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
                do! assertProblemCode "say.not_found" deleted

                let approveUri = sprintf "/api/v0/admin/say-messages/%O/approve" approvedId
                let! firstApproval = sendAdminJson client csrf approveUri "{}"
                use firstApproval = firstApproval
                Assert.That(firstApproval.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                let! repeatedApproval = sendAdminJson client csrf approveUri "{}"
                use repeatedApproval = repeatedApproval
                Assert.That(repeatedApproval.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "Same moderation target is a retry-safe transition.")
                let! oppositeApproval =
                    sendAdminJson client csrf (sprintf "/api/v0/admin/say-messages/%O/reject" approvedId) "{\"reason\":\"late\"}"

                use oppositeApproval = oppositeApproval
                Assert.That(oppositeApproval.StatusCode, Is.EqualTo(HttpStatusCode.Conflict))
                do! assertProblemCode "say.state_conflict" oppositeApproval

                let! approvedStatus, approvedReason, approvedPayment, approvedEvents = readSayModerationState connectionString approvedId
                Assert.That(approvedStatus, Is.EqualTo("Approved"))
                Assert.That(approvedReason |> Option.isNone, Is.True)
                Assert.That(approvedPayment, Is.EqualTo(Some "Paid"), "Moderation must never rewrite the settled payment.")
                Assert.That(approvedEvents, Is.EqualTo(1L), "Only the first transition appends SayMessageModerated.")

                use eventConnection = new NpgsqlConnection(connectionString)
                do! eventConnection.OpenAsync()

                use eventCommand =
                    new NpgsqlCommand(
                        """SELECT "Producer", "Payload" ->> 'sayMessageId', "Payload" ->> 'status', "Payload" -> 'moderationReason'
FROM "OutboxEvents"
WHERE "EventType" = 'SayMessageModerated' AND "Payload" ->> 'sayMessageId' = @Id;""",
                        eventConnection
                    )

                eventCommand.Parameters.AddWithValue("Id", approvedId.ToString("D")) |> ignore
                use! eventReader = eventCommand.ExecuteReaderAsync()
                let! hasEvent = eventReader.ReadAsync()
                Assert.That(hasEvent, Is.True)
                Assert.That(eventReader.GetString(0), Is.EqualTo("Web10.Radio.API.Admin"))
                Assert.That(eventReader.GetString(1), Is.EqualTo(approvedId.ToString("D")))
                Assert.That(eventReader.GetString(2), Is.EqualTo("Approved"))
                Assert.That(eventReader.GetFieldValue<JsonElement>(3).ValueKind, Is.EqualTo(JsonValueKind.Null))

                let rejectUri = sprintf "/api/v0/admin/say-messages/%O/reject" rejectedId
                let! firstRejection = sendAdminJson client csrf rejectUri "{\"reason\":\"  too loud  \"}"
                use firstRejection = firstRejection
                Assert.That(firstRejection.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                let! repeatedRejection = sendAdminJson client csrf rejectUri "{\"reason\":\"too loud\"}"
                use repeatedRejection = repeatedRejection
                Assert.That(repeatedRejection.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                let! oppositeRejection = sendAdminJson client csrf (sprintf "/api/v0/admin/say-messages/%O/approve" rejectedId) "{}"
                use oppositeRejection = oppositeRejection
                Assert.That(oppositeRejection.StatusCode, Is.EqualTo(HttpStatusCode.Conflict))
                do! assertProblemCode "say.state_conflict" oppositeRejection

                let! rejectedStatus, rejectedReason, rejectedPayment, rejectedEvents = readSayModerationState connectionString rejectedId
                Assert.That(rejectedStatus, Is.EqualTo("Rejected"))
                Assert.That(rejectedReason, Is.EqualTo(Some "too loud"))
                Assert.That(rejectedPayment, Is.EqualTo(Some "Paid"))
                Assert.That(rejectedEvents, Is.EqualTo(1L))

                let! playerResponse = client.GetAsync("/api/v0/player/state")
                use playerResponse = playerResponse
                Assert.That(playerResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! player = jsonDocument playerResponse
                use player = player
                let messages = player.RootElement |> jsonProperty "superChat" |> jsonProperty "messages"
                let visibleIds = messages.EnumerateArray() |> Seq.map (stringProperty "id") |> Set.ofSeq
                Assert.That(visibleIds.Contains(approvedId.ToString("D")), Is.True, "Approved paid message must be public immediately.")
                Assert.That(visibleIds.Contains(rejectedId.ToString("D")), Is.False, "Rejected paid message must stay off the public surface.")
            })

    [<Test>]
    let ``admin session exposes seeded social link and donation goal wire values`` () =
        withApiClient (fun connectionString client ->
            task {
                do! seedAdminReadModels connectionString
                let! session = loginAdmin client

                let! socialsResponse =
                    sendAdminRequest client (Some session.CsrfToken) HttpMethod.Get "/api/v0/admin/social-links"

                use socialsResponse = socialsResponse
                Assert.That(socialsResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! socials = jsonDocument socialsResponse
                use socials = socials
                Assert.That(socials.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array))
                Assert.That(socials.RootElement.GetArrayLength(), Is.EqualTo(1))
                let social = socials.RootElement.[0]
                Assert.That(social |> stringProperty "id", Is.EqualTo("01920000-0000-7000-8000-00000000a001"))
                Assert.That(social |> stringProperty "kind", Is.EqualTo("telegram"))
                Assert.That(social |> stringProperty "name", Is.EqualTo("Telegram"))
                Assert.That(social |> stringProperty "url", Is.EqualTo("https://t.me/web10radio"))
                Assert.That(social |> boolProperty "isFeatured", Is.True)

                let! goalResponse =
                    sendAdminRequest client (Some session.CsrfToken) HttpMethod.Get "/api/v0/admin/donation-goal"

                use goalResponse = goalResponse
                Assert.That(goalResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! goal = jsonDocument goalResponse
                use goal = goal
                Assert.That(goal.RootElement |> stringProperty "title", Is.EqualTo("Keep Web10.Radio live"))
                Assert.That(goal.RootElement |> intProperty "raisedStars", Is.EqualTo(250))
                Assert.That(goal.RootElement |> intProperty "goalStars", Is.EqualTo(1000))
            })

    [<Test>]
    let ``webhook rejects absent wrong and multi-value secret headers without durable effects`` () =
        withApiClient (fun connectionString client ->
            task {
                let body = typedRequestUpdate 9101L "secret validation"

                for secrets in [ []; [ "wrong-secret" ]; [ WebhookSecret; WebhookSecret ] ] do
                    let! response = sendWebhook client body secrets
                    use response = response
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
                    do! assertProblemCode "telegram.webhook.secret_invalid" response
                    do! assertNoWebhookEffects connectionString
            })

    [<Test>]
    let ``accepted typed request webhook durably emits one domain event before duplicate acknowledgement`` () =
        withApiClient (fun connectionString client ->
            task {
                let updateId = 9201L
                let body = typedRequestUpdate updateId "vaporwave"
                let! firstResponse = sendWebhook client body [ WebhookSecret ]
                use firstResponse = firstResponse
                Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                let! inboxCountAfterFirst = countInboxRows connectionString updateId "TrackRequested"
                let! outboxCountAfterFirst = countOutboxRows connectionString "TrackRequested"
                let! payloadText = outboxPayloadText connectionString "TrackRequested"
                Assert.That(inboxCountAfterFirst, Is.EqualTo(1), "204 is not valid until the typed update is durably ingested.")
                Assert.That(outboxCountAfterFirst, Is.EqualTo(1), "204 is not valid until the derived domain event is durably appended.")
                Assert.That(payloadText, Is.EqualTo("vaporwave"), "The durable event must preserve the parsed Telegram command query.")

                let! duplicateResponse = sendWebhook client body [ WebhookSecret ]
                use duplicateResponse = duplicateResponse
                Assert.That(duplicateResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                let! inboxCountAfterDuplicate = countInboxRows connectionString updateId "TrackRequested"
                let! outboxCountAfterDuplicate = countOutboxRows connectionString "TrackRequested"
                Assert.That(inboxCountAfterDuplicate, Is.EqualTo(1))
                Assert.That(outboxCountAfterDuplicate, Is.EqualTo(1))
            })

    [<Test>]
    let ``concurrent delivery of one typed update leaves one durable domain event`` () =
        withApiClient (fun connectionString client ->
            task {
                let updateId = 9202L
                let body = typedRequestUpdate updateId "concurrent request"
                let! responses = Task.WhenAll([| sendWebhook client body [ WebhookSecret ]; sendWebhook client body [ WebhookSecret ] |])

                try
                    for response in responses do
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                finally
                    for response in responses do
                        response.Dispose()

                let! inboxCount = countInboxRows connectionString updateId "TrackRequested"
                let! outboxCount = countOutboxRows connectionString "TrackRequested"
                Assert.That(inboxCount, Is.EqualTo(1))
                Assert.That(outboxCount, Is.EqualTo(1))
            })

    [<Test>]
    let ``malformed webhook JSON is rejected before typed ingestion`` () =
        withApiClient (fun connectionString client ->
            task {
                let! response = sendWebhook client "{not-json" [ WebhookSecret ]
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                do! assertProblemCode "request.invalid" response
                do! assertNoWebhookEffects connectionString
            })

    [<Test>]
    let ``a heartbeat exactly thirty seconds old remains live and serves range requests`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero)

        withApiClientAt nowUtc (fun connectionString tempRoot client ->
            task {
                do! seedHeartbeat connectionString "Live" (nowUtc.AddSeconds(-30.0))
                let! bytes = seedCachedPlayingTrack connectionString tempRoot nowUtc

                let! stateResponse = client.GetAsync("/api/v0/player/state")
                use stateResponse = stateResponse
                Assert.That(stateResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! state = jsonDocument stateResponse
                use state = state
                Assert.That(state.RootElement |> jsonProperty "stream" |> stringProperty "status", Is.EqualTo("live"))

                let! healthResponse = client.GetAsync("/api/v0/player/health")
                use healthResponse = healthResponse
                Assert.That(healthResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! health = jsonDocument healthResponse
                use health = health
                Assert.That(health.RootElement |> stringProperty "status", Is.EqualTo("live"))

                use streamRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v0/player/stream")
                streamRequest.Headers.TryAddWithoutValidation("Range", "bytes=2-5") |> ignore
                let! streamResponse = client.SendAsync(streamRequest)
                use streamResponse = streamResponse
                Assert.That(streamResponse.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent))
                Assert.That(streamResponse.Content.Headers.ContentRange.ToString(), Is.EqualTo("bytes 2-5/10"))
                let! rangedBytes = streamResponse.Content.ReadAsByteArrayAsync()
                Assert.That(rangedBytes, Is.EqualTo(bytes.[2..5] :> obj))
            })

    [<Test>]
    let ``a heartbeat older than thirty seconds is offline in state and health and gates stream`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero)

        withApiClientAt nowUtc (fun connectionString tempRoot client ->
            task {
                do! seedHeartbeat connectionString "Live" (nowUtc.AddSeconds(-30.001))
                let! _ = seedCachedPlayingTrack connectionString tempRoot nowUtc

                let! stateResponse = client.GetAsync("/api/v0/player/state")
                use stateResponse = stateResponse
                Assert.That(stateResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! state = jsonDocument stateResponse
                use state = state
                Assert.That(state.RootElement |> jsonProperty "stream" |> stringProperty "status", Is.EqualTo("offline"))
                Assert.That(
                    state.RootElement |> jsonProperty "stream" |> stringProperty "offlineReason",
                    Is.EqualTo("stream-node heartbeat stale")
                )

                let! healthResponse = client.GetAsync("/api/v0/player/health")
                use healthResponse = healthResponse
                Assert.That(healthResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! health = jsonDocument healthResponse
                use health = health
                Assert.That(health.RootElement |> stringProperty "status", Is.EqualTo("offline"))
                Assert.That(health.RootElement |> stringProperty "offlineReason", Is.EqualTo("stream-node heartbeat stale"))

                let! streamResponse = client.GetAsync("/api/v0/player/stream")
                use streamResponse = streamResponse
                Assert.That(streamResponse.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable))
                do! assertProblemCode "stream.unavailable" streamResponse
            })

    [<Test>]
    let ``webhook accepts exactly one mebibyte typed update and rejects the next byte before durable ingestion`` () =
        withApiClient (fun connectionString client ->
            task {
                let maxBytes = 1024 * 1024
                let acceptedUpdateId = 9301L
                let acceptedBody = paddedTypedRequestUpdate acceptedUpdateId maxBytes
                Assert.That(Encoding.UTF8.GetByteCount(acceptedBody), Is.EqualTo(maxBytes))

                let! accepted = sendWebhook client acceptedBody [ WebhookSecret ]
                use accepted = accepted
                Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                let! acceptedInbox = countInboxRows connectionString acceptedUpdateId "TrackRequested"
                let! acceptedOutbox = countOutboxRows connectionString "TrackRequested"
                Assert.That(acceptedInbox, Is.EqualTo(1))
                Assert.That(acceptedOutbox, Is.EqualTo(1))

                let oversizedUpdateId = 9302L
                let oversizedBody = paddedTypedRequestUpdate oversizedUpdateId (maxBytes + 1)
                Assert.That(Encoding.UTF8.GetByteCount(oversizedBody), Is.EqualTo(maxBytes + 1))
                let! oversized = sendWebhook client oversizedBody [ WebhookSecret ]
                use oversized = oversized
                Assert.That(oversized.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge))
                do! assertProblemCode "request.too_large" oversized
                let! oversizedInbox = countInboxRows connectionString oversizedUpdateId "TrackRequested"
                let! outboxAfterOversized = countOutboxRows connectionString "TrackRequested"
                Assert.That(oversizedInbox, Is.EqualTo(0))
                Assert.That(outboxAfterOversized, Is.EqualTo(1))
            })

    [<Test>]
    let ``telegram adapter exposes parse failure then clears it on a newer accepted update without regressing last update id`` () =
        withApiClient (fun _ client ->
            task {
                let firstUpdateId = 9402L
                let! first = sendWebhook client (typedRequestUpdate firstUpdateId "adapter state") [ WebhookSecret ]
                use first = first
                Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                let! malformed = sendWebhook client "{not-json" [ WebhookSecret ]
                use malformed = malformed
                Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                do! assertProblemCode "request.invalid" malformed

                let! failedHealthResponse = client.GetAsync("/api/v0/telegram/health")
                use failedHealthResponse = failedHealthResponse
                Assert.That(failedHealthResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! failedHealth = jsonDocument failedHealthResponse
                use failedHealth = failedHealth
                let failedLastUpdateId = failedHealth.RootElement |> jsonProperty "lastUpdateId" |> fun value -> value.GetInt64()
                Assert.That(failedLastUpdateId, Is.EqualTo(firstUpdateId))
                Assert.That(failedHealth.RootElement |> stringProperty "lastError", Is.EqualTo("request.invalid"))

                let latestUpdateId = 9403L
                let! latest = sendWebhook client (typedRequestUpdate latestUpdateId "clear adapter error") [ WebhookSecret ]
                use latest = latest
                Assert.That(latest.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                let! older = sendWebhook client (typedRequestUpdate 9401L "older update") [ WebhookSecret ]
                use older = older
                Assert.That(older.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                let! recoveredHealthResponse = client.GetAsync("/api/v0/telegram/health")
                use recoveredHealthResponse = recoveredHealthResponse
                let! recoveredHealth = jsonDocument recoveredHealthResponse
                use recoveredHealth = recoveredHealth
                let recoveredLastUpdateId = recoveredHealth.RootElement |> jsonProperty "lastUpdateId" |> fun value -> value.GetInt64()
                Assert.That(recoveredLastUpdateId, Is.EqualTo(latestUpdateId))
                Assert.That(recoveredHealth.RootElement |> valueKindProperty "lastError", Is.EqualTo(JsonValueKind.Null))
            })

    [<Test>]
    let ``player events publish every required name with its exact state fragment after a deterministic cycle signal`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero)
        let delay = ControlledPlayerEventsDelay()

        withApiClientAndEventsDelay nowUtc delay (fun connectionString client ->
            task {
                do! seedAdminReadModels connectionString
                do! seedSseFragments connectionString nowUtc
                do! seedHeartbeat connectionString "Live" nowUtc

                use cancellation = new CancellationTokenSource()
                let! response =
                    client.GetAsync("/api/v0/player/events", HttpCompletionOption.ResponseHeadersRead, cancellation.Token)

                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("text/event-stream"))
                let! stream = response.Content.ReadAsStreamAsync(cancellation.Token)
                use stream = stream
                use reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true)

                let! initial = readSseEvent reader cancellation.Token
                Assert.That(initial.Name, Is.EqualTo("player.state"))
                use initialDocument = JsonDocument.Parse(initial.Data)
                let initialQueue = initialDocument.RootElement |> jsonProperty "queue"
                let initialItems = initialQueue |> jsonProperty "items"
                Assert.That(initialItems.GetArrayLength(), Is.EqualTo(1))
                Assert.That(initialItems.[0] |> stringProperty "title", Is.EqualTo("SSE queue track"))
                let initialMessages = initialDocument.RootElement |> jsonProperty "superChat" |> jsonProperty "messages"
                Assert.That(initialMessages.[0] |> stringProperty "text", Is.EqualTo("SSE moderation payload"))
                Assert.That(initialDocument.RootElement |> jsonProperty "donationGoal" |> stringProperty "title", Is.EqualTo("Keep Web10.Radio live"))

                delay.Advance()
                let! queue = readSseEvent reader cancellation.Token
                Assert.That(queue.Name, Is.EqualTo("player.queue"))
                use queueDocument = JsonDocument.Parse(queue.Data)
                let queueItems = queueDocument.RootElement |> jsonProperty "items"
                Assert.That(queueItems.[0] |> stringProperty "title", Is.EqualTo("SSE queue track"))

                let! say = readSseEvent reader cancellation.Token
                Assert.That(say.Name, Is.EqualTo("player.say"))
                use sayDocument = JsonDocument.Parse(say.Data)
                let sayMessages = sayDocument.RootElement |> jsonProperty "messages"
                Assert.That(sayMessages.[0] |> stringProperty "text", Is.EqualTo("SSE moderation payload"))

                let! donation = readSseEvent reader cancellation.Token
                Assert.That(donation.Name, Is.EqualTo("player.donation"))
                use donationDocument = JsonDocument.Parse(donation.Data)
                Assert.That(donationDocument.RootElement |> stringProperty "title", Is.EqualTo("Keep Web10.Radio live"))
                Assert.That(donationDocument.RootElement |> intProperty "raisedStars", Is.EqualTo(250))
                Assert.That(donationDocument.RootElement |> intProperty "goalStars", Is.EqualTo(1000))

                let! health = readSseEvent reader cancellation.Token
                Assert.That(health.Name, Is.EqualTo("player.health"))
                use healthDocument = JsonDocument.Parse(health.Data)
                Assert.That(healthDocument.RootElement |> stringProperty "status", Is.EqualTo("live"))
                Assert.That(healthDocument.RootElement |> intProperty "bitrateKbps", Is.EqualTo(192))

                let! refreshed = readSseEvent reader cancellation.Token
                Assert.That(refreshed.Name, Is.EqualTo("player.state"))
                use refreshedDocument = JsonDocument.Parse(refreshed.Data)
                Assert.That((refreshedDocument.RootElement |> jsonProperty "nowPlaying").ValueKind, Is.EqualTo(JsonValueKind.Object))
                let refreshedItems = refreshedDocument.RootElement |> jsonProperty "queue" |> jsonProperty "items"
                Assert.That(refreshedItems.[0] |> stringProperty "title", Is.EqualTo("SSE queue track"))
            })

    [<Test>]
    let ``route failure before response start writes problem and captures the exception with request identity`` () =
        task {
            let entries = ConcurrentQueue<CapturedLog>()
            let logger = CapturingLogger(entries) :> ILogger
            let context = DefaultHttpContext()
            context.TraceIdentifier <- "trace-before-response"
            context.Request.Headers["X-Correlation-Id"] <- "correlation-before-response"
            context.Response.Body <- new MemoryStream()

            let handler: Web10.Radio.API.ApiRouteHandler =
                fun _ -> Task.FromException<int>(InvalidOperationException("before response failure"))

            do! Web10.Radio.API.ApiEndpoints.execute logger "/test/before-response" handler context
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status500InternalServerError))
            let body = (context.Response.Body :?> MemoryStream).ToArray() |> Encoding.UTF8.GetString
            use document = JsonDocument.Parse(body)
            Assert.That(document.RootElement |> stringProperty "code", Is.EqualTo("api.unhandled"))

            let failure =
                entries
                |> Seq.tryFind (fun entry ->
                    entry.Level = LogLevel.Error
                    && (entry.Exception
                        |> Option.exists (fun error -> error :? InvalidOperationException && error.Message = "before response failure")))

            match failure with
            | None -> Assert.Fail("Expected ApiRouteFailed to capture the original exception.")
            | Some entry ->
                Assert.That(entry.Message.Contains("/test/before-response"), Is.True)
                Assert.That(entry.Message.Contains("500"), Is.True)
                Assert.That(entry.Message.Contains("trace-before-response"), Is.True)
                Assert.That(entry.Message.Contains("correlation-before-response"), Is.True)
        }

    [<Test>]
    let ``route failure after response start preserves wire response and captures original exception with wire status`` () =
        task {
            let entries = ConcurrentQueue<CapturedLog>()
            let logger = CapturingLogger(entries) :> ILogger
            let context = DefaultHttpContext()
            context.Response.Body <- new MemoryStream()
            let responseBody = context.Response.Body :?> MemoryStream
            let feature = StartedResponseFeature(418)
            context.Features.Set<IHttpResponseFeature>(feature)
            context.TraceIdentifier <- "trace-after-response"
            context.Request.Headers["X-Correlation-Id"] <- "correlation-after-response"
            let alreadyWritten = Encoding.UTF8.GetBytes("already-written")
            do! responseBody.WriteAsync(alreadyWritten)

            let handler: Web10.Radio.API.ApiRouteHandler =
                fun _ -> Task.FromException<int>(InvalidOperationException("after response failure"))

            do! Web10.Radio.API.ApiEndpoints.execute logger "/test/after-response" handler context
            Assert.That(context.Response.StatusCode, Is.EqualTo(418), "A started response cannot be replaced with a problem response.")
            Assert.That(responseBody.ToArray(), Is.EqualTo(alreadyWritten :> obj))

            let failure =
                entries
                |> Seq.tryFind (fun entry ->
                    entry.Level = LogLevel.Error
                    && (entry.Exception
                        |> Option.exists (fun error -> error :? InvalidOperationException && error.Message = "after response failure")))

            match failure with
            | None -> Assert.Fail("Expected ApiRouteFailed to capture the post-start exception.")
            | Some entry ->
                Assert.That(entry.Message.Contains("/test/after-response"), Is.True)
                Assert.That(entry.Message.Contains("418"), Is.True)
                Assert.That(entry.Message.Contains("trace-after-response"), Is.True)
                Assert.That(entry.Message.Contains("correlation-after-response"), Is.True)
        }

    [<Test>]
    let ``stream-node callbacks enforce bearer body and fence contracts then persist both terminal outcomes`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 22, 0, 0, TimeSpan.Zero)
        let callbackToken = "stream-callback-token-Secret_123456"

        withApiClientWithServices
            (fun services ->
                services.RemoveAll<IHostedService>() |> ignore
                services.RemoveAll<Web10.Radio.API.IClock>() |> ignore
                services.AddSingleton<Web10.Radio.API.IClock>(FixedClock(nowUtc) :> Web10.Radio.API.IClock) |> ignore)
            (fun connectionString _ _ client ->
                task {
                    let playedItemId, playedOwner = Guid.NewGuid(), Guid.NewGuid()
                    let failedItemId, failedOwner = Guid.NewGuid(), Guid.NewGuid()

                    use connection = new NpgsqlConnection(connectionString)
                    do! connection.OpenAsync()

                    let seedPlaying queueItemId owner =
                        task {
                            use command =
                                new NpgsqlCommand(
                                    """INSERT INTO "PlaybackQueue" ("Id", "Source", "Status", "Priority", "RequestedAtUtc", "ClaimedAtUtc", "StartedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc", "IsDeleted")
VALUES (@Id, 'admin', 'Playing', 0, @NowUtc, @NowUtc, @NowUtc, @Owner, 1, @LeaseExpiresAtUtc, false);""",
                                    connection
                                )

                            command.Parameters.AddWithValue("Id", queueItemId) |> ignore
                            command.Parameters.AddWithValue("Owner", owner) |> ignore
                            command.Parameters.AddWithValue("NowUtc", nowUtc) |> ignore
                            command.Parameters.AddWithValue("LeaseExpiresAtUtc", nowUtc.AddMinutes(1.0)) |> ignore
                            let! _ = command.ExecuteNonQueryAsync()
                            return ()
                        }

                    do! seedPlaying playedItemId playedOwner
                    do! seedPlaying failedItemId failedOwner

                    let playedPath = sprintf "/api/v0/stream-node/playback/%O" playedItemId
                    let leaseBody = sprintf "{\"claimOwner\":\"%O\",\"claimAttempt\":1}" playedOwner
                    let completionBody = sprintf "{\"claimOwner\":\"%O\",\"claimAttempt\":1,\"status\":\"played\"}" playedOwner
                    let endpoints = [ "/lease", leaseBody; "/completion", completionBody ]

                    let rejectedCredentials =
                        [ "missing", []
                          "wrong", [ "Bearer wrong-stream-node-token-Secret_123456" ]
                          "multiple", [ sprintf "Bearer %s" callbackToken; "Bearer another-stream-node-token-Secret_123456" ] ]

                    for suffix, body in endpoints do
                        for credentialName, authorizations in rejectedCredentials do
                            let! rejected = sendPlaybackCallback client (playedPath + suffix) body authorizations
                            use rejected = rejected

                            Assert.That(
                                rejected.StatusCode,
                                Is.EqualTo(HttpStatusCode.Unauthorized),
                                sprintf "%s callback with %s credentials" suffix credentialName
                            )

                            Assert.That(
                                rejected.Headers.GetValues("WWW-Authenticate") |> String.concat ",",
                                Is.EqualTo("Bearer"),
                                sprintf "%s callback must issue the Bearer challenge for %s credentials" suffix credentialName
                            )

                            do! assertProblemCode "stream-node.auth.required" rejected

                        let! malformed = sendPlaybackCallback client (playedPath + suffix) "{" [ sprintf "Bearer %s" callbackToken ]
                        use malformed = malformed
                        Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), sprintf "%s rejects malformed JSON" suffix)
                        do! assertProblemCode "request.invalid" malformed

                        let! oversized =
                            sendPlaybackCallback
                                client
                                (playedPath + suffix)
                                (String.replicate 4097 "x")
                                [ sprintf "Bearer %s" callbackToken ]

                        use oversized = oversized
                        Assert.That(oversized.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge), sprintf "%s rejects an oversized body" suffix)
                        do! assertProblemCode "request.too_large" oversized

                    let! renewed = sendPlaybackCallback client (playedPath + "/lease") leaseBody [ sprintf "Bearer %s" callbackToken ]
                    use renewed = renewed
                    Assert.That(renewed.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "The active playing claim renews its lease.")

                    let! staleOwner =
                        sendPlaybackCallback
                            client
                            (playedPath + "/lease")
                            (sprintf "{\"claimOwner\":\"%O\",\"claimAttempt\":1}" (Guid.NewGuid()))
                            [ sprintf "Bearer %s" callbackToken ]

                    use staleOwner = staleOwner
                    Assert.That(staleOwner.StatusCode, Is.EqualTo(HttpStatusCode.Conflict))
                    do! assertProblemCode "playback.claim_stale" staleOwner

                    let! staleAttempt =
                        sendPlaybackCallback
                            client
                            (playedPath + "/completion")
                            (sprintf "{\"claimOwner\":\"%O\",\"claimAttempt\":2,\"status\":\"played\"}" playedOwner)
                            [ sprintf "Bearer %s" callbackToken ]

                    use staleAttempt = staleAttempt
                    Assert.That(staleAttempt.StatusCode, Is.EqualTo(HttpStatusCode.Conflict))
                    do! assertProblemCode "playback.claim_stale" staleAttempt

                    let! played =
                        sendPlaybackCallback client (playedPath + "/completion") completionBody [ sprintf "Bearer %s" callbackToken ]

                    use played = played
                    Assert.That(played.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                    let failedPath = sprintf "/api/v0/stream-node/playback/%O/completion" failedItemId

                    let! failed =
                        sendPlaybackCallback
                            client
                            failedPath
                            (sprintf "{\"claimOwner\":\"%O\",\"claimAttempt\":1,\"status\":\"failed\",\"failureReason\":\"decoder exited\"}" failedOwner)
                            [ sprintf "Bearer %s" callbackToken ]

                    use failed = failed
                    Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                    use check =
                        new NpgsqlCommand(
                            """SELECT
    (SELECT "Status" FROM "PlaybackQueue" WHERE "Id" = @PlayedItemId),
    (SELECT "FinishedAtUtc" IS NOT NULL FROM "PlaybackQueue" WHERE "Id" = @PlayedItemId),
    (SELECT "ClaimOwner" IS NULL FROM "PlaybackQueue" WHERE "Id" = @PlayedItemId),
    (SELECT "Status" FROM "PlaybackQueue" WHERE "Id" = @FailedItemId),
    (SELECT "FailureReason" FROM "PlaybackQueue" WHERE "Id" = @FailedItemId),
    (SELECT "FinishedAtUtc" IS NOT NULL FROM "PlaybackQueue" WHERE "Id" = @FailedItemId),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'PlaybackEnded' AND "Status" = 'Pending' AND "Payload" ->> 'queueItemId' = @PlayedItemIdText AND "Payload" ->> 'status' = 'played'),
    (SELECT count(*) FROM "OutboxEvents" WHERE "EventType" = 'PlaybackEnded' AND "Status" = 'Pending' AND "Payload" ->> 'queueItemId' = @FailedItemIdText AND "Payload" ->> 'status' = 'failed' AND "Payload" ->> 'failureReason' = 'decoder exited');""",
                            connection
                        )

                    check.Parameters.AddWithValue("PlayedItemId", playedItemId) |> ignore
                    check.Parameters.AddWithValue("FailedItemId", failedItemId) |> ignore
                    check.Parameters.AddWithValue("PlayedItemIdText", string playedItemId) |> ignore
                    check.Parameters.AddWithValue("FailedItemIdText", string failedItemId) |> ignore
                    let! reader = check.ExecuteReaderAsync()
                    use reader = reader
                    let! hasRow = reader.ReadAsync()
                    Assert.That(hasRow, Is.True)
                    Assert.That(reader.GetString(0), Is.EqualTo("Played"))
                    Assert.That(reader.GetBoolean(1), Is.True)
                    Assert.That(reader.GetBoolean(2), Is.True)
                    Assert.That(reader.GetString(3), Is.EqualTo("Failed"))
                    Assert.That(reader.GetString(4), Is.EqualTo("decoder exited"))
                    Assert.That(reader.GetBoolean(5), Is.True)
                    Assert.That(reader.GetInt64(6), Is.EqualTo(1L), "Successful completion appends one pending PlaybackEnded event.")
                    Assert.That(reader.GetInt64(7), Is.EqualTo(1L), "Failed completion appends one pending PlaybackEnded event.")
                })

    [<Test>]
    let ``duplicate request and say webhooks relay exactly one domain row each`` () =
        withApiClientWithServices
            (fun services ->
                services.RemoveAll<IHostedService>() |> ignore
                services.AddSingleton<Web10.Radio.API.OutboxRelayHostedService>() |> ignore)
            (fun connectionString _ factory client ->
                task {
                    let relay = factory.Services.GetRequiredService<Web10.Radio.API.OutboxRelayHostedService>()

                    let processOne eventName =
                        task {
                            let! result = relay.ProcessDueEventsOnceAsync(CancellationToken.None)

                            match result with
                            | Ok 1 -> return ()
                            | Ok count -> return raise (AssertionException(sprintf "Expected one relayed %s event, got %d." eventName count))
                            | Error error -> return raise (AssertionException(sprintf "Relaying %s failed: %O" eventName error))
                        }

                    let requestUpdateId = 9501L
                    let requestQuery = "one exact request row"
                    let requestBody = typedRequestUpdate requestUpdateId requestQuery
                    let! firstRequest = sendWebhook client requestBody [ WebhookSecret ]
                    use firstRequest = firstRequest
                    Assert.That(firstRequest.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                    let! duplicateRequest = sendWebhook client requestBody [ WebhookSecret ]
                    use duplicateRequest = duplicateRequest
                    Assert.That(duplicateRequest.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                    do! processOne "TrackRequested"
                    let! requestRows = countTrackRequestRows connectionString requestQuery
                    let! requestOutboxRows = countOutboxRows connectionString "TrackRequested"
                    Assert.That(requestRows, Is.EqualTo(1), "Duplicate /request delivery must create one TrackRequests row after relay.")
                    Assert.That(requestOutboxRows, Is.EqualTo(1), "Duplicate /request delivery must relay one TrackRequested event.")

                    let sayUpdateId = 9502L
                    let sayText = "one exact say row"
                    let sayBody = typedSayUpdate sayUpdateId sayText
                    let! firstSay = sendWebhook client sayBody [ WebhookSecret ]
                    use firstSay = firstSay
                    Assert.That(firstSay.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                    let! duplicateSay = sendWebhook client sayBody [ WebhookSecret ]
                    use duplicateSay = duplicateSay
                    Assert.That(duplicateSay.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                    do! processOne "SayMessageSubmitted"
                    let! sayRows = countSayMessageRows connectionString sayText
                    let! sayOutboxRows = countOutboxRows connectionString "SayMessageSubmitted"
                    Assert.That(sayRows, Is.EqualTo(1), "Duplicate /say delivery must create one SayMessages row after relay.")
                    Assert.That(sayOutboxRows, Is.EqualTo(1), "Duplicate /say delivery must relay one SayMessageSubmitted event.")
                })

    [<Test>]
    let ``duplicate successful payment webhook relays once and persists one paid charge`` () =
        withApiClientWithServices
            (fun services ->
                services.RemoveAll<IHostedService>() |> ignore
                services.AddSingleton<Web10.Radio.API.OutboxRelayHostedService>() |> ignore)
            (fun connectionString _ factory client ->
                task {
                    let relay = factory.Services.GetRequiredService<Web10.Radio.API.OutboxRelayHostedService>()
                    let paymentId = Guid.NewGuid()
                    let chargeId = "telegram-charge-idempotent-9503"
                    do! seedDonationPayment connectionString paymentId 42

                    let paymentBody = typedSuccessfulPaymentUpdate 9503L paymentId chargeId 42
                    let! firstPayment = sendWebhook client paymentBody [ WebhookSecret ]
                    use firstPayment = firstPayment
                    Assert.That(firstPayment.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                    let! duplicatePayment = sendWebhook client paymentBody [ WebhookSecret ]
                    use duplicatePayment = duplicatePayment
                    Assert.That(duplicatePayment.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                    let! firstRelay = relay.ProcessDueEventsOnceAsync(CancellationToken.None)

                    match firstRelay with
                    | Ok 1 -> ()
                    | Ok count -> Assert.Fail(sprintf "Expected one relayed DonationPaid event, got %d." count)
                    | Error error -> Assert.Fail(sprintf "Relaying DonationPaid failed: %O" error)

                    let! secondRelay = relay.ProcessDueEventsOnceAsync(CancellationToken.None)

                    match secondRelay with
                    | Ok 0 -> ()
                    | Ok count -> Assert.Fail(sprintf "Duplicate successful_payment created %d additional relay work items." count)
                    | Error error -> Assert.Fail(sprintf "Relaying duplicate DonationPaid failed: %O" error)

                    use check =
                        new NpgsqlCommand(
                            """SELECT "Status", "TelegramPaymentChargeId", "AmountStars", "Currency", "PaidAtUtc" IS NOT NULL,
    (SELECT count(*) FROM "Payments" WHERE "Id" = @PaymentId AND "IsDeleted" = false)
FROM "Payments"
WHERE "Id" = @PaymentId AND "IsDeleted" = false;""",
                            new NpgsqlConnection(connectionString)
                        )

                    do! check.Connection.OpenAsync()
                    check.Parameters.AddWithValue("PaymentId", paymentId) |> ignore
                    let! reader = check.ExecuteReaderAsync()
                    use reader = reader
                    let! hasRow = reader.ReadAsync()
                    Assert.That(hasRow, Is.True)
                    Assert.That(reader.GetString(0), Is.EqualTo("Paid"))
                    Assert.That(reader.GetString(1), Is.EqualTo(chargeId))
                    Assert.That(reader.GetInt32(2), Is.EqualTo(42))
                    Assert.That(reader.GetString(3), Is.EqualTo("XTR"))
                    Assert.That(reader.GetBoolean(4), Is.True)
                    Assert.That(reader.GetInt64(5), Is.EqualTo(1L), "Duplicate successful_payment must preserve a single Payment row.")

                    let! paymentOutboxRows = countOutboxRows connectionString "DonationPaid"
                    Assert.That(paymentOutboxRows, Is.EqualTo(1), "Duplicate successful_payment must append one DonationPaid event.")
                })

    [<Test>]
    let ``live cached metadata whose file disappears returns stream unavailable instead of an internal error`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 23, 0, 0, TimeSpan.Zero)

        withApiClientAt nowUtc (fun connectionString tempRoot client ->
            task {
                do! seedHeartbeat connectionString "Live" nowUtc
                let! _ = seedCachedPlayingTrack connectionString tempRoot nowUtc
                File.Delete(Path.Combine(tempRoot, "live-track.mp3"))

                let! response = client.GetAsync("/api/v0/player/stream")
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable))
                do! assertProblemCode "stream.unavailable" response
            })

    [<Test>]
    let ``health live stays reachable and ready reports every operational check healthy`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 23, 30, 0, TimeSpan.Zero)

        withApiClientWithServices
            (fun services ->
                services.RemoveAll<Web10.Radio.API.ITelegramIdentityProbe>() |> ignore
                services.AddSingleton<Web10.Radio.API.ITelegramIdentityProbe>(AuthenticatedTelegramIdentityProbe()) |> ignore
                services.RemoveAll<Web10.Radio.API.IClock>() |> ignore
                services.AddSingleton<Web10.Radio.API.IClock>(FixedClock(nowUtc) :> Web10.Radio.API.IClock) |> ignore)
            (fun connectionString tempRoot _ client ->
                task {
                    Directory.CreateDirectory(Path.Combine(tempRoot, "library")) |> ignore
                    do! seedHeartbeat connectionString "Live" nowUtc

                    let! liveResponse = client.GetAsync("/health/live")
                    use liveResponse = liveResponse
                    Assert.That(liveResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                    let! readyResponse = client.GetAsync("/health/ready")
                    use readyResponse = readyResponse
                    Assert.That(readyResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    let! ready = jsonDocument readyResponse
                    use ready = ready
                    Assert.That(ready.RootElement |> stringProperty "status", Is.EqualTo("Healthy"))

                    let checks = ready.RootElement |> jsonProperty "checks"

                    let expectedChecks =
                        [ "api", "Healthy"
                          "telegram-adapter", "Healthy"
                          "postgresql", "Healthy"
                          "storage", "Healthy"
                          "stream-node-heartbeat", "Healthy" ]

                    Assert.That(checks.GetArrayLength(), Is.EqualTo(expectedChecks.Length))

                    for name, expectedStatus in expectedChecks do
                        let check =
                            checks.EnumerateArray()
                            |> Seq.tryFind (fun item -> item |> stringProperty "name" = name)

                        match check with
                        | Some item -> Assert.That(item |> stringProperty "status", Is.EqualTo(expectedStatus), name)
                        | None -> Assert.Fail(sprintf "Readiness response omitted required %s check." name)
                })

    [<Test>]
    let ``pre-checkout webhook maps the verified Telegram actor to replacement workflow and acknowledges success`` () =
        let workflow = RecordingPreCheckoutWorkflow(Ok())

        withApiClientWithServices
            (fun services ->
                services.RemoveAll<Web10.Radio.API.ITelegramPreCheckoutWorkflow>() |> ignore
                services.AddSingleton<Web10.Radio.API.ITelegramPreCheckoutWorkflow>(workflow) |> ignore)
            (fun _ _ _ client ->
                task {
                    let! response =
                        sendWebhook
                            client
                            (typedPreCheckoutUpdate 9701L 808L "ru-RU" 73 "00000000-0000-0000-0000-000000000701")
                            [ WebhookSecret ]

                    use response = response
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                    Assert.That(workflow.Inputs.Length, Is.EqualTo(1), "The protocol branch must invoke the replacement workflow exactly once.")
                    let input = workflow.Inputs.[0]
                    Assert.That(input.TelegramUpdateId, Is.EqualTo(9701L))
                    Assert.That(input.QueryId, Is.EqualTo("precheckout-9701"))
                    Assert.That(input.TelegramUserId, Is.EqualTo(808L))
                    Assert.That(input.LanguageCode, Is.EqualTo(Some "ru-RU"))
                    Assert.That(input.Currency, Is.EqualTo("XTR"))
                    Assert.That(input.TotalAmount, Is.EqualTo(73))
                    Assert.That(input.InvoicePayload, Is.EqualTo("00000000-0000-0000-0000-000000000701"))
                })

    [<Test>]
    let ``pre-checkout workflow failure maps to retryable webhook unavailable problem`` () =
        let workflow =
            RecordingPreCheckoutWorkflow(
                Error(Web10.Radio.API.TelegramTransportError("answerPreCheckoutQuery", "timeout"))
            )

        withApiClientWithServices
            (fun services ->
                services.RemoveAll<Web10.Radio.API.ITelegramPreCheckoutWorkflow>() |> ignore
                services.AddSingleton<Web10.Radio.API.ITelegramPreCheckoutWorkflow>(workflow) |> ignore)
            (fun _ _ _ client ->
                task {
                    let! response =
                        sendWebhook
                            client
                            (typedPreCheckoutUpdate 9702L 809L "en-US" 50 "00000000-0000-0000-0000-000000000702")
                            [ WebhookSecret ]

                    use response = response
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable))
                    do! assertProblemCode "telegram.pre_checkout_unavailable" response
                    Assert.That(workflow.Inputs.Length, Is.EqualTo(1))
                })

    [<Test>]
    let ``private localized commands blank required arguments callbacks and successful payments preserve Telegram actor payloads`` () =
        withApiClient (fun connectionString client ->
            task {
                let knownCommands =
                    [ 9710L, "/start@web10radio", "/start"
                      9711L, "/help", "/help"
                      9712L, "/terms", "/terms"
                      9713L, "/paysupport", "/paysupport"
                      9714L, "/song", "/song" ]

                for updateId, text, expectedCommand in knownCommands do
                    let! response = sendWebhook client (typedPrivateCommandUpdate updateId 810L "ru-RU" text) [ WebhookSecret ]
                    use response = response
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), text)
                    let! payload = outboxPayloadForUpdate connectionString "TelegramCommandReceived" updateId
                    use document = JsonDocument.Parse(payload)
                    Assert.That(document.RootElement |> stringProperty "command", Is.EqualTo(expectedCommand))
                    Assert.That(document.RootElement |> stringProperty "argument", Is.EqualTo(""))
                    Assert.That((document.RootElement |> jsonProperty "telegramUserId" |> fun value -> value.GetInt64()), Is.EqualTo(810L))
                    Assert.That(document.RootElement |> stringProperty "languageCode", Is.EqualTo("ru-RU"))
                    Assert.That(document.RootElement |> boolProperty "isPrivateChat", Is.True)

                for updateId, text, eventType, payloadField in
                    [ 9715L, "/request", "TrackRequested", "query"
                      9716L, "/say", "SayMessageSubmitted", "text" ] do
                    let! response = sendWebhook client (typedPrivateCommandUpdate updateId 811L "ru-RU" text) [ WebhookSecret ]
                    use response = response
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), text)
                    let! payload = outboxPayloadForUpdate connectionString eventType updateId
                    use document = JsonDocument.Parse(payload)
                    Assert.That(document.RootElement |> stringProperty payloadField, Is.EqualTo(""), "Missing required argument remains durable for localized relay feedback.")
                    Assert.That((document.RootElement |> jsonProperty "telegramUserId" |> fun value -> value.GetInt64()), Is.EqualTo(811L))

                let! callbackResponse = sendWebhook client (typedCallbackUpdate 9717L 812L "ru-RU" "malformed") [ WebhookSecret ]
                use callbackResponse = callbackResponse
                Assert.That(callbackResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                let! callbackPayload = outboxPayloadForUpdate connectionString "TelegramCallbackReceived" 9717L
                use callbackDocument = JsonDocument.Parse(callbackPayload)
                Assert.That((callbackDocument.RootElement |> jsonProperty "telegramUserId" |> fun value -> value.GetInt64()), Is.EqualTo(812L))
                Assert.That(callbackDocument.RootElement |> stringProperty "languageCode", Is.EqualTo("ru-RU"))
                Assert.That(callbackDocument.RootElement |> stringProperty "callbackQueryId", Is.EqualTo("callback-9717"))
                Assert.That(callbackDocument.RootElement |> stringProperty "rawCallbackData", Is.EqualTo("malformed"))

                let paymentId = Guid.NewGuid()
                let! paymentResponse = sendWebhook client (typedSuccessfulPaymentUpdate 9718L paymentId "actor-payload-charge" 50) [ WebhookSecret ]
                use paymentResponse = paymentResponse
                Assert.That(paymentResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                let! paymentPayload = outboxPayloadForUpdate connectionString "DonationPaid" 9718L
                use paymentDocument = JsonDocument.Parse(paymentPayload)
                Assert.That((paymentDocument.RootElement |> jsonProperty "telegramUserId" |> fun value -> value.GetInt64()), Is.EqualTo(502L))
                Assert.That(paymentDocument.RootElement |> stringProperty "paymentId", Is.EqualTo(paymentId.ToString("D")))
                Assert.That(paymentDocument.RootElement |> stringProperty "telegramPaymentChargeId", Is.EqualTo("actor-payload-charge"))
            })

    [<Test>]
    let ``group private-only request remains durable but relay creates no request payment or invoice`` () =
        let telegram = RecordingTelegramBotClient()

        withApiClientWithServices
            (fun services ->
                services.RemoveAll<IHostedService>() |> ignore
                services.AddSingleton<Web10.Radio.API.OutboxRelayHostedService>() |> ignore
                services.RemoveAll<Web10.Radio.Telegram.ITelegramBotClient>() |> ignore
                services.AddSingleton<Web10.Radio.Telegram.ITelegramBotClient>(telegram) |> ignore)
            (fun connectionString _ factory client ->
                task {
                    let updateId = 9720L
                    let query = "must remain private"
                    let! received = sendWebhook client (typedGroupCommandUpdate updateId 813L ("/request " + query)) [ WebhookSecret ]
                    use received = received
                    Assert.That(received.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                    let! inboxRows = countInboxRows connectionString updateId "TrackRequested"
                    Assert.That(inboxRows, Is.EqualTo(1), "Group invocation is retained so the relay can issue the private-chat guidance.")

                    let relay = factory.Services.GetRequiredService<Web10.Radio.API.OutboxRelayHostedService>()
                    let! relayed = relay.ProcessDueEventsOnceAsync(CancellationToken.None)

                    match relayed with
                    | Ok 1 -> ()
                    | Ok count -> Assert.Fail(sprintf "Expected the one group request interaction to relay, got %d events." count)
                    | Error error -> Assert.Fail(sprintf "Group private-only interaction relay failed: %O" error)

                    let! requestRows = countTrackRequestRows connectionString query
                    let! paymentRows = countActivePaymentRows connectionString
                    Assert.That(requestRows, Is.EqualTo(0), "A group request must not create a TrackRequests row.")
                    Assert.That(paymentRows, Is.EqualTo(0), "A group request must not create a payment order.")
                    Assert.That(telegram.SentInvoices.Length, Is.EqualTo(0), "A group request must never send an invoice.")
                    Assert.That(telegram.SentTexts.Length, Is.EqualTo(1))
                    let chatId, text = telegram.SentTexts.[0]
                    Assert.That(chatId, Is.EqualTo(-100502L))
                    Assert.That(text, Is.EqualTo("Open a private chat with the bot for this command."))
                })

    [<Test>]
    let ``library scan is retry-idempotent exposes status count and durably requests work while tracks queue independently`` () =
        withApiClientWithServices
            (fun services -> services.RemoveAll<IHostedService>() |> ignore)
            (fun connectionString _ _ client ->
                task {
                    do! seedBootstrapAdmin connectionString
                    let! session = loginAdmin client
                    let csrf = Some session.CsrfToken
                    let! first = sendAdminJson client csrf "/api/v0/admin/library/scan" "{}"
                    use first = first
                    Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Accepted))
                    let! firstDocument = jsonDocument first
                    use firstDocument = firstDocument
                    let scanJobId = firstDocument.RootElement |> stringProperty "scanJobId"
                    let! retry = sendAdminJson client csrf "/api/v0/admin/library/scan" "{}"
                    use retry = retry
                    Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted))
                    let! retryDocument = jsonDocument retry
                    use retryDocument = retryDocument
                    Assert.That(retryDocument.RootElement |> stringProperty "scanJobId", Is.EqualTo(scanJobId), "A retry must reuse the queued scan for the same backend.")
                    use connection = new NpgsqlConnection(connectionString)
                    do! connection.OpenAsync()
                    use scanUpdate = new NpgsqlCommand("UPDATE \"LibraryScanJobs\" SET \"DiscoveredCount\" = 2 WHERE \"Id\" = @Id;", connection)
                    scanUpdate.Parameters.AddWithValue("Id", Guid.Parse(scanJobId)) |> ignore
                    let! _ = scanUpdate.ExecuteNonQueryAsync()
                    let! status = sendAdminRequest client csrf HttpMethod.Get ("/api/v0/admin/library/scan/" + scanJobId)
                    use status = status
                    Assert.That(status.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    let! statusDocument = jsonDocument status
                    use statusDocument = statusDocument
                    Assert.That(statusDocument.RootElement |> intProperty "discoveredCount", Is.EqualTo(2))
                    use requestedEvent = new NpgsqlCommand("SELECT count(*) FROM \"OutboxEvents\" WHERE \"EventType\" = 'LibraryScanRequested' AND \"Payload\" ->> 'libraryScanJobId' = @Id;", connection)
                    requestedEvent.Parameters.AddWithValue("Id", scanJobId) |> ignore
                    let! requestedCount = requestedEvent.ExecuteScalarAsync()
                    Assert.That(Convert.ToInt32(requestedCount), Is.EqualTo(1), "Accepting a scan is not complete until the durable work event is committed.")

                    let trackId = Guid.Parse("01920000-0000-7000-8000-000000000111")
                    use seedTrack = new NpgsqlCommand("INSERT INTO \"Tracks\" (\"Id\", \"Title\", \"Artist\", \"Album\", \"DurationMs\", \"IsDeleted\") VALUES (@Id, 'Queued track', 'Artist', NULL, NULL, false); INSERT INTO \"TrackFiles\" (\"Id\", \"TrackId\", \"StoragePath\", \"CachePath\", \"IsCached\", \"IsDeleted\") VALUES ('01920000-0000-7000-8000-000000000112', @Id, 'queued.mp3', '/library/queued.mp3', true, false);", connection)
                    seedTrack.Parameters.AddWithValue("Id", trackId) |> ignore
                    let! _ = seedTrack.ExecuteNonQueryAsync()
                    let! tracks = sendAdminRequest client csrf HttpMethod.Get "/api/v0/admin/tracks?query=Queued&limit=1"
                    use tracks = tracks
                    Assert.That(tracks.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    let! tracksDocument = jsonDocument tracks
                    use tracksDocument = tracksDocument
                    Assert.That(tracksDocument.RootElement.[0] |> stringProperty "artist", Is.EqualTo("Artist"))
                    Assert.That(tracksDocument.RootElement.[0] |> stringProperty "album", Is.EqualTo(""))
                    Assert.That(tracksDocument.RootElement.[0] |> intProperty "durationMs", Is.EqualTo(0))
                    let! queued = sendAdminJson client csrf "/api/v0/admin/playback/queue" (sprintf "{\"trackId\":\"%O\"}" trackId)
                    use queued = queued
                    Assert.That(queued.StatusCode, Is.EqualTo(HttpStatusCode.Accepted))
                    let! missing = sendAdminJson client csrf "/api/v0/admin/playback/queue" "{\"trackId\":\"01920000-0000-7000-8000-000000000199\"}"
                    use missing = missing
                    Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
                    do! assertProblemCode "playback.not_found" missing
                })

    [<Test>]
    let ``heartbeat and stream-node assignment contracts authenticate validate and expose durable transport state`` () =
        withApiClient (fun connectionString client ->
            task {
                let heartbeatUri = "/api/v0/stream-node/heartbeat"
                let! anonymous = sendPlaybackCallback client heartbeatUri "{}" []
                use anonymous = anonymous
                Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
                let! malformed = sendPlaybackCallback client heartbeatUri "{\"status\":\"invalid\"}" [ "Bearer stream-callback-token-Secret_123456" ]
                use malformed = malformed
                Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                do! assertProblemCode "stream-node.heartbeat.invalid" malformed
                let payload = "{\"status\":\"live\",\"failureReason\":null,\"metadata\":{\"bitrateKbps\":192,\"restartAttempt\":0,\"activeQueueItemId\":null}}"
                let! accepted = sendPlaybackCallback client heartbeatUri payload [ "Bearer stream-callback-token-Secret_123456" ]
                use accepted = accepted
                Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
                let! heartbeatEvents = countOutboxRows connectionString "StreamNodeHeartbeatReceived"
                Assert.That(heartbeatEvents, Is.EqualTo(1), "A heartbeat acknowledgement must follow its durable event append.")
                let! noAssignment =
                    let request = new HttpRequestMessage(HttpMethod.Get, "/api/v0/stream-node/playback/current")
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer stream-callback-token-Secret_123456") |> ignore
                    client.SendAsync(request)
                use noAssignment = noAssignment
                Assert.That(noAssignment.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))
            })

    [<Test>]
    let ``admin content writes return canonical success and domain validation not-found and conflict statuses`` () =
        withApiClient (fun _ client ->
            task {
                let! session = loginAdmin client
                let csrf = Some session.CsrfToken
                let missingPlaylist = "01920000-0000-7000-8000-000000000221"
                let cases =
                    [ "donation validation", HttpMethod.Put, "/api/v0/admin/donation-goal", "{\"title\":\" \",\"goalStars\":0}", HttpStatusCode.BadRequest, "donation.goal.request_invalid"
                      "donation update", HttpMethod.Put, "/api/v0/admin/donation-goal", "{\"title\":\"Fund the station\",\"goalStars\":500}", HttpStatusCode.OK, ""
                      "social validation", HttpMethod.Put, "/api/v0/admin/social-links", "[{\"id\":null,\"kind\":\"external\",\"name\":\"Bad\",\"handle\":null,\"url\":\"ftp://bad\",\"glyph\":null,\"color\":null,\"qrImageUrl\":null,\"isFeatured\":false}]", HttpStatusCode.BadRequest, "social-links.request_invalid"
                      "social replacement", HttpMethod.Put, "/api/v0/admin/social-links", "[{\"id\":null,\"kind\":\"telegram\",\"name\":\" Telegram \",\"handle\":null,\"url\":\"https://t.me/web10\",\"glyph\":null,\"color\":\"#112233\",\"qrImageUrl\":null,\"isFeatured\":true}]", HttpStatusCode.OK, ""
                      "playlist validation", HttpMethod.Post, "/api/v0/admin/playlists", "{\"name\":\"\",\"description\":null,\"isActive\":false}", HttpStatusCode.BadRequest, "playlist.request_invalid"
                      "playlist creation", HttpMethod.Post, "/api/v0/admin/playlists", "{\"name\":\"Night mix\",\"description\":null,\"isActive\":true}", HttpStatusCode.Created, ""
                      "missing playlist update", HttpMethod.Put, "/api/v0/admin/playlists/" + missingPlaylist, "{\"name\":\"Gone\",\"description\":null,\"isActive\":false}", HttpStatusCode.NotFound, "playlist.not_found"
                      "missing playlist item", HttpMethod.Post, "/api/v0/admin/playlists/" + missingPlaylist + "/items", "{\"trackId\":\"01920000-0000-7000-8000-000000000222\"}", HttpStatusCode.NotFound, "playlist.not_found"
                      "storage validation", HttpMethod.Put, "/api/v0/admin/storage", "{\"additionalBackends\":[{\"id\":null,\"name\":\"Bad local\",\"type\":\"local\",\"localRoot\":\"relative\",\"s3Bucket\":null,\"isEnabled\":true}]}", HttpStatusCode.BadRequest, "storage.request_invalid"
                      "storage replacement", HttpMethod.Put, "/api/v0/admin/storage", "{\"additionalBackends\":[{\"id\":null,\"name\":\"Archive\",\"type\":\"s3\",\"localRoot\":null,\"s3Bucket\":\"web10-archive\",\"isEnabled\":true}]}", HttpStatusCode.OK, "" ]

                for name, method', uri, body, expectedStatus, expectedCode in cases do
                    let! response = sendAdminJsonWithMethod client csrf method' uri body
                    use response = response
                    Assert.That(response.StatusCode, Is.EqualTo(expectedStatus), name)
                    if expectedCode <> "" then do! assertProblemCode expectedCode response

                let! controlStart = sendAdminJson client csrf "/api/v0/admin/stream-node/start" "{}"
                use controlStart = controlStart
                Assert.That(controlStart.StatusCode, Is.EqualTo(HttpStatusCode.Accepted))
                let! startDocument = jsonDocument controlStart
                use startDocument = startDocument
                let startGeneration = startDocument.RootElement |> intProperty "restartGeneration"
                let! controlStop = sendAdminJson client csrf "/api/v0/admin/stream-node/stop" "{}"
                use controlStop = controlStop
                Assert.That(controlStop.StatusCode, Is.EqualTo(HttpStatusCode.Accepted))
                let! stopDocument = jsonDocument controlStop
                use stopDocument = stopDocument
                Assert.That(stopDocument.RootElement |> stringProperty "desiredState", Is.EqualTo("stopped"))
                let! controlRestart = sendAdminJson client csrf "/api/v0/admin/stream-node/restart" "{}"
                use controlRestart = controlRestart
                Assert.That(controlRestart.StatusCode, Is.EqualTo(HttpStatusCode.Accepted))
                let! restartDocument = jsonDocument controlRestart
                use restartDocument = restartDocument
                Assert.That(restartDocument.RootElement |> stringProperty "desiredState", Is.EqualTo("running"))
                Assert.That(restartDocument.RootElement |> intProperty "restartGeneration", Is.EqualTo(startGeneration + 1))
            })

    [<Test>]
    let ``current assignment projects a fenced playing row and admin status keeps the exact thirty-second freshness boundary`` () =
        let nowUtc = DateTimeOffset(2026, 7, 10, 20, 0, 0, TimeSpan.Zero)

        withApiClientAt nowUtc (fun connectionString _ client ->
            task {
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                let trackId = Guid.Parse("01920000-0000-7000-8000-000000000311")
                let queueItemId = Guid.Parse("01920000-0000-7000-8000-000000000312")
                let owner = Guid.Parse("01920000-0000-7000-8000-000000000313")
                use seed = new NpgsqlCommand("INSERT INTO \"Tracks\" (\"Id\", \"Title\", \"Artist\", \"Album\", \"DurationMs\", \"IsDeleted\") VALUES (@TrackId, 'Assignment', 'Node', NULL, NULL, false); INSERT INTO \"TrackFiles\" (\"Id\", \"TrackId\", \"StoragePath\", \"CachePath\", \"ContentType\", \"IsCached\", \"IsDeleted\") VALUES ('01920000-0000-7000-8000-000000000314', @TrackId, 'assignment.mp3', '/library/assignment.mp3', NULL, true, false); INSERT INTO \"PlaybackQueue\" (\"Id\", \"TrackId\", \"Source\", \"Status\", \"RequestedAtUtc\", \"StartedAtUtc\", \"ClaimOwner\", \"ClaimAttempt\", \"ClaimLeaseExpiresAtUtc\", \"IsDeleted\") VALUES (@QueueItemId, @TrackId, 'admin', 'Playing', @NowUtc, @NowUtc, @Owner, 1, @LeaseExpiresAtUtc, false);", connection)
                seed.Parameters.AddWithValue("TrackId", trackId) |> ignore
                seed.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                seed.Parameters.AddWithValue("Owner", owner) |> ignore
                seed.Parameters.AddWithValue("NowUtc", nowUtc) |> ignore
                seed.Parameters.AddWithValue("LeaseExpiresAtUtc", nowUtc.AddMinutes(1.0)) |> ignore
                let! _ = seed.ExecuteNonQueryAsync()
                use assignmentRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v0/stream-node/playback/current")
                assignmentRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer stream-callback-token-Secret_123456") |> ignore
                let! assignment = client.SendAsync(assignmentRequest)
                use assignment = assignment
                Assert.That(assignment.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! assignmentDocument = jsonDocument assignment
                use assignmentDocument = assignmentDocument
                Assert.That(assignmentDocument.RootElement |> stringProperty "queueItemId", Is.EqualTo(queueItemId.ToString("D")))
                Assert.That(assignmentDocument.RootElement |> stringProperty "claimOwner", Is.EqualTo(owner.ToString("D")))
                Assert.That(assignmentDocument.RootElement |> intProperty "claimAttempt", Is.EqualTo(1))
                Assert.That(assignmentDocument.RootElement |> stringProperty "contentType", Is.EqualTo("audio/mpeg"))
                Assert.That(assignmentDocument.RootElement |> intProperty "durationMs", Is.EqualTo(0))

                do! seedHeartbeat connectionString "Live" (nowUtc.AddSeconds(-30.0))
                let! session = loginAdmin client
                let! fresh = sendAdminRequest client (Some session.CsrfToken) HttpMethod.Get "/api/v0/admin/stream-node/status"
                use fresh = fresh
                Assert.That(fresh.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! freshDocument = jsonDocument fresh
                use freshDocument = freshDocument
                Assert.That(freshDocument.RootElement |> stringProperty "status", Is.EqualTo("live"), "Exactly thirty seconds remains fresh.")
                use stale = new NpgsqlCommand("UPDATE \"StreamNodeHeartbeats\" SET \"HeartbeatAtUtc\" = @StaleAtUtc;", connection)
                stale.Parameters.AddWithValue("StaleAtUtc", nowUtc.AddSeconds(-30.001)) |> ignore
                let! _ = stale.ExecuteNonQueryAsync()
                let! staleResponse = sendAdminRequest client (Some session.CsrfToken) HttpMethod.Get "/api/v0/admin/stream-node/status"
                use staleResponse = staleResponse
                Assert.That(staleResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                let! staleDocument = jsonDocument staleResponse
                use staleDocument = staleDocument
                Assert.That(staleDocument.RootElement |> stringProperty "status", Is.EqualTo("offline"))
                Assert.That(staleDocument.RootElement |> valueKindProperty "lastHeartbeatUtc", Is.EqualTo(JsonValueKind.Null))
                Assert.That(staleDocument.RootElement |> intProperty "bitrateKbps", Is.EqualTo(0))
            })

    type private FixtureIds =
        { DonationPaymentId: Guid
          SayPaymentId: Guid
          SayMessageId: Guid }

    type private FixtureDatabaseState =
        { ActiveGoalCount: int64
          GoalTitle: string
          GoalStars: int
          RaisedStars: int
          DonationPaymentCount: int64
          DonationPaymentId: Guid
          DonationTelegramUserId: int64
          DonationPurpose: string
          DonationAmountStars: int
          DonationCurrency: string
          DonationInvoicePayload: string
          DonationPayerDisplayName: string
          DonationStatus: string
          DonationChargeId: string option
          DonationPaidAt: bool
          SayPaymentCount: int64
          SayPaymentId: Guid
          SayTelegramUserId: int64
          SayPurpose: string
          SayPurposeEntityId: Guid
          SayAmountStars: int
          SayCurrency: string
          SayInvoicePayload: string
          SayPayerDisplayName: string
          SayPaymentStatus: string
          SayChargeId: string option
          SayPaymentPaidAt: bool
          SayMessageCount: int64
          SayMessageId: Guid
          SayMessageTelegramUserId: int64
          SayMessageDisplayName: string
          SayMessageText: string
          SayMessageAmountStars: int
          SayMessageStatus: string
          SayMessagePaidAt: bool }

    let private fixtureRoute = "/api/v0/admin/dev/fixtures/paid-vertical-slice"

    let private fixtureInvoicePayload fixtureKey purpose =
        sprintf "dev:%s:%s" fixtureKey purpose

    let private expectedFixtureTelegramUpdateId fixtureKey purpose =
        let source = Encoding.UTF8.GetBytes(fixtureInvoicePayload fixtureKey purpose)
        let hash = SHA256.HashData(source)
        let remainder = System.Numerics.BigInteger(hash, true, true) % System.Numerics.BigInteger(Int64.MaxValue)
        let value = int64 remainder
        if value = 0L then 1L else value

    let private createFixtureFactory connectionString tempRoot environment fixturesEnabled configureServices =
        let enabledText = if fixturesEnabled then "true" else "false"

        let fixturePairs =
            [ KeyValuePair<string, string>("DEV:FIXTURES_ENABLED", enabledText)
              KeyValuePair<string, string>("WEB10_DEV__FIXTURES_ENABLED", enabledText) ]

        (createFactory connectionString tempRoot None None None)
            .WithWebHostBuilder(fun builder ->
                builder.UseSetting(WebHostDefaults.EnvironmentKey, environment) |> ignore

                fixturePairs
                |> List.iter (fun pair -> builder.UseSetting(pair.Key, pair.Value) |> ignore)

                builder.ConfigureAppConfiguration(fun _ configurationBuilder ->
                    configurationBuilder.AddInMemoryCollection(fixturePairs) |> ignore)
                |> ignore

                builder.ConfigureServices(fun services -> configureServices services) |> ignore)

    let private withFixtureApiClient environment fixturesEnabled configureServices work =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let tempRoot = Directory.CreateTempSubdirectory("web10-radio-fixture-api-tests-")
                let factory = createFixtureFactory connectionString tempRoot.FullName environment fixturesEnabled configureServices
                let client = factory.CreateClient(WebApplicationFactoryClientOptions(HandleCookies = true))

                try
                    return! work connectionString factory client
                finally
                    client.Dispose()
                    factory.Dispose()
                    tempRoot.Delete(true)
            })

    let private parseFixtureIds (response: HttpResponseMessage) =
        task {
            let! document = jsonDocument response
            use document = document
            let root = document.RootElement
            Assert.That(root.EnumerateObject() |> Seq.length, Is.EqualTo(3), "The fixture response must contain only its three durable identifiers.")

            return
                { DonationPaymentId = Guid.Parse(root |> stringProperty "donationPaymentId")
                  SayPaymentId = Guid.Parse(root |> stringProperty "sayPaymentId")
                  SayMessageId = Guid.Parse(root |> stringProperty "sayMessageId") }
        }

    let private readFixtureDatabaseState connectionString fixtureKey =
        task {
            let donationPayload = fixtureInvoicePayload fixtureKey "donation"
            let sayPayload = fixtureInvoicePayload fixtureKey "say"
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT
    (SELECT count(*) FROM "DonationGoals" WHERE "IsDeleted" = false AND "IsActive" = true),
    (SELECT "Title" FROM "DonationGoals" WHERE "IsDeleted" = false AND "IsActive" = true ORDER BY "UpdatedAtUtc" DESC, "CreatedAtUtc" DESC, "Id" DESC LIMIT 1),
    (SELECT "GoalStars" FROM "DonationGoals" WHERE "IsDeleted" = false AND "IsActive" = true ORDER BY "UpdatedAtUtc" DESC, "CreatedAtUtc" DESC, "Id" DESC LIMIT 1),
    (SELECT "RaisedStars" FROM "DonationGoals" WHERE "IsDeleted" = false AND "IsActive" = true ORDER BY "UpdatedAtUtc" DESC, "CreatedAtUtc" DESC, "Id" DESC LIMIT 1),
    (SELECT count(*) FROM "Payments" WHERE "TelegramInvoicePayload" = @DonationPayload AND "IsDeleted" = false),
    (SELECT "Id" FROM "Payments" WHERE "TelegramInvoicePayload" = @DonationPayload AND "IsDeleted" = false),
    (SELECT "TelegramUserId" FROM "Payments" WHERE "TelegramInvoicePayload" = @DonationPayload AND "IsDeleted" = false),
    (SELECT "Purpose" FROM "Payments" WHERE "TelegramInvoicePayload" = @DonationPayload AND "IsDeleted" = false),
    (SELECT "AmountStars" FROM "Payments" WHERE "TelegramInvoicePayload" = @DonationPayload AND "IsDeleted" = false),
    (SELECT "Currency" FROM "Payments" WHERE "TelegramInvoicePayload" = @DonationPayload AND "IsDeleted" = false),
    (SELECT "TelegramInvoicePayload" FROM "Payments" WHERE "TelegramInvoicePayload" = @DonationPayload AND "IsDeleted" = false),
    (SELECT "PayerDisplayName" FROM "Payments" WHERE "TelegramInvoicePayload" = @DonationPayload AND "IsDeleted" = false),
    (SELECT "Status" FROM "Payments" WHERE "TelegramInvoicePayload" = @DonationPayload AND "IsDeleted" = false),
    (SELECT "TelegramPaymentChargeId" FROM "Payments" WHERE "TelegramInvoicePayload" = @DonationPayload AND "IsDeleted" = false),
    (SELECT "PaidAtUtc" IS NOT NULL FROM "Payments" WHERE "TelegramInvoicePayload" = @DonationPayload AND "IsDeleted" = false),
    (SELECT count(*) FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false),
    (SELECT "Id" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false),
    (SELECT "TelegramUserId" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false),
    (SELECT "Purpose" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false),
    (SELECT "PurposeEntityId" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false),
    (SELECT "AmountStars" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false),
    (SELECT "Currency" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false),
    (SELECT "TelegramInvoicePayload" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false),
    (SELECT "PayerDisplayName" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false),
    (SELECT "Status" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false),
    (SELECT "TelegramPaymentChargeId" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false),
    (SELECT "PaidAtUtc" IS NOT NULL FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false),
    (SELECT count(*) FROM "SayMessages" WHERE "Id" = (SELECT "PurposeEntityId" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false) AND "IsDeleted" = false),
    (SELECT "Id" FROM "SayMessages" WHERE "Id" = (SELECT "PurposeEntityId" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false) AND "IsDeleted" = false),
    (SELECT "TelegramUserId" FROM "SayMessages" WHERE "Id" = (SELECT "PurposeEntityId" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false) AND "IsDeleted" = false),
    (SELECT "DisplayName" FROM "SayMessages" WHERE "Id" = (SELECT "PurposeEntityId" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false) AND "IsDeleted" = false),
    (SELECT "Text" FROM "SayMessages" WHERE "Id" = (SELECT "PurposeEntityId" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false) AND "IsDeleted" = false),
    (SELECT "AmountStars" FROM "SayMessages" WHERE "Id" = (SELECT "PurposeEntityId" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false) AND "IsDeleted" = false),
    (SELECT "Status" FROM "SayMessages" WHERE "Id" = (SELECT "PurposeEntityId" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false) AND "IsDeleted" = false),
    (SELECT "PaidAtUtc" IS NOT NULL FROM "SayMessages" WHERE "Id" = (SELECT "PurposeEntityId" FROM "Payments" WHERE "TelegramInvoicePayload" = @SayPayload AND "IsDeleted" = false) AND "IsDeleted" = false);""",
                    connection
                )

            command.Parameters.AddWithValue("DonationPayload", donationPayload) |> ignore
            command.Parameters.AddWithValue("SayPayload", sayPayload) |> ignore
            use! reader = command.ExecuteReaderAsync()
            let! found = reader.ReadAsync()
            Assert.That(found, Is.True, "The fixture state query must return one aggregate row.")

            let optionalString index = if reader.IsDBNull(index) then None else Some(reader.GetString(index))

            return
                { ActiveGoalCount = reader.GetInt64(0)
                  GoalTitle = reader.GetString(1)
                  GoalStars = reader.GetInt32(2)
                  RaisedStars = reader.GetInt32(3)
                  DonationPaymentCount = reader.GetInt64(4)
                  DonationPaymentId = reader.GetGuid(5)
                  DonationTelegramUserId = reader.GetInt64(6)
                  DonationPurpose = reader.GetString(7)
                  DonationAmountStars = reader.GetInt32(8)
                  DonationCurrency = reader.GetString(9)
                  DonationInvoicePayload = reader.GetString(10)
                  DonationPayerDisplayName = reader.GetString(11)
                  DonationStatus = reader.GetString(12)
                  DonationChargeId = optionalString 13
                  DonationPaidAt = reader.GetBoolean(14)
                  SayPaymentCount = reader.GetInt64(15)
                  SayPaymentId = reader.GetGuid(16)
                  SayTelegramUserId = reader.GetInt64(17)
                  SayPurpose = reader.GetString(18)
                  SayPurposeEntityId = reader.GetGuid(19)
                  SayAmountStars = reader.GetInt32(20)
                  SayCurrency = reader.GetString(21)
                  SayInvoicePayload = reader.GetString(22)
                  SayPayerDisplayName = reader.GetString(23)
                  SayPaymentStatus = reader.GetString(24)
                  SayChargeId = optionalString 25
                  SayPaymentPaidAt = reader.GetBoolean(26)
                  SayMessageCount = reader.GetInt64(27)
                  SayMessageId = reader.GetGuid(28)
                  SayMessageTelegramUserId = reader.GetInt64(29)
                  SayMessageDisplayName = reader.GetString(30)
                  SayMessageText = reader.GetString(31)
                  SayMessageAmountStars = reader.GetInt32(32)
                  SayMessageStatus = reader.GetString(33)
                  SayMessagePaidAt = reader.GetBoolean(34) }
        }

    let private readFixtureDonationPaidPayload connectionString (paymentId: Guid) =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            use command =
                new NpgsqlCommand(
                    """SELECT "Payload"::text
FROM "OutboxEvents"
WHERE "EventType" = 'DonationPaid'
  AND "IsDeleted" = false
  AND "Payload" ->> 'paymentId' = @PaymentId;""",
                    connection
                )

            command.Parameters.AddWithValue("PaymentId", paymentId.ToString("D")) |> ignore
            let! payload = command.ExecuteScalarAsync()

            if isNull payload || payload = box DBNull.Value then
                return raise (AssertionException(sprintf "Expected a DonationPaid envelope for fixture payment %O." paymentId))
            else
                return string payload
        }

    let private assertFixtureDonationPaidEnvelope (fixtureKey: string) (purpose: string) (paymentId: Guid) (amountStars: int) (payload: string) =
        use document = JsonDocument.Parse(payload)
        let root = document.RootElement
        Assert.That(root |> stringProperty "paymentId", Is.EqualTo(paymentId.ToString("D")))
        Assert.That((root |> jsonProperty "telegramUserId" |> fun value -> value.GetInt64()), Is.EqualTo(900000001L))
        Assert.That(root |> stringProperty "telegramPaymentChargeId", Is.EqualTo(fixtureInvoicePayload fixtureKey purpose))
        Assert.That((root |> jsonProperty "amountStars" |> fun value -> value.GetInt32()), Is.EqualTo(amountStars))
        Assert.That(root |> stringProperty "currency", Is.EqualTo("XTR"))
        Assert.That((root |> jsonProperty "telegramUpdateId" |> fun value -> value.GetInt64()), Is.EqualTo(expectedFixtureTelegramUpdateId fixtureKey purpose))
        Assert.That((root |> jsonProperty "telegramUpdateId" |> fun value -> value.GetInt64()), Is.GreaterThan(0L))

    [<Test>]
    let ``paid vertical slice fixture remains route-absent outside the Development plus enabled double gate`` () =
        let assertAbsent environment fixturesEnabled =
            withFixtureApiClient
                environment
                fixturesEnabled
                (fun services -> services.RemoveAll<IHostedService>() |> ignore)
                (fun connectionString _ client ->
                    task {
                        do! seedBootstrapAdmin connectionString
                        let! session = loginAdmin client
                        let! response = sendAdminJson client (Some session.CsrfToken) fixtureRoute "{\"fixtureKey\":\"double-gate-proof\"}"
                        use response = response
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), sprintf "%s/%b must not map the development fixture route." environment fixturesEnabled)
                    })

        task {
            do! assertAbsent Environments.Production true
            do! assertAbsent Environments.Development false
        }

    [<Test>]
    let ``paid vertical slice fixture inside the enabled development gate requires an admin session and synchronizer token`` () =
        withFixtureApiClient
            Environments.Development
            true
            (fun services -> services.RemoveAll<IHostedService>() |> ignore)
            (fun connectionString _ client ->
                task {
                    let body = "{\"fixtureKey\":\"session-csrf-proof\"}"
                    let! anonymous = sendAdminJson client None fixtureRoute body
                    use anonymous = anonymous
                    Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
                    do! assertProblemCode "admin.auth.required" anonymous

                    do! seedBootstrapAdmin connectionString
                    let! session = loginAdmin client
                    let! missingCsrf = sendAdminJson client None fixtureRoute body
                    use missingCsrf = missingCsrf
                    Assert.That(missingCsrf.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
                    do! assertProblemCode "admin.auth.csrf_invalid" missingCsrf

                    let! wrongCsrf = sendAdminJson client (Some "wrong-synchronizer-token") fixtureRoute body
                    use wrongCsrf = wrongCsrf
                    Assert.That(wrongCsrf.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
                    do! assertProblemCode "admin.auth.csrf_invalid" wrongCsrf

                    let! admitted = sendAdminJson client (Some session.CsrfToken) fixtureRoute body
                    use admitted = admitted
                    Assert.That(admitted.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    let! _ = parseFixtureIds admitted
                    return ()
                })

    [<Test>]
    let ``paid vertical slice fixture rejects malformed extra boundary and oversized bodies with its exact problem code`` () =
        withFixtureApiClient
            Environments.Development
            true
            (fun services -> services.RemoveAll<IHostedService>() |> ignore)
            (fun connectionString _ client ->
                task {
                    do! seedBootstrapAdmin connectionString
                    let! session = loginAdmin client
                    let oversized = "{\"fixtureKey\":\"" + String.replicate (16 * 1024) "x" + "\"}"

                    for name, body in
                        [ "array", "[]"
                          "missing key", "{}"
                          "unknown key", "{\"fixtureKey\":\"valid\",\"other\":true}"
                          "blank key", "{\"fixtureKey\":\"   \"}"
                          "key over sixty-four characters", "{\"fixtureKey\":\"" + String.replicate 65 "k" + "\"}"
                          "over sixteen kibibytes", oversized ] do
                        let! response = sendAdminJson client (Some session.CsrfToken) fixtureRoute body
                        use response = response
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), name)
                        do! assertProblemCode "dev.fixture.invalid" response
                })

    [<Test>]
    let ``paid vertical slice fixture is concurrent-idempotent and relays the real payment transitions exactly once`` () =
        let fixtureKey = "paid-vertical-slice-contract-20260710"
        let telegram = RecordingTelegramBotClient()

        withFixtureApiClient
            Environments.Development
            true
            (fun services ->
                services.RemoveAll<IHostedService>() |> ignore
                services.AddSingleton<Web10.Radio.API.OutboxRelayHostedService>() |> ignore
                services.RemoveAll<Web10.Radio.Telegram.ITelegramBotClient>() |> ignore
                services.AddSingleton<Web10.Radio.Telegram.ITelegramBotClient>(telegram) |> ignore)
            (fun connectionString factory client ->
                task {
                    do! seedBootstrapAdmin connectionString
                    let secondClient = factory.CreateClient(WebApplicationFactoryClientOptions(HandleCookies = true))

                    try
                        let! firstSession = loginAdmin client
                        let! secondSession = loginAdmin secondClient
                        let body = sprintf "{\"fixtureKey\":%s}" (JsonSerializer.Serialize(fixtureKey))
                        let firstRequest = sendAdminJson client (Some firstSession.CsrfToken) fixtureRoute body
                        let secondRequest = sendAdminJson secondClient (Some secondSession.CsrfToken) fixtureRoute body
                        let! responses = Task.WhenAll([| firstRequest; secondRequest |])
                        use firstResponse = responses.[0]
                        use secondResponse = responses.[1]
                        Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                        Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                        let! firstIds = parseFixtureIds firstResponse
                        let! secondIds = parseFixtureIds secondResponse
                        Assert.That(secondIds, Is.EqualTo(firstIds), "Concurrent identical fixture requests must return the already-created durable identifiers.")

                        let! beforeRelay = readFixtureDatabaseState connectionString fixtureKey
                        Assert.That(beforeRelay.ActiveGoalCount, Is.EqualTo(1L))
                        Assert.That(beforeRelay.GoalTitle, Is.EqualTo("Web10.Radio launch"))
                        Assert.That(beforeRelay.GoalStars, Is.EqualTo(5000))
                        Assert.That(beforeRelay.RaisedStars, Is.EqualTo(0), "The fixture must not directly credit the donation before the real relay runs.")
                        Assert.That(beforeRelay.DonationPaymentCount, Is.EqualTo(1L))
                        Assert.That(beforeRelay.DonationPaymentId, Is.EqualTo(firstIds.DonationPaymentId))
                        Assert.That(beforeRelay.DonationTelegramUserId, Is.EqualTo(900000001L))
                        Assert.That(beforeRelay.DonationPurpose, Is.EqualTo("Donation"))
                        Assert.That(beforeRelay.DonationAmountStars, Is.EqualTo(250))
                        Assert.That(beforeRelay.DonationCurrency, Is.EqualTo("XTR"))
                        Assert.That(beforeRelay.DonationInvoicePayload, Is.EqualTo(fixtureInvoicePayload fixtureKey "donation"))
                        Assert.That(beforeRelay.DonationPayerDisplayName, Is.EqualTo("dev_listener"))
                        Assert.That(beforeRelay.DonationStatus, Is.EqualTo("InvoiceCreated"))
                        Assert.That(beforeRelay.DonationChargeId, Is.EqualTo(None))
                        Assert.That(beforeRelay.DonationPaidAt, Is.False)
                        Assert.That(beforeRelay.SayPaymentCount, Is.EqualTo(1L))
                        Assert.That(beforeRelay.SayPaymentId, Is.EqualTo(firstIds.SayPaymentId))
                        Assert.That(beforeRelay.SayTelegramUserId, Is.EqualTo(900000001L))
                        Assert.That(beforeRelay.SayPurpose, Is.EqualTo("Say"))
                        Assert.That(beforeRelay.SayPurposeEntityId, Is.EqualTo(firstIds.SayMessageId))
                        Assert.That(beforeRelay.SayAmountStars, Is.EqualTo(50))
                        Assert.That(beforeRelay.SayCurrency, Is.EqualTo("XTR"))
                        Assert.That(beforeRelay.SayInvoicePayload, Is.EqualTo(fixtureInvoicePayload fixtureKey "say"))
                        Assert.That(beforeRelay.SayPayerDisplayName, Is.EqualTo("dev_listener"))
                        Assert.That(beforeRelay.SayPaymentStatus, Is.EqualTo("InvoiceCreated"))
                        Assert.That(beforeRelay.SayChargeId, Is.EqualTo(None))
                        Assert.That(beforeRelay.SayPaymentPaidAt, Is.False)
                        Assert.That(beforeRelay.SayMessageCount, Is.EqualTo(1L))
                        Assert.That(beforeRelay.SayMessageId, Is.EqualTo(firstIds.SayMessageId))
                        Assert.That(beforeRelay.SayMessageTelegramUserId, Is.EqualTo(900000001L))
                        Assert.That(beforeRelay.SayMessageDisplayName, Is.EqualTo("dev_listener"))
                        Assert.That(beforeRelay.SayMessageText, Is.EqualTo("hello from the development fixture"))
                        Assert.That(beforeRelay.SayMessageAmountStars, Is.EqualTo(50))
                        Assert.That(beforeRelay.SayMessageStatus, Is.EqualTo("PendingPayment"))
                        Assert.That(beforeRelay.SayMessagePaidAt, Is.False)
                        Assert.That(telegram.SentInvoices.Length, Is.EqualTo(0), "The development fixture must never call Telegram to construct its durable payment path.")
                        Assert.That(telegram.SentTexts.Length, Is.EqualTo(0), "The development fixture must never send a Telegram message.")

                        let! pendingOutbox = countOutboxRows connectionString "DonationPaid"
                        Assert.That(pendingOutbox, Is.EqualTo(2), "The fixture must append one real DonationPaid event for each created payment.")
                        let! donationEnvelope = readFixtureDonationPaidPayload connectionString firstIds.DonationPaymentId
                        let! sayEnvelope = readFixtureDonationPaidPayload connectionString firstIds.SayPaymentId
                        assertFixtureDonationPaidEnvelope fixtureKey "donation" firstIds.DonationPaymentId 250 donationEnvelope
                        assertFixtureDonationPaidEnvelope fixtureKey "say" firstIds.SayPaymentId 50 sayEnvelope

                        let relay = factory.Services.GetRequiredService<Web10.Radio.API.OutboxRelayHostedService>()

                        let processOne paymentPurpose =
                            task {
                                let! result = relay.ProcessDueEventsOnceAsync(CancellationToken.None)

                                match result with
                                | Ok 1 -> return ()
                                | Ok count -> return raise (AssertionException(sprintf "Expected one relayed %s fixture payment event, got %d." paymentPurpose count))
                                | Error error -> return raise (AssertionException(sprintf "Relaying fixture %s payment failed: %O" paymentPurpose error))
                            }

                        do! processOne "first"
                        do! processOne "second"

                        let! afterRelay = readFixtureDatabaseState connectionString fixtureKey
                        Assert.That(afterRelay.RaisedStars, Is.EqualTo(250), "Only the DonationPaid transition may credit the fixture goal, exactly once.")
                        Assert.That(afterRelay.DonationStatus, Is.EqualTo("Paid"))
                        Assert.That(afterRelay.DonationChargeId, Is.EqualTo(Some(fixtureInvoicePayload fixtureKey "donation")))
                        Assert.That(afterRelay.DonationPaidAt, Is.True)
                        Assert.That(afterRelay.SayPaymentStatus, Is.EqualTo("Paid"))
                        Assert.That(afterRelay.SayChargeId, Is.EqualTo(Some(fixtureInvoicePayload fixtureKey "say")))
                        Assert.That(afterRelay.SayPaymentPaidAt, Is.True)
                        Assert.That(afterRelay.SayMessageStatus, Is.EqualTo("PaidPendingModeration"))
                        Assert.That(afterRelay.SayMessagePaidAt, Is.True)
                        Assert.That(telegram.SentInvoices.Length, Is.EqualTo(0), "Relaying durable fixture payment events must not issue Telegram invoices.")
                        Assert.That(telegram.SentTexts.Length, Is.EqualTo(0), "Relaying durable fixture payment events must not send Telegram text.")

                        let! repeated = sendAdminJson client (Some firstSession.CsrfToken) fixtureRoute body
                        use repeated = repeated
                        Assert.That(repeated.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                        let! repeatedIds = parseFixtureIds repeated
                        Assert.That(repeatedIds, Is.EqualTo(firstIds), "A retry after relay must preserve all fixture identities.")
                        let! repeatedState = readFixtureDatabaseState connectionString fixtureKey
                        Assert.That(repeatedState.DonationPaymentCount, Is.EqualTo(1L))
                        Assert.That(repeatedState.SayPaymentCount, Is.EqualTo(1L))
                        Assert.That(repeatedState.SayMessageCount, Is.EqualTo(1L))
                        Assert.That(repeatedState.RaisedStars, Is.EqualTo(250), "Fixture retries must not double-credit a paid donation.")
                        let! repeatedOutbox = countOutboxRows connectionString "DonationPaid"
                        Assert.That(repeatedOutbox, Is.EqualTo(2), "Fixture retries must not append duplicate payment events.")
                        let! idleRelay = relay.ProcessDueEventsOnceAsync(CancellationToken.None)

                        match idleRelay with
                        | Ok 0 -> ()
                        | Ok count -> Assert.Fail(sprintf "Fixture retry unexpectedly created %d relay work items." count)
                        | Error error -> Assert.Fail(sprintf "Fixture retry relay check failed: %O" error)
                    finally
                        secondClient.Dispose()
                })
