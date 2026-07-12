namespace Web10.Radio.Tests

open System
open System.IO
open NUnit.Framework
open Web10.Radio.API

[<TestFixture>]
type LibraryScanRepositoryTests() =
    [<Test>]
    member _.``metadata reader rejects empty paths``() =
        match TrackMetadata.read "   " with
        | Error(TrackMetadataError.InvalidPath message) -> Assert.That(message, Does.Contain("empty"))
        | Error error -> Assert.Fail(sprintf "Unexpected metadata error: %A" error)
        | Ok _ -> Assert.Fail("An empty path must be rejected.")

    [<Test>]
    member _.``metadata reader reports missing files``() =
        let path = Path.Combine(Path.GetTempPath(), "web10-radio-missing-" + Guid.NewGuid().ToString("N") + ".mp3")
        match TrackMetadata.read path with
        | Error(TrackMetadataError.FileNotFound actualPath) -> Assert.That(actualPath, Is.EqualTo(path))
        | Error error -> Assert.Fail(sprintf "Unexpected metadata error: %A" error)
        | Ok _ -> Assert.Fail("A missing media file must be rejected.")

    [<Test>]
    member _.``metadata error cases remain discriminated and actionable``() =
        let error = TrackMetadataError.DurationInvalid "duration"
        match error with
        | TrackMetadataError.DurationInvalid reason -> Assert.That(reason, Is.EqualTo("duration"))
        | _ -> Assert.Fail("Unexpected metadata error case.")
