namespace Web10.Radio.Tests

open System
open System.IO
open NUnit.Framework
open Web10.Radio.API

[<TestFixture>]
type StorageContentTests() =
    let expectError (result: Result<string, unit>) =
        match result with
        | Error () -> ()
        | Ok _ -> Assert.Fail("Expected a validation error.")

    let expectOk (expected: string) (result: Result<string, unit>) =
        match result with
        | Ok actual -> Assert.That(actual, Is.EqualTo(expected))
        | Error () -> Assert.Fail("Expected a valid value.")

    [<Test>]
    member _.``storage paths accept root and canonical descendants``() =
        expectOk "" (StoragePath.canonical "")
        expectOk "album/track.mp3" (StoragePath.canonical "album/track.mp3")
        expectError (StoragePath.nonRoot "")

    [<TestCase("../escape")>]
    [<TestCase("album/../track.mp3")>]
    [<TestCase("album//track.mp3")>]
    [<TestCase("album\\track.mp3")>]
    [<TestCase("/absolute")>]
    [<TestCase("trailing/")>]
    member _.``storage paths reject traversal and unsafe separators``(path: string) =
        expectError (StoragePath.canonical path)

    [<Test>]
    member _.``storage paths reject NUL and oversized UTF8 values``() =
        let nulPath = "album/" + string (char 0) + "track.mp3"
        let oversizedPath = String.replicate 1025 "a"
        expectError (StoragePath.canonical nulPath)
        expectError (StoragePath.canonical oversizedPath)

    [<Test>]
    member _.``folder markers are distinct from canonical file keys``() =
        Assert.That(S3KeyValidation.isCanonical "foo", Is.True)
        Assert.That(S3KeyValidation.isCanonical "foo/", Is.False)
        Assert.That(S3KeyValidation.isFolderMarker "foo/", Is.True)
        Assert.That(S3KeyValidation.isFolderMarker "foobar/", Is.True)

    [<Test>]
    member _.``local path resolution rejects symlink traversal``() =
        let root = Path.Combine(Path.GetTempPath(), "web10-storage-test-" + Guid.NewGuid().ToString("N"))
        let outside = Path.Combine(Path.GetTempPath(), "web10-storage-outside-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        Directory.CreateDirectory(outside) |> ignore
        let link = Path.Combine(root, "link")
        try
            Directory.CreateSymbolicLink(link, outside) |> ignore
            expectError (StoragePath.localAbsolute root "link/file.mp3")
        finally
            try Directory.Delete(root, true) with _ -> ()
            try Directory.Delete(outside, true) with _ -> ()
