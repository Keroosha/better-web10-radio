namespace Web10.Radio.Tests

open System
open System.Text.Json
open NUnit.Framework
open Web10.Radio.API

module DomainEventTests =
    let private expectedEventLiterals =
        [ "TrackRequested"
          "TrackRequestMatched"
          "SayMessageSubmitted"
          "SayMessageModerated"
          "DonationInvoiceCreated"
          "DonationPaid"
          "PaymentRefunded"
          "LibraryScanRequested"
          "TrackDiscovered"
          "PlaybackQueueItemClaimed"
          "PlaybackStarted"
          "PlaybackEnded"
          "StreamNodeHeartbeatReceived"
          "StreamNodeFailureDetected"
          "AdminGoalChanged"
          "SocialLinkChanged" ]

    type private FixedClock(nowUtc: DateTimeOffset) =
        interface IClock with
            member _.UtcNow = nowUtc

    let private idGenerator () = UuidV7IdGenerator() :> IIdGenerator

    let private clockAt value = FixedClock(value) :> IClock

    let private createEnvelope payloadJson =
        DomainEventEnvelope.create
            (idGenerator ())
            (clockAt (DateTimeOffset(2026, 7, 8, 12, 30, 0, TimeSpan.Zero)))
            DomainEventType.TrackRequested
            "web10.radio.tests"
            None
            None
            payloadJson

    let private assertVersion7Guid (name: string) (value: Guid) =
        Assert.That(value, Is.Not.EqualTo(Guid.Empty), sprintf "%s must not be Guid.Empty." name)
        Assert.That(value.ToString("D").Substring(14, 1), Is.EqualTo("7"), sprintf "%s must be an RFC9562 UUIDv7 value." name)

    [<Test>]
    let ``event type literals match SPEC table order`` () =
        let actual = DomainEventType.all |> List.map DomainEventType.toString |> List.toArray
        let expected = expectedEventLiterals |> List.toArray

        Assert.That(actual, Is.EqualTo(box expected))

    [<Test>]
    let ``event type parser accepts exactly the documented literals`` () =
        let parsedRoundTrip =
            expectedEventLiterals
            |> List.map (fun literal ->
                DomainEventType.tryParse literal
                |> Option.map DomainEventType.toString)
            |> List.toArray

        let expectedRoundTrip = expectedEventLiterals |> List.map Some |> List.toArray

        Assert.That(parsedRoundTrip, Is.EqualTo(box expectedRoundTrip))
        Assert.That(DomainEventType.tryParse "track-requested", Is.EqualTo(None))

    [<Test>]
    let ``envelope rejects blank and array payloads as non-object JSON`` () =
        let cases = [ ""; "[]" ]

        for payloadJson in cases do
            match createEnvelope payloadJson with
            | Error DomainEventError.PayloadMustBeJsonObject -> ()
            | Error error -> Assert.Fail(sprintf "Expected PayloadMustBeJsonObject for payload %A, but got %A." payloadJson error)
            | Ok envelope -> Assert.Fail(sprintf "Expected payload %A to be rejected, but got envelope %A." payloadJson envelope)

    [<Test>]
    let ``domain event errors render stable public messages`` () =
        Assert.That(DomainEventError.toMessage DomainEventError.ProducerRequired, Is.EqualTo("Domain event producer is required."))
        Assert.That(DomainEventError.toMessage DomainEventError.PayloadMustBeJsonObject, Is.EqualTo("Domain event payload must be a JSON object."))

    [<Test>]
    let ``invalid JSON payload surfaces parse error with stable message prefix`` () =
        match createEnvelope "not-json" with
        | Ok envelope -> Assert.Fail(sprintf "Expected invalid JSON to be rejected, but got envelope %A." envelope)
        | Error(DomainEventError.PayloadJsonInvalid message as error) ->
            Assert.That(message, Is.Not.Empty)
            Assert.That(DomainEventError.toMessage error, Does.StartWith("Domain event payload is invalid JSON:"))
        | Error error -> Assert.Fail(sprintf "Expected PayloadJsonInvalid, but got %A." error)

    [<Test>]
    let ``valid envelope uses uuidv7 runtime ids supplied producer causation and clock`` () =
        let occurredAtUtc = DateTimeOffset(2026, 7, 8, 12, 30, 0, TimeSpan.Zero)
        let causationId = Guid.Parse("018f12f0-4d20-7000-8000-000000000001")

        let result =
            DomainEventEnvelope.create
                (idGenerator ())
                (clockAt occurredAtUtc)
                DomainEventType.DonationPaid
                "web10.radio.tests"
                None
                (Some causationId)
                "{\"amountStars\":42}"

        match result with
        | Error error -> Assert.Fail(sprintf "Expected valid envelope, but got %A." error)
        | Ok envelope ->
            assertVersion7Guid "EventId" envelope.EventId
            assertVersion7Guid "CorrelationId" envelope.CorrelationId
            Assert.That(envelope.EventType, Is.EqualTo(DomainEventType.DonationPaid))
            Assert.That(envelope.OccurredAtUtc, Is.EqualTo(occurredAtUtc))
            Assert.That(envelope.Producer, Is.EqualTo("web10.radio.tests"))
            Assert.That(envelope.CausationId, Is.EqualTo(Some causationId))
            Assert.That(envelope.PayloadJson, Is.EqualTo("{\"amountStars\":42}"))


    [<Test>]
    let ``envelope toJson emits exact event envelope contract with raw payload`` () =
        let eventId = Guid.Parse("018f12f0-4d20-7000-8000-000000000101")
        let correlationId = Guid.Parse("018f12f0-4d20-7000-8000-000000000102")
        let occurredAtUtc = DateTimeOffset(2026, 7, 8, 12, 30, 0, TimeSpan.Zero)
        let envelope =
            { EventId = eventId
              EventType = DomainEventType.DonationPaid
              OccurredAtUtc = occurredAtUtc
              Producer = "web10.radio.tests"
              CorrelationId = correlationId
              CausationId = None
              PayloadJson = "{\"amountStars\":42}" }

        let json = DomainEventEnvelope.toJson envelope
        use document = JsonDocument.Parse(json)
        let root = document.RootElement
        let propertyNames =
            root.EnumerateObject()
            |> Seq.map (fun property -> property.Name)
            |> Seq.toArray

        Assert.That(
            propertyNames,
            Is.EqualTo(box [| "eventId"; "eventType"; "occurredAtUtc"; "producer"; "correlationId"; "causationId"; "payload" |])
        )
        let mutable payloadJsonProperty = Unchecked.defaultof<JsonElement>
        Assert.That(root.TryGetProperty("payloadJson", &payloadJsonProperty), Is.False)
        Assert.That(root.GetProperty("eventId").GetString(), Is.EqualTo(eventId.ToString("D")))
        Assert.That(root.GetProperty("eventType").GetString(), Is.EqualTo("DonationPaid"))
        Assert.That(root.GetProperty("producer").GetString(), Is.EqualTo("web10.radio.tests"))
        Assert.That(root.GetProperty("correlationId").GetString(), Is.EqualTo(correlationId.ToString("D")))
        Assert.That(root.GetProperty("causationId").ValueKind, Is.EqualTo(JsonValueKind.Null))
        Assert.That(root.GetProperty("payload").GetProperty("amountStars").GetInt32(), Is.EqualTo(42))