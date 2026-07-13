namespace Web10.Radio.Tests

open System.Text
open NUnit.Framework
open Web10.Radio.API

module CueSheetTests =
    let private parse (sheet: string) =
        CueSheet.parseBytes "library/album.cue" (Encoding.UTF8.GetBytes(sheet))

    [<Test>]
    let ``single-image CUE parses metadata and exact CD-frame boundaries`` () =
        let sheet =
            """PERFORMER "Album Artist"
TITLE "Album Title"
FILE "album.flac" WAVE
  TRACK 01 AUDIO
    TITLE "First"
    INDEX 01 00:00:00
  TRACK 02 AUDIO
    PERFORMER "Second Artist"
    TITLE "Second"
    INDEX 01 00:02:00
"""

        match parse sheet with
        | Error error -> Assert.Fail(sprintf "Expected CUE parse success, got line %d: %s" error.LineNumber error.Message)
        | Ok tracks ->
            Assert.That(tracks, Has.Length.EqualTo(2))
            Assert.Multiple(fun () ->
                let first = tracks.[0]
                let second = tracks.[1]
                Assert.That(first.FileName, Is.EqualTo("album.flac"))
                Assert.That(first.TrackNumber, Is.EqualTo(1))
                Assert.That(first.Title, Is.EqualTo(Some "First"))
                Assert.That(first.Artist, Is.EqualTo(Some "Album Artist"))
                Assert.That(first.Album, Is.EqualTo(Some "Album Title"))
                Assert.That(first.StartMs, Is.EqualTo(0))
                Assert.That(second.TrackNumber, Is.EqualTo(2))
                Assert.That(second.Title, Is.EqualTo(Some "Second"))
                Assert.That(second.Artist, Is.EqualTo(Some "Second Artist"))
                Assert.That(second.StartMs, Is.EqualTo(2000)))

    [<Test>]
    let ``CUE retains WAV declaration for scanner FLAC stem resolution`` () =
        let sheet =
            """FILE "01 - Hyperstition.wav" WAVE
  TRACK 01 AUDIO
    INDEX 01 00:00:01
"""
        match parse sheet with
        | Error error -> Assert.Fail(error.Message)
        | Ok [ track ] ->
            Assert.Multiple(fun () ->
                Assert.That(track.FileName, Is.EqualTo("01 - Hyperstition.wav"))
                Assert.That(track.StartMs, Is.EqualTo(13)))
        | Ok tracks -> Assert.Fail(sprintf "Expected one track, got %d." tracks.Length)

    [<Test>]
    let ``CUE decodes Windows-1251 metadata and track overrides global values`` () =
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)
        let sheet =
            """PERFORMER "Глобальный артист"
TITLE "Глобальный альбом"
FILE "album.flac" WAVE
  TRACK 01 AUDIO
    TITLE "Локальный трек"
    INDEX 01 00:00:00
"""
        let bytes = Encoding.GetEncoding(1251).GetBytes(sheet)
        match CueSheet.parseBytes "library/cyrillic.cue" bytes with
        | Error error -> Assert.Fail(error.Message)
        | Ok [ track ] ->
            Assert.Multiple(fun () ->
                Assert.That(track.Title, Is.EqualTo(Some "Локальный трек"))
                Assert.That(track.Artist, Is.EqualTo(Some "Глобальный артист"))
                Assert.That(track.Album, Is.EqualTo(Some "Глобальный альбом")))
        | Ok tracks -> Assert.Fail(sprintf "Expected one track, got %d." tracks.Length)

    [<TestCase("TRACK 01 AUDIO\n  INDEX 01 00:00:00", 1)>]
    [<TestCase("FILE \"album.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n  TRACK 01 AUDIO\n    INDEX 01 00:01:00", 4)>]
    [<TestCase("FILE \"album.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:02:00\n  TRACK 02 AUDIO\n    INDEX 01 00:01:00", 5)>]
    let ``CUE rejects malformed duplicate and regressing audio tracks with line context`` (sheet: string) (lineNumber: int) =
        match parse sheet with
        | Ok tracks -> Assert.Fail(sprintf "Expected parse failure, got %d tracks." tracks.Length)
        | Error error ->
            Assert.Multiple(fun () ->
                Assert.That(error.SheetPath, Is.EqualTo("library/album.cue"))
                Assert.That(error.LineNumber, Is.EqualTo(lineNumber))
                Assert.That(error.Message, Is.Not.Empty))
