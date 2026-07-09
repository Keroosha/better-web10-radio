namespace Web10.Radio.Tests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Mvc.Testing
open Microsoft.Extensions.Configuration
open Npgsql
open NUnit.Framework

module ApiContractTests =
    [<Literal>]
    let private WebhookSecret = "test-webhook-secret"

    type private SseEvent =
        { Name: string
          Data: string }

    let private configurationPairs connectionString tempRoot =
        let strippedPairs =
            [ "POSTGRES:CONNECTION_STRING", connectionString
              "TELEGRAM:BOT_TOKEN", "123456:TESTTOKEN"
              "TELEGRAM:WEBHOOK_SECRET", WebhookSecret
              "TELEGRAM:CHANNEL_ID_OR_USERNAME", "@netscapedidnothingwrong"
              "STREAM:RTMP_URL", "rtmps://dc4-1.rtmp.t.me/s/"
              "STREAM:RTMP_KEY", "test-rtmp-key"
              "STREAM:STAGE_URL", "http://localhost:5173/"
              "STORAGE:TYPE", "Local"
              "STORAGE:LOCAL_ROOT", Path.Combine(tempRoot, "library")
              "STORAGE:S3_BUCKET", "web10-radio-test-bucket"
              "OTEL:EXPORTER_OTLP_ENDPOINT", "http://localhost:4317"
              "DATA_PROTECTION:KEY_RING_PATH", Path.Combine(tempRoot, "keys") ]

        let web10Mirrors =
            strippedPairs
            |> List.map (fun (key, value) -> sprintf "WEB10_%s" (key.Replace(":", "__")), value)

        strippedPairs @ web10Mirrors
        |> List.map (fun (key, value) -> KeyValuePair<string, string>(key, value))

    let private createFactory connectionString tempRoot =
        let pairs = configurationPairs connectionString tempRoot

        (new WebApplicationFactory<Web10.Radio.API.ApiProgramMarker>())
            .WithWebHostBuilder(fun builder ->
                pairs |> List.iter (fun pair -> builder.UseSetting(pair.Key, pair.Value) |> ignore)

                builder.ConfigureAppConfiguration(fun _ configurationBuilder ->
                    configurationBuilder.AddInMemoryCollection(pairs) |> ignore)
                |> ignore)

    let private withApiClient (work: string -> HttpClient -> Task<'T>) =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let tempRoot = Directory.CreateTempSubdirectory("web10-radio-api-tests-")
                let factory = createFactory connectionString tempRoot.FullName
                let client = factory.CreateClient()

                try
                    let! result = work connectionString client
                    return result
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

    let private assertEmptyPlayerState (root: JsonElement) =
        let stream = root |> jsonProperty "stream"
        Assert.That(stream |> stringProperty "status", Is.EqualTo("offline"))

        let queue = root |> jsonProperty "queue"
        Assert.That(queue |> arrayLengthProperty "items", Is.EqualTo(0))

        let donationGoal = root |> jsonProperty "donationGoal"
        Assert.That(donationGoal |> valueKindProperty "topDonator", Is.EqualTo(JsonValueKind.Null))

        let superChat = root |> jsonProperty "superChat"
        Assert.That(superChat |> arrayLengthProperty "messages", Is.EqualTo(0))
        Assert.That(root |> arrayLengthProperty "socials", Is.EqualTo(0))

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

    let private readFirstSseEvent (stream: Stream) (cancellationToken: CancellationToken) =
        task {
            use reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true)
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

    [<Test>]
    let ``GET player state on empty database returns public empty state`` () =
        withApiClient (fun _ client ->
            task {
                let! response = client.GetAsync("/api/v0/player/state")
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                let! document = jsonDocument response
                use document = document
                assertEmptyPlayerState document.RootElement
            })

    [<Test>]
    let ``GET player events emits initial player state event with JSON data`` () =
        withApiClient (fun _ client ->
            task {
                use cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10.0))

                let! response =
                    client.GetAsync("/api/v0/player/events", HttpCompletionOption.ResponseHeadersRead, cancellation.Token)

                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                let! stream = response.Content.ReadAsStreamAsync(cancellation.Token)
                use stream = stream
                let! event = readFirstSseEvent stream cancellation.Token
                Assert.That(event.Name, Is.EqualTo("player.state"))

                use document = JsonDocument.Parse(event.Data)
                document.RootElement |> jsonProperty "serverTimeUtc" |> ignore
                document.RootElement |> jsonProperty "stream" |> jsonProperty "status" |> ignore
                document.RootElement |> jsonProperty "queue" |> jsonProperty "items" |> ignore
                document.RootElement |> jsonProperty "donationGoal" |> ignore
                document.RootElement |> jsonProperty "superChat" |> ignore
                document.RootElement |> jsonProperty "socials" |> ignore
                document.RootElement |> jsonProperty "overlay" |> ignore
            })

    [<Test>]
    let ``GET player stream without live cached track returns stream unavailable problem`` () =
        withApiClient (fun _ client ->
            task {
                let! response = client.GetAsync("/api/v0/player/stream")
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable))
                do! assertProblemCode "stream.unavailable" response
            })

    [<Test>]
    let ``GET player song on empty database returns empty strings`` () =
        withApiClient (fun _ client ->
            task {
                let! response = client.GetAsync("/api/v0/player/song")
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                let! document = jsonDocument response
                use document = document
                let root = document.RootElement
                Assert.That(root |> stringProperty "title", Is.EqualTo(""))
                Assert.That(root |> stringProperty "artist", Is.EqualTo(""))
                Assert.That(root |> stringProperty "externalUrl", Is.EqualTo(""))
                Assert.That(root |> stringProperty "fallbackText", Is.EqualTo(""))
            })

    [<Test>]
    let ``POST telegram webhook with wrong secret is unauthorized and records no inbox row`` () =
        withApiClient (fun connectionString client ->
            task {
                let! response = postJson client "/api/v0/telegram/webhook" """{ "update_id": 777 }""" (Some "wrong-secret")
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
                do! assertProblemCode "telegram.webhook.secret_invalid" response

                let! count = countAllInboxRows connectionString
                Assert.That(count, Is.EqualTo(0))
            })

    [<Test>]
    let ``POST telegram webhook records one active row for duplicate accepted update`` () =
        withApiClient (fun connectionString client ->
            task {
                let body = """{ "update_id": 9001 }"""
                let! firstResponse = postJson client "/api/v0/telegram/webhook" body (Some WebhookSecret)
                use firstResponse = firstResponse
                Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                let! secondResponse = postJson client "/api/v0/telegram/webhook" body (Some WebhookSecret)
                use secondResponse = secondResponse
                Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                let! count = countInboxRows connectionString 9001L "telegram.webhook"
                Assert.That(count, Is.EqualTo(1))
            })

    [<Test>]
    let ``GET admin social links returns frontend social link wire shape`` () =
        withApiClient (fun connectionString client ->
            task {
                do! seedAdminReadModels connectionString

                let! response = client.GetAsync("/api/v0/admin/social-links")
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                let! document = jsonDocument response
                use document = document
                Assert.That(document.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array))
                Assert.That(document.RootElement.GetArrayLength(), Is.EqualTo(1))

                let social = document.RootElement.[0]
                Assert.That(social |> stringProperty "id", Is.EqualTo("01920000-0000-7000-8000-00000000a001"))
                Assert.That(social |> stringProperty "kind", Is.EqualTo("telegram"))
                Assert.That(social |> stringProperty "name", Is.EqualTo("Telegram"))
                Assert.That(social |> stringProperty "handle", Is.EqualTo(""))
                Assert.That(social |> stringProperty "url", Is.EqualTo("https://t.me/web10radio"))
                Assert.That(social |> stringProperty "glyph", Is.EqualTo(""))
                Assert.That(social |> stringProperty "color", Is.EqualTo(""))
                Assert.That(social |> stringProperty "qrImageUrl", Is.EqualTo(""))
                Assert.That(social |> boolProperty "isFeatured", Is.True)
            })

    [<Test>]
    let ``GET admin donation goal returns frontend donation goal wire shape`` () =
        withApiClient (fun connectionString client ->
            task {
                do! seedAdminReadModels connectionString

                let! response = client.GetAsync("/api/v0/admin/donation-goal")
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                let! document = jsonDocument response
                use document = document
                let root = document.RootElement
                Assert.That(root |> stringProperty "title", Is.EqualTo("Keep Web10.Radio live"))
                Assert.That(root |> intProperty "raisedStars", Is.EqualTo(250))
                Assert.That(root |> intProperty "goalStars", Is.EqualTo(1000))
                Assert.That(root |> valueKindProperty "topDonator", Is.EqualTo(JsonValueKind.Null))
                Assert.That(root |> arrayLengthProperty "recent", Is.EqualTo(0))
            })

    [<Test>]
    let ``POST admin library scan returns unpinned admin contract problem`` () =
        withApiClient (fun _ client ->
            task {
                let! response = client.PostAsync("/api/v0/admin/library/scan", new StringContent("{}", Encoding.UTF8, "application/json"))
                use response = response
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotImplemented))
                do! assertProblemCode "admin.contract_unpinned" response
            })
