namespace Web10.Radio.Tests

open System
open NUnit.Framework
open Web10.Radio.Application
open Web10.Radio.Database.Repositories

[<TestFixture>]
type BackgroundWorkerTests() =
    [<Test>]
    member _.``background worker errors keep actionable operation context``() =
        let id = Guid.Parse("019f0000-0000-7000-8000-000000000001")
        let message = BackgroundWorkerError.toMessage (StateTransitionRejected("mark processed", id))
        Assert.That(message, Does.Contain("mark processed"))
        Assert.That(message, Does.Contain(id.ToString("D")))

    [<Test>]
    member _.``domain event audience routes API playback events away from Telegram``() =
        Assert.That(DomainEventAudience.forType DomainEventType.PlaybackStarted, Is.EqualTo(OutboxAudience.Api))
        Assert.That(DomainEventAudience.forType DomainEventType.TrackRequested, Is.EqualTo(OutboxAudience.Telegram))

    [<Test>]
    member _.``event parser accepts every current event literal``() =
        for eventType in DomainEventType.all do
            let literal = DomainEventType.toString eventType
            Assert.That(DomainEventType.tryParse literal, Is.EqualTo(Some eventType), literal)

    [<Test>]
    member _.``envelope rejects non-object payloads before persistence``() =
        let result = DomainEventEnvelope.create TimeProvider.System DomainEventType.PlaybackStarted "tests" None None "[]"
        match result with
        | Error PayloadMustBeJsonObject -> ()
        | Error error -> Assert.Fail(sprintf "Unexpected error: %A" error)
        | Ok _ -> Assert.Fail("An array payload must be rejected.")
