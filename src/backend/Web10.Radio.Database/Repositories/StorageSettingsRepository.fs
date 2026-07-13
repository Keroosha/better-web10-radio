namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Web10.Radio.Database

type StorageSettings =
    { Id: Guid
      S3CacheMaxBytes: int64
      PresignTtlSeconds: int
      CreatedAtUtc: DateTimeOffset
      UpdatedAtUtc: DateTimeOffset }

type CacheEvictionCandidate =
    { TrackFileId: Guid
      CachePath: string
      SizeBytes: int64 }

module StorageSettingsRepository =
    let private databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)

    let private readSettings (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          S3CacheMaxBytes = reader.GetInt64(1)
          PresignTtlSeconds = reader.GetInt32(2)
          CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(3)
          UpdatedAtUtc = reader.GetFieldValue<DateTimeOffset>(4) }

    let private selectLocked (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (token: CancellationToken) : Task<StorageSettings option> =
        task {
            use command = new NpgsqlCommand("""SELECT "Id", "S3CacheMaxBytes", "PresignTtlSeconds", "CreatedAtUtc", "UpdatedAtUtc"
FROM "StorageSettings"
WHERE "SingletonKey" = 'primary' AND "IsDeleted" = false
FOR UPDATE;""", connection, transaction)
            use! reader = command.ExecuteReaderAsync(token)
            let! found = reader.ReadAsync(token)
            return if found then Some(readSettings reader) else None
        }

    let private ensureRow (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (candidateId: Guid) (defaultCacheMaxBytes: int64) (defaultPresignTtlSeconds: int) (atUtc: DateTimeOffset) (token: CancellationToken) =
        task {
            use insert = new NpgsqlCommand("""INSERT INTO "StorageSettings" ("Id", "SingletonKey", "S3CacheMaxBytes", "PresignTtlSeconds", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, 'primary', @CacheMaxBytes, @PresignTtl, false, @AtUtc, @AtUtc)
ON CONFLICT ("SingletonKey") WHERE "IsDeleted" = false AND "SingletonKey" = 'primary' DO NOTHING;""", connection, transaction)
            insert.Parameters.AddWithValue("Id", candidateId) |> ignore
            insert.Parameters.AddWithValue("CacheMaxBytes", defaultCacheMaxBytes) |> ignore
            insert.Parameters.AddWithValue("PresignTtl", defaultPresignTtlSeconds) |> ignore
            insert.Parameters.AddWithValue("AtUtc", atUtc) |> ignore
            let! _ = insert.ExecuteNonQueryAsync(token)
            return ()
        }

    let getOrCreate
        (dataSource: NpgsqlDataSource)
        (candidateId: Guid)
        (defaultCacheMaxBytes: int64)
        (defaultPresignTtlSeconds: int)
        (atUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<StorageSettings, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        do! ensureRow connection transaction candidateId defaultCacheMaxBytes defaultPresignTtlSeconds atUtc token
                        let! state = selectLocked connection transaction token
                        return
                            match state with
                            | Some value -> Ok value
                            | None -> Error(DatabaseError("StorageSettingsRepository.getOrCreate", "The singleton row was not available after insertion."))
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | ex -> return Error(databaseError "StorageSettingsRepository.getOrCreate" ex)
                })
            cancellationToken

    let update
        (dataSource: NpgsqlDataSource)
        (candidateId: Guid)
        (defaultCacheMaxBytes: int64)
        (defaultPresignTtlSeconds: int)
        (s3CacheMaxBytes: int64)
        (presignTtlSeconds: int)
        (atUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<StorageSettings, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        do! ensureRow connection transaction candidateId defaultCacheMaxBytes defaultPresignTtlSeconds atUtc token
                        let! locked = selectLocked connection transaction token
                        match locked with
                        | None -> return Error(DatabaseError("StorageSettingsRepository.update", "The singleton row has not been created."))
                        | Some _ ->
                            use command = new NpgsqlCommand("""UPDATE "StorageSettings"
SET "S3CacheMaxBytes" = @CacheMaxBytes,
    "PresignTtlSeconds" = @PresignTtl,
    "UpdatedAtUtc" = @AtUtc
WHERE "SingletonKey" = 'primary' AND "IsDeleted" = false
RETURNING "Id", "S3CacheMaxBytes", "PresignTtlSeconds", "CreatedAtUtc", "UpdatedAtUtc";""", connection, transaction)
                            command.Parameters.AddWithValue("CacheMaxBytes", s3CacheMaxBytes) |> ignore
                            command.Parameters.AddWithValue("PresignTtl", presignTtlSeconds) |> ignore
                            command.Parameters.AddWithValue("AtUtc", atUtc) |> ignore
                            use! reader = command.ExecuteReaderAsync(token)
                            let! found = reader.ReadAsync(token)
                            return if found then Ok(readSettings reader) else Error(DatabaseError("StorageSettingsRepository.update", "The locked singleton row disappeared."))
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | ex -> return Error(databaseError "StorageSettingsRepository.update" ex)
                })
            cancellationToken

    [<Literal>]
    let private cacheBytesSql = """SELECT COALESCE(SUM(COALESCE(tf."SizeBytes", 0)), 0)
FROM "TrackFiles" AS tf
WHERE tf."IsDeleted" = false
  AND tf."IsCached" = true
  AND tf."CachePath" IS NOT NULL AND btrim(tf."CachePath") <> ''
  AND tf."CachePath" <> tf."StoragePath"
  AND tf."StorageBackendId" IS NULL;"""

    let totalS3CacheBytes (dataSource: NpgsqlDataSource) (cancellationToken: CancellationToken) : Task<Result<int64, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        use command = new NpgsqlCommand(cacheBytesSql, connection, transaction)
                        let! value = command.ExecuteScalarAsync(token)
                        return Ok(Convert.ToInt64(value))
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | ex -> return Error(databaseError "StorageSettingsRepository.totalS3CacheBytes" ex)
                })
            cancellationToken

    [<Literal>]
    let private evictionCandidatesSql = """SELECT tf."Id", tf."CachePath", COALESCE(tf."SizeBytes", 0)
FROM "TrackFiles" AS tf
LEFT JOIN LATERAL (
    SELECT MAX(pq."FinishedAtUtc") AS "LastPlayedAtUtc"
    FROM "PlaybackQueue" AS pq
    WHERE pq."TrackId" = tf."TrackId" AND pq."IsDeleted" = false AND pq."Status" = 'Played'
) AS history ON true
WHERE tf."IsDeleted" = false
  AND tf."IsCached" = true
  AND tf."CachePath" IS NOT NULL AND btrim(tf."CachePath") <> ''
  AND tf."CachePath" <> tf."StoragePath"
  AND tf."StorageBackendId" IS NULL
  AND NOT EXISTS (
      SELECT 1 FROM "PlaybackQueue" AS active
      WHERE active."TrackId" = tf."TrackId" AND active."IsDeleted" = false
        AND active."Status" IN ('Queued', 'Claimed', 'Playing')
  )
ORDER BY history."LastPlayedAtUtc" ASC NULLS FIRST, tf."UpdatedAtUtc" ASC, tf."Id" ASC;"""

    let listEvictionCandidates (dataSource: NpgsqlDataSource) (cancellationToken: CancellationToken) : Task<Result<CacheEvictionCandidate list, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        use command = new NpgsqlCommand(evictionCandidatesSql, connection, transaction)
                        use! reader = command.ExecuteReaderAsync(token)
                        let values = ResizeArray<CacheEvictionCandidate>()
                        let mutable reading = true
                        while reading do
                            let! found = reader.ReadAsync(token)
                            if found then
                                values.Add
                                    { TrackFileId = reader.GetGuid(0)
                                      CachePath = reader.GetString(1)
                                      SizeBytes = reader.GetInt64(2) }
                            else
                                reading <- false
                        return Ok(List.ofSeq values)
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | ex -> return Error(databaseError "StorageSettingsRepository.listEvictionCandidates" ex)
                })
            cancellationToken

    let markCacheEvicted (dataSource: NpgsqlDataSource) (trackFileId: Guid) (atUtc: DateTimeOffset) (cancellationToken: CancellationToken) : Task<Result<bool, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        use command = new NpgsqlCommand("""UPDATE "TrackFiles"
SET "IsCached" = false,
    "CachePath" = NULL,
    "UpdatedAtUtc" = @AtUtc
WHERE "Id" = @Id
  AND "IsDeleted" = false
  AND "CachePath" <> "StoragePath"
  AND "StorageBackendId" IS NULL;""", connection, transaction)
                        command.Parameters.AddWithValue("Id", trackFileId) |> ignore
                        command.Parameters.AddWithValue("AtUtc", atUtc) |> ignore
                        let! affected = command.ExecuteNonQueryAsync(token)
                        return Ok(affected = 1)
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | ex -> return Error(databaseError "StorageSettingsRepository.markCacheEvicted" ex)
                })
            cancellationToken
