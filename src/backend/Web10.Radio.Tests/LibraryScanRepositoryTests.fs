namespace Web10.Radio.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Database
open Web10.Radio.Database.Repositories

[<TestFixture>]
type LibraryScanRepositoryTests() =
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
                Task.FromException<S3ReadHandle>(InvalidOperationException("S3 is not used by Local scanner tests."))

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

    let scannerOptions connectionString storageRoot cacheRoot =
        { Postgres = { ConnectionString = connectionString }
          Stream =
            { RtmpUrl = Uri("rtmp://localhost:1935/live")
              RtmpKey = "test-key"
              StageUrl = Uri("http://localhost:8080")
              CallbackToken = "test-callback" }
          Storage =
            { Type = Local
              LocalRoot = storageRoot
              CacheRoot = cacheRoot
              S3Bucket = ""
              S3Region = "us-east-1"
              S3ServiceUrl = None
              S3ForcePathStyle = false
              MaxUploadBytes = 536870912L }
          Admin = { Username = "admin"; Password = "admin-password" }
          Otel = None
          DevelopmentFixturesEnabled = false
          ServeFrontend = false
          DataProtection = { KeyRingPath = cacheRoot } }
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

    [<Test>]
    member _.``successful Local rescan soft deletes track files absent from current source inventory``() =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let temporaryRoot = Path.Combine(Path.GetTempPath(), "web10-radio-scan-" + Guid.NewGuid().ToString("N"))
                let cacheRoot = Path.Combine(Path.GetTempPath(), "web10-radio-cache-" + Guid.NewGuid().ToString("N"))
                let albumDirectory = Path.Combine(temporaryRoot, "album")
                let sourceFile = Path.Combine(albumDirectory, "Artist - Removed.mp3")
                Directory.CreateDirectory(albumDirectory) |> ignore
                File.WriteAllBytes(sourceFile, [| 1uy |])

                try
                    use dataSource = NpgsqlDataSource.Create(connectionString)
                    let scanner =
                        new LibraryScanHostedService(
                            dataSource,
                            TimeProvider.System,
                            scannerOptions connectionString temporaryRoot cacheRoot,
                            unusedS3Storage,
                            new StorageOperationCoordinator(),
                            NullLogger<LibraryScanHostedService>.Instance
                        )

                    let nowUtc = TimeProvider.System.GetUtcNow()
                    let firstJobId = Guid.NewGuid()

                    let! firstJob =
                        LibraryScanRepository.createOrGetActiveJob dataSource firstJobId None nowUtc CancellationToken.None

                    match firstJob with
                    | Error error -> Assert.Fail(sprintf "Could not create first Local scan job: %A" error)
                    | Ok CreateOrGetActiveLibraryScanJobResult.StorageBackendNotFound -> Assert.Fail("Configured default Local storage must be available.")
                    | Ok(CreateOrGetActiveLibraryScanJobResult.Existing _) -> Assert.Fail("First Local scan job unexpectedly reused an active job.")
                    | Ok(CreateOrGetActiveLibraryScanJobResult.Created _) -> ()

                    let! firstScan = scanner.ProcessJobAsync(firstJobId, CancellationToken.None)

                    match firstScan with
                    | Error error -> Assert.Fail(sprintf "First Local scan failed: %A" error)
                    | Ok false -> Assert.Fail("First Local scan job was not claimed.")
                    | Ok true -> ()

                    use connection = new NpgsqlConnection(connectionString)
                    do! connection.OpenAsync()
                    use firstFileCommand =
                        new NpgsqlCommand(
                            """SELECT "Id", "TrackId", "StoragePath"
FROM "TrackFiles"
WHERE "StoragePath" = 'album/Artist - Removed.mp3'
  AND "IsDeleted" = false;""",
                            connection
                        )

                    use! firstFileReader = firstFileCommand.ExecuteReaderAsync()
                    let! hasFirstFile = firstFileReader.ReadAsync()
                    Assert.That(hasFirstFile, Is.True, "First Local scan did not persist the canonical relative source path.")
                    let trackFileId = firstFileReader.GetGuid(0)
                    let trackId = firstFileReader.GetGuid(1)
                    Assert.That(firstFileReader.GetString(2), Is.EqualTo("album/Artist - Removed.mp3"))
                    do! firstFileReader.CloseAsync()

                    File.Delete(sourceFile)
                    let secondJobId = Guid.NewGuid()

                    let! secondJob =
                        LibraryScanRepository.createOrGetActiveJob dataSource secondJobId None (TimeProvider.System.GetUtcNow()) CancellationToken.None

                    match secondJob with
                    | Error error -> Assert.Fail(sprintf "Could not create second Local scan job: %A" error)
                    | Ok CreateOrGetActiveLibraryScanJobResult.StorageBackendNotFound -> Assert.Fail("Configured default Local storage must remain available.")
                    | Ok(CreateOrGetActiveLibraryScanJobResult.Existing _) -> Assert.Fail("Second Local scan job unexpectedly reused an active job.")
                    | Ok(CreateOrGetActiveLibraryScanJobResult.Created _) -> ()

                    let! secondScan = scanner.ProcessJobAsync(secondJobId, CancellationToken.None)

                    match secondScan with
                    | Error error -> Assert.Fail(sprintf "Second Local scan failed: %A" error)
                    | Ok false -> Assert.Fail("Second Local scan job was not claimed.")
                    | Ok true -> ()

                    use reconciledCommand =
                        new NpgsqlCommand(
                            """SELECT track_file."IsDeleted", track."IsDeleted"
FROM "TrackFiles" AS track_file
INNER JOIN "Tracks" AS track ON track."Id" = track_file."TrackId"
WHERE track_file."Id" = @TrackFileId
  AND track."Id" = @TrackId;""",
                            connection
                        )

                    reconciledCommand.Parameters.AddWithValue("TrackFileId", trackFileId) |> ignore
                    reconciledCommand.Parameters.AddWithValue("TrackId", trackId) |> ignore
                    use! reconciledReader = reconciledCommand.ExecuteReaderAsync()
                    let! hasReconciledRow = reconciledReader.ReadAsync()
                    Assert.That(hasReconciledRow, Is.True, "Reconciliation must preserve auditable soft-deleted rows.")
                    Assert.That(reconciledReader.GetBoolean(0), Is.True, "The missing TrackFile must be soft deleted.")
                    Assert.That(reconciledReader.GetBoolean(1), Is.True, "An orphaned Track must be soft deleted.")
                finally
                    if Directory.Exists(temporaryRoot) then Directory.Delete(temporaryRoot, true)
                    if Directory.Exists(cacheRoot) then Directory.Delete(cacheRoot, true)
            })
