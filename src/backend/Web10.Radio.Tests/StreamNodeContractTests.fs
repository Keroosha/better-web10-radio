namespace Web10.Radio.Tests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Web10.Radio.StreamNode

type private RecordingCallbackSink() =
    let accepted = ResizeArray<CallbackPayload>()

    member _.Accepted = accepted |> Seq.toList

    interface ICallbackSink with
        member _.IsAlive = true

        member _.Accept(payload) =
            accepted.Add(payload)
            true

type private StubHttpHandler(json: string) =
    inherit HttpMessageHandler()
    override _.SendAsync(_: HttpRequestMessage, _: CancellationToken) =
        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StringContent(json, Encoding.UTF8, "application/json")
        Task.FromResult(response)

[<TestFixture>]
type StreamNodeContractTests() =
    [<Test>]
    member _.``runtime configuration accepts local RTMP contract``() =
        let values = Dictionary<string, string>()
        values["WEB10_API__BASE_URL"] <- "http://api:8080"
        values["WEB10_STREAM__CALLBACK_TOKEN"] <- "stream-callback-token-1234567890"
        values["WEB10_STREAM__STAGE_URL"] <- "http://frontend/"
        values["WEB10_STREAM__RTMP_URL"] <- "rtmp://rtmp-sink:1935/s/"
        values["WEB10_STREAM__RTMP_KEY"] <- "compose-smoke-rtmp-key"

        match Configuration.validate values with
        | Ok config ->
            Assert.That(config.ApiBaseUrl, Is.EqualTo("http://api:8080"))
            Assert.That(config.RtmpUrl, Is.EqualTo("rtmp://rtmp-sink:1935/s/"))
            Assert.That(config.RtmpKey, Is.EqualTo("compose-smoke-rtmp-key"))
            Assert.That(config.CallbackPort, Is.EqualTo(18080))
        | Error error -> Assert.Fail(sprintf "Expected valid stream-node configuration, got %A." error)

    [<Test>]
    member _.``runtime configuration rejects non-RTMP target``() =
        let values = Dictionary<string, string>()
        values["WEB10_API__BASE_URL"] <- "http://api:8080"
        values["WEB10_STREAM__CALLBACK_TOKEN"] <- "stream-callback-token-1234567890"
        values["WEB10_STREAM__STAGE_URL"] <- "http://frontend/"
        values["WEB10_STREAM__RTMP_URL"] <- "https://rtmp-sink:1935/s/"
        values["WEB10_STREAM__RTMP_KEY"] <- "compose-smoke-rtmp-key"

        match Configuration.validate values with
        | Error(ConfigurationError.Invalid key) -> Assert.That(key, Is.EqualTo("WEB10_STREAM__RTMP_URL"))
        | result -> Assert.Fail(sprintf "Expected RTMP validation failure, got %A." result)

    [<Test>]
    member _.``callback server exposes health and strict output failure endpoint``() =
        let sink = RecordingCallbackSink()
        let port = Random.Shared.Next(19000, 29000)
        let server = CallbackServer(port, sink)
        server.Start()

        try
            use client = new HttpClient()
            let health = client.GetAsync(sprintf "http://127.0.0.1:%d/healthz" port).GetAwaiter().GetResult()
            Assert.That(int health.StatusCode, Is.EqualTo(204))

            use emptyBody = new StringContent("{}", Encoding.UTF8, "application/json")
            let accepted = client.PostAsync(sprintf "http://127.0.0.1:%d/callbacks/output-failed" port, emptyBody).GetAwaiter().GetResult()
            Assert.That(int accepted.StatusCode, Is.EqualTo(204))
            Assert.That(sink.Accepted, Has.Length.EqualTo(1))
            Assert.That(sink.Accepted |> List.head, Is.EqualTo(CallbackPayload.OutputFailed))

            use invalidBody = new StringContent("{\"unexpected\":true}", Encoding.UTF8, "application/json")
            let rejected = client.PostAsync(sprintf "http://127.0.0.1:%d/callbacks/output-failed" port, invalidBody).GetAwaiter().GetResult()
            Assert.That(int rejected.StatusCode, Is.EqualTo(400))
        finally
            server.StopAsync().GetAwaiter().GetResult()

    [<Test>]
    member _.``media protocol uri is keyed by queue item and content type``() =
        let baseAssignment: Assignment =
            { QueueItemId = Guid.Parse("00000000-0000-0000-0000-0000000000ab")
              ClaimOwner = Guid.NewGuid()
              ClaimAttempt = 1
              TrackId = Guid.NewGuid()
              ContentType = "audio/mpeg"
              Title = "t"
              Artist = "a"
              DurationMs = 1000 }

        Assert.Multiple(fun () ->
            Assert.That(Liquidsoap.mediaProtocolUri baseAssignment, Is.EqualTo("web10media:00000000-0000-0000-0000-0000000000ab.mp3"))
            Assert.That(Liquidsoap.mediaProtocolUri { baseAssignment with ContentType = "audio/ogg" }, Is.EqualTo("web10media:00000000-0000-0000-0000-0000000000ab.ogg"))
            Assert.That(Liquidsoap.mediaProtocolUri { baseAssignment with ContentType = "application/unknown" }, Is.EqualTo("web10media:00000000-0000-0000-0000-0000000000ab.mp3")))

    static member private SampleConfig() =
        let values = Dictionary<string, string>()
        values["WEB10_API__BASE_URL"] <- "http://api:8080"
        values["WEB10_STREAM__CALLBACK_TOKEN"] <- "stream-callback-token-1234567890"
        values["WEB10_STREAM__STAGE_URL"] <- "http://frontend/"
        values["WEB10_STREAM__RTMP_URL"] <- "rtmp://rtmp-sink:1935/s/"
        values["WEB10_STREAM__RTMP_KEY"] <- "compose-smoke-rtmp-key"
        match Configuration.validate values with
        | Ok config -> config
        | Error error -> failwithf "Expected valid stream-node configuration, got %A." error

    [<Test>]
    member _.``upcoming lookahead parses both current and on-deck fences``() =
        let json = """{"current":{"queueItemId":"00000000-0000-0000-0000-0000000000a1","claimOwner":"00000000-0000-0000-0000-0000000000b1","claimAttempt":4,"trackId":"00000000-0000-0000-0000-0000000000c1","contentType":"audio/mpeg","title":"A","artist":"AA","durationMs":1000},"next":{"queueItemId":"00000000-0000-0000-0000-0000000000a2","claimOwner":"00000000-0000-0000-0000-0000000000b2","claimAttempt":5,"trackId":"00000000-0000-0000-0000-0000000000c2","contentType":"audio/ogg","title":"B","artist":"BB","durationMs":2000}}"""
        use handler = new StubHttpHandler(json)
        use httpClient = new HttpClient(handler)
        use client = new BackendClient(StreamNodeContractTests.SampleConfig(), httpClient)
        let result = (client :> IBackendClient).GetUpcomingAsync(CancellationToken.None).GetAwaiter().GetResult()
        match result with
        | Ok(Some current, Some next) ->
            Assert.Multiple(fun () ->
                Assert.That(current.QueueItemId, Is.EqualTo(Guid.Parse("00000000-0000-0000-0000-0000000000a1")))
                Assert.That(current.ClaimAttempt, Is.EqualTo(4))
                Assert.That(current.ContentType, Is.EqualTo("audio/mpeg"))
                Assert.That(next.QueueItemId, Is.EqualTo(Guid.Parse("00000000-0000-0000-0000-0000000000a2")))
                Assert.That(next.ClaimOwner, Is.EqualTo(Guid.Parse("00000000-0000-0000-0000-0000000000b2")))
                Assert.That(next.ClaimAttempt, Is.EqualTo(5))
                Assert.That(next.ContentType, Is.EqualTo("audio/ogg")))
        | actual -> Assert.Fail(sprintf "Expected both current and on-deck assignments, got %A." actual)

    [<Test>]
    member _.``upcoming lookahead tolerates an absent on-deck``() =
        let json = """{"current":{"queueItemId":"00000000-0000-0000-0000-0000000000a1","claimOwner":"00000000-0000-0000-0000-0000000000b1","claimAttempt":1,"trackId":"00000000-0000-0000-0000-0000000000c1","contentType":"audio/mpeg","title":"A","artist":"AA","durationMs":1000},"next":null}"""
        use handler = new StubHttpHandler(json)
        use httpClient = new HttpClient(handler)
        use client = new BackendClient(StreamNodeContractTests.SampleConfig(), httpClient)
        let result = (client :> IBackendClient).GetUpcomingAsync(CancellationToken.None).GetAwaiter().GetResult()
        match result with
        | Ok(Some _, None) -> ()
        | actual -> Assert.Fail(sprintf "Expected a current with no on-deck, got %A." actual)
