namespace Web10.Radio.Tests

open System.Text.Json
open NUnit.Framework
open Web10.Radio.API

[<TestFixture>]
type ApiContractTests() =
    static member private JsonOptions() =
        let options = JsonSerializerOptions(JsonSerializerDefaults.Web)
        options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        options

    [<Test>]
    member _.``playback queue accepted contract uses queueItemId``() =
        let json = JsonSerializer.Serialize(({ QueueItemId = "019f0000-0000-7000-8000-000000000001" } : PlaybackQueueAcceptedDto), ApiContractTests.JsonOptions())
        Assert.That(json, Does.Contain("\"queueItemId\""))
        Assert.That(json, Does.Not.Contain("QueueItemId"))

    [<Test>]
    member _.``stream control contract exposes the next playback generation``() =
        let contract =
            { DesiredState = "running"
              RestartGeneration = 4
              PlaybackCommands = []
              NextPlaybackGeneration = 9L }
        let json = JsonSerializer.Serialize(contract, ApiContractTests.JsonOptions())
        Assert.That(json, Does.Contain("\"nextPlaybackGeneration\":9"))

    [<Test>]
    member _.``admin track contract carries metadata and cover source``() =
        let contract =
            { Id = "019f0000-0000-7000-8000-000000000001"
              Title = "Title"
              Artist = "Artist"
              Album = "Album"
              DurationMs = 123000
              HasCachedFile = true
              CoverImageUrl = "/api/v0/player/cover/asset"
              MetadataSource = "Embedded" }
        let json = JsonSerializer.Serialize(contract, ApiContractTests.JsonOptions())
        Assert.That(json, Does.Contain("\"album\":\"Album\""))
        Assert.That(json, Does.Contain("\"metadataSource\":\"Embedded\""))

    [<Test>]
    member _.``queue reorder request preserves ordered identifiers``() =
        let request = { QueueItemIds = [ "first"; "second" ] }
        Assert.That(String.concat "," request.QueueItemIds, Is.EqualTo("first,second"))
