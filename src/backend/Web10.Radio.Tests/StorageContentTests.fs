namespace Web10.Radio.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Database.Repositories

[<TestFixture>]
type StorageContentTests() =
    let unusedS3Storage: IS3ObjectStorage =
        { new IS3ObjectStorage with
            member _.ListPageAsync(_, _, _, _, _, _, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                Task.FromResult({ Objects = []; CommonPrefixes = []; NextContinuationToken = None })

            member _.GetMetadataAsync(_, _, _, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                Task.FromResult({ ContentLength = 0L; ContentType = None; LastModifiedUtc = None; ETag = None })

            member _.OpenReadAsync(_, _, _, _, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                Task.FromException<S3ReadHandle>(InvalidOperationException("S3 is not used by Local storage tests."))

            member _.UploadAsync(_, _, _, _, _, _, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                Task.CompletedTask

            member _.DeleteManyAsync(_, _, _, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                Task.FromResult([])

            member _.ProbeBucketAsync(_, _, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                Task.CompletedTask

            member _.DownloadToFileAsync(_, _, _, _, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                Task.CompletedTask

            member _.GeneratePresignedGetUrlAsync(_, _, _, _, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                Task.FromResult("https://s3.example.test/presigned") }

    let localStorageOptions localRoot cacheRoot =
        { Type = Local
          LocalRoot = localRoot
          CacheRoot = cacheRoot
          S3Bucket = ""
          S3Region = "us-east-1"
          S3ServiceUrl = None
          S3ForcePathStyle = false
          MaxUploadBytes = 536870912L }

    let expectError (result: Result<string, unit>) =
        match result with
        | Error () -> ()
        | Ok _ -> Assert.Fail("Expected a validation error.")

    let expectOk (expected: string) (result: Result<string, unit>) =
        match result with
        | Ok actual -> Assert.That(actual, Is.EqualTo(expected))
        | Error () -> Assert.Fail("Expected a valid value.")

    [<TestCase("Altær.flac")>]
    [<TestCase("Песня 漢字 🌟.flac")>]
    member _.``storage download headers RFC 5987 encode Unicode file names``(fileName: string) =
        let disposition = ApiEndpoints.storageDownloadContentDisposition("album/" + fileName)
        let encodedPrefix = "filename*=UTF-8''"
        let startIndex = disposition.IndexOf(encodedPrefix, StringComparison.Ordinal)

        Assert.That(disposition, Does.StartWith("attachment;"))
        Assert.That(startIndex, Is.GreaterThanOrEqualTo(0), "Expected an RFC 5987 filename* parameter.")
        Assert.That(disposition |> Seq.forall (fun character -> int character <= 127), Is.True, "HTTP header values must remain ASCII.")

        let encodedFileName = disposition.Substring(startIndex + encodedPrefix.Length)
        Assert.That(Uri.UnescapeDataString(encodedFileName), Is.EqualTo(fileName))

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

    [<Test>]
    member _.``folder deletion removes descendants and soft deletes matching library rows``() =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let localRoot = Path.Combine(Path.GetTempPath(), "web10-radio-storage-" + Guid.NewGuid().ToString("N"))
                let cacheRoot = Path.Combine(Path.GetTempPath(), "web10-radio-storage-cache-" + Guid.NewGuid().ToString("N"))
                let nestedDirectory = Path.Combine(localRoot, "delete-me", "nested")
                let sourceFile = Path.Combine(nestedDirectory, "Artist - Folder.mp3")
                Directory.CreateDirectory(nestedDirectory) |> ignore
                File.WriteAllBytes(sourceFile, [| 1uy; 2uy |])

                try
                    use dataSource = NpgsqlDataSource.Create(connectionString)
                    let trackId = Guid.NewGuid()
                    let trackFileId = Guid.NewGuid()
                    use connection = new NpgsqlConnection(connectionString)
                    do! connection.OpenAsync()
                    use seed =
                        new NpgsqlCommand(
                            """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackId, 'Folder', 'Artist', false);
INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "IsCached", "IsDeleted")
VALUES (@TrackFileId, @TrackId, 'delete-me/nested/Artist - Folder.mp3', false, false);""",
                            connection
                        )

                    seed.Parameters.AddWithValue("TrackId", trackId) |> ignore
                    seed.Parameters.AddWithValue("TrackFileId", trackFileId) |> ignore
                    let! _ = seed.ExecuteNonQueryAsync()

                    let service =
                        new StorageContentService(
                            localStorageOptions localRoot cacheRoot,
                            dataSource,
                            unusedS3Storage,
                            TimeProvider.System,
                            new StorageOperationCoordinator()
                        )

                    let selections = [ { PhysicalPath = "delete-me"; Kind = StorageSelectionKind.Folder } ]
                    let! preview = service.PreviewDeleteAsync(None, selections, CancellationToken.None)

                    let report =
                        match preview with
                        | Error error ->
                            Assert.Fail(sprintf "Folder deletion preview failed: %A" error)
                            Unchecked.defaultof<StorageDeleteReport>
                        | Ok value -> value

                    Assert.That(report.Descriptors |> List.filter (fun descriptor -> descriptor.Kind = StorageSelectionKind.File) |> List.length, Is.EqualTo(1))
                    Assert.That(report.Descriptors |> List.filter (fun descriptor -> descriptor.Kind = StorageSelectionKind.Folder) |> List.length, Is.EqualTo(2))
                    Assert.That(report.Impact.TrackFiles |> List.map _.StoragePath, Is.EqualTo(([ "delete-me/nested/Artist - Folder.mp3" ] : string list) :> obj))

                    let! deleted = service.DeleteAsync(None, selections, report.ImpactToken, CancellationToken.None)

                    match deleted with
                    | Error error -> Assert.Fail(sprintf "Folder deletion failed: %A" error)
                    | Ok(deletedReport, mutation) ->
                        Assert.That(deletedReport.Impact.TrackFiles |> List.length, Is.EqualTo(1))
                        Assert.That(mutation.DeletedTrackCount, Is.EqualTo(1))

                    Assert.That(Directory.Exists(Path.Combine(localRoot, "delete-me")), Is.False, "The physical folder tree must be removed.")

                    use state =
                        new NpgsqlCommand(
                            """SELECT track_file."IsDeleted", track."IsDeleted"
FROM "TrackFiles" AS track_file
INNER JOIN "Tracks" AS track ON track."Id" = track_file."TrackId"
WHERE track_file."Id" = @TrackFileId
  AND track."Id" = @TrackId;""",
                            connection
                        )

                    state.Parameters.AddWithValue("TrackFileId", trackFileId) |> ignore
                    state.Parameters.AddWithValue("TrackId", trackId) |> ignore
                    use! stateReader = state.ExecuteReaderAsync()
                    let! hasState = stateReader.ReadAsync()
                    Assert.That(hasState, Is.True)
                    Assert.That(stateReader.GetBoolean(0), Is.True)
                    Assert.That(stateReader.GetBoolean(1), Is.True)
                finally
                    if Directory.Exists(localRoot) then Directory.Delete(localRoot, true)
                    if Directory.Exists(cacheRoot) then Directory.Delete(cacheRoot, true)
            })

    [<Test>]
    member _.``physical folder deletion failure rolls back matching library mutations``() =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let localRoot = Path.Combine(Path.GetTempPath(), "web10-radio-storage-rollback-" + Guid.NewGuid().ToString("N"))
                let cacheRoot = Path.Combine(Path.GetTempPath(), "web10-radio-storage-rollback-cache-" + Guid.NewGuid().ToString("N"))
                let deleteDirectory = Path.Combine(localRoot, "delete-me")
                let sourceFile = Path.Combine(deleteDirectory, "Artist - Rollback.mp3")
                let outsideDirectory = Path.Combine(Path.GetTempPath(), "web10-radio-storage-outside-" + Guid.NewGuid().ToString("N"))
                Directory.CreateDirectory(deleteDirectory) |> ignore
                Directory.CreateDirectory(outsideDirectory) |> ignore
                File.WriteAllBytes(sourceFile, [| 1uy |])

                try
                    use dataSource = NpgsqlDataSource.Create(connectionString)
                    let trackId = Guid.NewGuid()
                    let trackFileId = Guid.NewGuid()
                    use connection = new NpgsqlConnection(connectionString)
                    do! connection.OpenAsync()
                    use seed =
                        new NpgsqlCommand(
                            """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@TrackId, 'Rollback', 'Artist', false);
INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "IsCached", "IsDeleted")
VALUES (@TrackFileId, @TrackId, 'delete-me/Artist - Rollback.mp3', false, false);""",
                            connection
                        )

                    seed.Parameters.AddWithValue("TrackId", trackId) |> ignore
                    seed.Parameters.AddWithValue("TrackFileId", trackFileId) |> ignore
                    let! _ = seed.ExecuteNonQueryAsync()

                    let service =
                        new StorageContentService(
                            localStorageOptions localRoot cacheRoot,
                            dataSource,
                            unusedS3Storage,
                            TimeProvider.System,
                            new StorageOperationCoordinator()
                        )

                    let selections = [ { PhysicalPath = "delete-me"; Kind = StorageSelectionKind.Folder } ]
                    let! preview = service.PreviewDeleteAsync(None, selections, CancellationToken.None)

                    let report =
                        match preview with
                        | Error error ->
                            Assert.Fail(sprintf "Folder deletion preview failed: %A" error)
                            Unchecked.defaultof<StorageDeleteReport>
                        | Ok value -> value

                    let lateLink = Path.Combine(deleteDirectory, "late-link")
                    Directory.CreateSymbolicLink(lateLink, outsideDirectory) |> ignore
                    let! deleted = service.DeleteAsync(None, selections, report.ImpactToken, CancellationToken.None)
                    match deleted with
                    | Error StorageContentError.DeleteFailed -> ()
                    | Error error -> Assert.Fail(sprintf "Expected physical deletion failure, got: %A" error)
                    | Ok _ -> Assert.Fail("Late reparse-point child must make physical folder deletion fail.")

                    use state =
                        new NpgsqlCommand(
                            """SELECT track_file."IsDeleted", track."IsDeleted"
FROM "TrackFiles" AS track_file
INNER JOIN "Tracks" AS track ON track."Id" = track_file."TrackId"
WHERE track_file."Id" = @TrackFileId
  AND track."Id" = @TrackId;""",
                            connection
                        )

                    state.Parameters.AddWithValue("TrackFileId", trackFileId) |> ignore
                    state.Parameters.AddWithValue("TrackId", trackId) |> ignore
                    use! stateReader = state.ExecuteReaderAsync()
                    let! hasState = stateReader.ReadAsync()
                    Assert.That(hasState, Is.True)
                    Assert.That(stateReader.GetBoolean(0), Is.False, "The TrackFile mutation must roll back after a physical delete failure.")
                    Assert.That(stateReader.GetBoolean(1), Is.False, "The Track mutation must roll back after a physical delete failure.")
                finally
                    if Directory.Exists(localRoot) then Directory.Delete(localRoot, true)
                    if Directory.Exists(outsideDirectory) then Directory.Delete(outsideDirectory, true)
                    if Directory.Exists(cacheRoot) then Directory.Delete(cacheRoot, true)
            })
