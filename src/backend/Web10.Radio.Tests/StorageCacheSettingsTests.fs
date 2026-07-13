namespace Web10.Radio.Tests

open System
open System.Threading
open Npgsql
open Dodo.Primitives
open NUnit.Framework
open Web10.Radio.Database.Repositories

module StorageCacheSettingsTests =
    let private newId () =
        Uuid.CreateVersion7().ToGuidBigEndian()

    [<Test>]
    let ``storage settings seed defaults then persist updates`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let now = DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero)

                let! created = StorageSettingsRepository.getOrCreate dataSource (newId ()) 10737418240L 3600 now CancellationToken.None
                match created with
                | Ok settings ->
                    Assert.Multiple(fun () ->
                        Assert.That(settings.S3CacheMaxBytes, Is.EqualTo(10737418240L))
                        Assert.That(settings.PresignTtlSeconds, Is.EqualTo(3600)))
                | actual -> Assert.Fail(sprintf "Expected the seeded defaults, got %A." actual)

                let! updated = StorageSettingsRepository.update dataSource (newId ()) 10737418240L 3600 5368709120L 1800 (now.AddMinutes 1.0) CancellationToken.None
                match updated with
                | Ok settings ->
                    Assert.Multiple(fun () ->
                        Assert.That(settings.S3CacheMaxBytes, Is.EqualTo(5368709120L))
                        Assert.That(settings.PresignTtlSeconds, Is.EqualTo(1800)))
                | actual -> Assert.Fail(sprintf "Expected the updated values, got %A." actual)

                let! reread = StorageSettingsRepository.getOrCreate dataSource (newId ()) 10737418240L 3600 (now.AddMinutes 2.0) CancellationToken.None
                match reread with
                | Ok settings -> Assert.That(settings.S3CacheMaxBytes, Is.EqualTo(5368709120L), "getOrCreate never overwrites an existing singleton row.")
                | actual -> Assert.Fail(sprintf "Expected the persisted row, got %A." actual)
            })

    [<Test>]
    let ``cache eviction targets only non-active default-backend S3 copies`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let s3Track = newId ()
                let s3File = newId ()
                let localTrack = newId ()
                let localFile = newId ()
                let activeTrack = newId ()
                let activeFile = newId ()
                let activeQueueId = newId ()
                let claimOwner = newId ()
                let now = DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use setup =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@S3Track, 's3', 'a', false), (@LocalTrack, 'local', 'a', false), (@ActiveTrack, 'active', 'a', false);

INSERT INTO "TrackFiles" ("Id", "TrackId", "StorageBackendId", "StoragePath", "CachePath", "ContentType", "IsCached", "SizeBytes", "IsDeleted")
VALUES (@S3File, @S3Track, NULL, 'library/s3.mp3', '/cache/audio/s3.mp3', 'audio/mpeg', true, 1000, false),
       (@LocalFile, @LocalTrack, NULL, '/srv/media/local.mp3', '/srv/media/local.mp3', 'audio/mpeg', true, 2000, false),
       (@ActiveFile, @ActiveTrack, NULL, 'library/active.mp3', '/cache/audio/active.mp3', 'audio/mpeg', true, 4000, false);

INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc", "StartedAtUtc", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc")
VALUES (@ActiveQueueId, @ActiveTrack, 'fallback', 'Playing', 0, @Now, @Now, @ClaimOwner, 1, @Lease);""",
                        connection
                    )
                for name, value in
                    [ "S3Track", box s3Track; "S3File", box s3File
                      "LocalTrack", box localTrack; "LocalFile", box localFile
                      "ActiveTrack", box activeTrack; "ActiveFile", box activeFile
                      "ActiveQueueId", box activeQueueId; "ClaimOwner", box claimOwner
                      "Now", box now; "Lease", box (now.AddSeconds 30.0) ] do
                    setup.Parameters.AddWithValue(name, value) |> ignore
                let! _ = setup.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! total = StorageSettingsRepository.totalS3CacheBytes dataSource CancellationToken.None
                match total with
                | Ok bytes -> Assert.That(bytes, Is.EqualTo(5000L), "Only S3 default-backend cache copies count (Local source excluded).")
                | actual -> Assert.Fail(sprintf "Expected the S3 cache byte total, got %A." actual)

                let! candidates = StorageSettingsRepository.listEvictionCandidates dataSource CancellationToken.None
                match candidates with
                | Ok list ->
                    let ids = list |> List.map (fun candidate -> candidate.TrackFileId)
                    Assert.Multiple(fun () ->
                        Assert.That(ids.Length, Is.EqualTo(1), "Only the non-active S3 copy is evictable; Local and Playing are protected.")
                        Assert.That(List.head ids, Is.EqualTo(s3File)))
                | actual -> Assert.Fail(sprintf "Expected the eviction candidates, got %A." actual)

                let! marked = StorageSettingsRepository.markCacheEvicted dataSource s3File (now.AddMinutes 1.0) CancellationToken.None
                match marked with
                | Ok true -> ()
                | actual -> Assert.Fail(sprintf "Expected the eviction to apply, got %A." actual)

                use check = new NpgsqlCommand("""SELECT "IsCached", "CachePath" FROM "TrackFiles" WHERE "Id" = @Id;""", connection)
                check.Parameters.AddWithValue("Id", s3File) |> ignore
                let! reader = check.ExecuteReaderAsync()
                use reader = reader
                let! _ = reader.ReadAsync()
                Assert.Multiple(fun () ->
                    Assert.That(reader.GetBoolean(0), Is.False, "The evicted copy is no longer cached.")
                    Assert.That(reader.IsDBNull(1), Is.True, "CachePath is cleared so a later scan can re-materialize it."))
            })
