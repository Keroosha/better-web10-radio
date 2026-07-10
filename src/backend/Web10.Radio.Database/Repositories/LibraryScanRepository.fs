namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Npgsql
open NpgsqlTypes
open Web10.Radio.Database

type StorageBackendRecord =
    { Id: Guid option
      Name: string
      Type: string
      LocalRoot: string option
      S3Bucket: string option }

type LibraryScanJobRecord =
    { Id: Guid
      StorageBackendId: Guid option
      ClaimOwner: Guid
      ClaimAttempt: int
      LeaseExpiresAtUtc: DateTimeOffset }

type DiscoveredTrackFile =
    { TrackId: Guid
      TrackFileId: Guid
      StorageBackendId: Guid option
      StoragePath: string
      CachePath: string option
      IsCached: bool
      Title: string
      Artist: string
      ContentType: string option
      SizeBytes: int64 option
      DiscoveredAtUtc: DateTimeOffset }

module LibraryScanRepository =
    [<Literal>]
    let private claimNextJobSql = """WITH next_job AS (
    SELECT "Id"
    FROM "LibraryScanJobs"
    WHERE "IsDeleted" = false
      AND (@JobId IS NULL OR "Id" = @JobId)
      AND (
          "Status" = 'Queued'
          OR (
              "Status" = 'Running'
              AND ("ClaimLeaseExpiresAtUtc" IS NULL OR "ClaimLeaseExpiresAtUtc" <= @StartedAtUtc)
          )
      )
    ORDER BY
        CASE WHEN "Status" = 'Running' THEN 0 ELSE 1 END,
        "RequestedAtUtc" ASC,
        "CreatedAtUtc" ASC
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
UPDATE "LibraryScanJobs" AS job
SET "Status" = 'Running',
    "ClaimOwner" = @ClaimOwner,
    "ClaimAttempt" = job."ClaimAttempt" + 1,
    "ClaimLeaseExpiresAtUtc" = @LeaseExpiresAtUtc,
    "StartedAtUtc" = @StartedAtUtc,
    "FinishedAtUtc" = NULL,
    "FailureReason" = NULL,
    "UpdatedAtUtc" = @StartedAtUtc
FROM next_job
WHERE job."Id" = next_job."Id"
RETURNING job."Id", job."StorageBackendId", job."ClaimOwner", job."ClaimAttempt", job."ClaimLeaseExpiresAtUtc";"""

    [<Literal>]
    let private completeJobSql = """UPDATE "LibraryScanJobs"
SET "Status" = 'Completed',
    "FinishedAtUtc" = @FinishedAtUtc,
    "ClaimOwner" = NULL,
    "ClaimLeaseExpiresAtUtc" = NULL,
    "UpdatedAtUtc" = @FinishedAtUtc
WHERE "Id" = @JobId
  AND "IsDeleted" = false
  AND "Status" = 'Running'
  AND "ClaimOwner" = @ClaimOwner
  AND "ClaimAttempt" = @ClaimAttempt;"""

    [<Literal>]
    let private failJobSql = """UPDATE "LibraryScanJobs"
SET "Status" = 'Failed',
    "FinishedAtUtc" = @FinishedAtUtc,
    "FailureReason" = @FailureReason,
    "ClaimOwner" = NULL,
    "ClaimLeaseExpiresAtUtc" = NULL,
    "UpdatedAtUtc" = @FinishedAtUtc
WHERE "Id" = @JobId
  AND "IsDeleted" = false
  AND "Status" = 'Running'
  AND "ClaimOwner" = @ClaimOwner
  AND "ClaimAttempt" = @ClaimAttempt;"""

    [<Literal>]
    let private renewJobLeaseSql = """UPDATE "LibraryScanJobs"
SET "ClaimLeaseExpiresAtUtc" = @LeaseExpiresAtUtc,
    "UpdatedAtUtc" = @RenewedAtUtc
WHERE "Id" = @JobId
  AND "IsDeleted" = false
  AND "Status" = 'Running'
  AND "ClaimOwner" = @ClaimOwner
  AND "ClaimAttempt" = @ClaimAttempt;"""

    [<Literal>]
    let private getStorageBackendSql = """SELECT "Id", "Name", "Type", "LocalRoot", "S3Bucket"
FROM "StorageBackends"
WHERE "Id" = @StorageBackendId
  AND "IsDeleted" = false
  AND "IsEnabled" = true
LIMIT 1;"""

    [<Literal>]
    let private activeTrackFileExistsSql = """SELECT EXISTS (
    SELECT 1
    FROM "TrackFiles"
    WHERE "IsDeleted" = false
      AND "StorageBackendId" IS NOT DISTINCT FROM @StorageBackendId
      AND "StoragePath" = @StoragePath
);"""

    [<Literal>]
    let private insertTrackSql = """INSERT INTO "Tracks" (
    "Id", "Title", "Artist", "Album", "DurationMs", "CoverImageUrl",
    "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (@TrackId, @Title, @Artist, NULL, NULL, NULL, false, @DiscoveredAtUtc, @DiscoveredAtUtc);"""

    [<Literal>]
    let private insertTrackFileSql = """INSERT INTO "TrackFiles" (
    "Id", "TrackId", "StorageBackendId", "StoragePath", "CachePath", "ContentType", "SizeBytes", "IsCached",
    "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (
    @TrackFileId, @TrackId, @StorageBackendId, @StoragePath, @CachePath, @ContentType, @SizeBytes, @IsCached,
    false, @DiscoveredAtUtc, @DiscoveredAtUtc
)
ON CONFLICT DO NOTHING
RETURNING "Id";"""

    [<Literal>]
    let private softDeleteUnreferencedTrackSql = """UPDATE "Tracks"
SET "IsDeleted" = true,
    "UpdatedAtUtc" = @DeletedAtUtc
WHERE "Id" = @TrackId
  AND "IsDeleted" = false
  AND NOT EXISTS (
      SELECT 1
      FROM "TrackFiles"
      WHERE "TrackId" = @TrackId
        AND "IsDeleted" = false
  );"""

    let private databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)

    let private addNullableUuid (command: NpgsqlCommand) name value =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Uuid)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value
        parameter |> ignore

    let private addNullableText (command: NpgsqlCommand) name value =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Text)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value
        parameter |> ignore

    let private addNullableInt64 (command: NpgsqlCommand) name value =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Bigint)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value
        parameter |> ignore

    let private readNullableGuid (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetGuid(ordinal))

    let private readNullableString (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetString(ordinal))

    let private readJob (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          StorageBackendId = readNullableGuid reader 1
          ClaimOwner = reader.GetGuid(2)
          ClaimAttempt = reader.GetInt32(3)
          LeaseExpiresAtUtc = reader.GetFieldValue<DateTimeOffset>(4) }

    let private claimJob
        (dataSource: NpgsqlDataSource)
        (jobId: Guid option)
        (claimOwner: Guid)
        (startedAtUtc: DateTimeOffset)
        (leaseExpiresAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<LibraryScanJobRecord option, RepositoryError>> =
        taskResult {
            try
                return!
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction cancellationToken ->
                            taskResult {
                                use command = new NpgsqlCommand(claimNextJobSql, connection, transaction)
                                addNullableUuid command "JobId" jobId
                                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                                command.Parameters.AddWithValue("StartedAtUtc", startedAtUtc) |> ignore
                                command.Parameters.AddWithValue("LeaseExpiresAtUtc", leaseExpiresAtUtc) |> ignore
                                let! reader = command.ExecuteReaderAsync(cancellationToken)
                                use reader = reader
                                let! hasRow = reader.ReadAsync(cancellationToken)
                                return if hasRow then Some(readJob reader) else None
                            })
                        cancellationToken
            with ex ->
                return! Error(databaseError "LibraryScanRepository.claimJob" ex)
        }

    let claimNextJob
        (dataSource: NpgsqlDataSource)
        (claimOwner: Guid)
        (startedAtUtc: DateTimeOffset)
        (leaseExpiresAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<LibraryScanJobRecord option, RepositoryError>> =
        claimJob dataSource None claimOwner startedAtUtc leaseExpiresAtUtc cancellationToken

    let claimJobById
        (dataSource: NpgsqlDataSource)
        (jobId: Guid)
        (claimOwner: Guid)
        (startedAtUtc: DateTimeOffset)
        (leaseExpiresAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<LibraryScanJobRecord option, RepositoryError>> =
        claimJob dataSource (Some jobId) claimOwner startedAtUtc leaseExpiresAtUtc cancellationToken

    let renewJobLease
        (dataSource: NpgsqlDataSource)
        (jobId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (leaseExpiresAtUtc: DateTimeOffset)
        (renewedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(renewJobLeaseSql, connection)
                command.Parameters.AddWithValue("JobId", jobId) |> ignore
                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                command.Parameters.AddWithValue("ClaimAttempt", claimAttempt) |> ignore
                command.Parameters.AddWithValue("LeaseExpiresAtUtc", leaseExpiresAtUtc) |> ignore
                command.Parameters.AddWithValue("RenewedAtUtc", renewedAtUtc) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "LibraryScanRepository.renewJobLease" ex)
        }

    let completeJobInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (jobId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (finishedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(completeJobSql, connection, transaction)
                command.Parameters.AddWithValue("JobId", jobId) |> ignore
                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                command.Parameters.AddWithValue("ClaimAttempt", claimAttempt) |> ignore
                command.Parameters.AddWithValue("FinishedAtUtc", finishedAtUtc) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "LibraryScanRepository.completeJobInTransaction" ex)
        }

    let completeJob
        (dataSource: NpgsqlDataSource)
        (jobId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (finishedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                completeJobInTransaction connection transaction jobId claimOwner claimAttempt finishedAtUtc cancellationToken)
            cancellationToken

    let failJobInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (jobId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (finishedAtUtc: DateTimeOffset)
        (failureReason: string)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(failJobSql, connection, transaction)
                command.Parameters.AddWithValue("JobId", jobId) |> ignore
                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                command.Parameters.AddWithValue("ClaimAttempt", claimAttempt) |> ignore
                command.Parameters.AddWithValue("FinishedAtUtc", finishedAtUtc) |> ignore
                command.Parameters.AddWithValue("FailureReason", failureReason) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "LibraryScanRepository.failJobInTransaction" ex)
        }

    let failJob
        (dataSource: NpgsqlDataSource)
        (jobId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (finishedAtUtc: DateTimeOffset)
        (failureReason: string)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                failJobInTransaction connection transaction jobId claimOwner claimAttempt finishedAtUtc failureReason cancellationToken)
            cancellationToken

    let getStorageBackend
        (dataSource: NpgsqlDataSource)
        (storageBackendId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<StorageBackendRecord option, RepositoryError>> =
        taskResult {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(getStorageBackendSql, connection)
                command.Parameters.AddWithValue("StorageBackendId", storageBackendId) |> ignore
                let! reader = command.ExecuteReaderAsync(cancellationToken)
                use reader = reader
                let! hasRow = reader.ReadAsync(cancellationToken)

                if hasRow then
                    return
                        Some
                            { Id = Some(reader.GetGuid(0))
                              Name = reader.GetString(1)
                              Type = reader.GetString(2)
                              LocalRoot = readNullableString reader 3
                              S3Bucket = readNullableString reader 4 }
                else
                    return None
            with ex ->
                return! Error(databaseError "LibraryScanRepository.getStorageBackend" ex)
        }

    let activeTrackFileExists
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (storageBackendId: Guid option)
        (storagePath: string)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(activeTrackFileExistsSql, connection, transaction)
                addNullableUuid command "StorageBackendId" storageBackendId
                command.Parameters.AddWithValue("StoragePath", storagePath) |> ignore
                let! exists = command.ExecuteScalarAsync(cancellationToken)
                return Convert.ToBoolean(exists)
            with ex ->
                return! Error(databaseError "LibraryScanRepository.activeTrackFileExists" ex)
        }

    let insertDiscoveredTrackInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (trackFile: DiscoveredTrackFile)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use trackCommand = new NpgsqlCommand(insertTrackSql, connection, transaction)
                trackCommand.Parameters.AddWithValue("TrackId", trackFile.TrackId) |> ignore
                trackCommand.Parameters.AddWithValue("Title", trackFile.Title) |> ignore
                trackCommand.Parameters.AddWithValue("Artist", trackFile.Artist) |> ignore
                trackCommand.Parameters.AddWithValue("DiscoveredAtUtc", trackFile.DiscoveredAtUtc) |> ignore
                let! _ = trackCommand.ExecuteNonQueryAsync(cancellationToken)

                use trackFileCommand = new NpgsqlCommand(insertTrackFileSql, connection, transaction)
                trackFileCommand.Parameters.AddWithValue("TrackFileId", trackFile.TrackFileId) |> ignore
                trackFileCommand.Parameters.AddWithValue("TrackId", trackFile.TrackId) |> ignore
                addNullableUuid trackFileCommand "StorageBackendId" trackFile.StorageBackendId
                trackFileCommand.Parameters.AddWithValue("StoragePath", trackFile.StoragePath) |> ignore
                addNullableText trackFileCommand "CachePath" trackFile.CachePath
                addNullableText trackFileCommand "ContentType" trackFile.ContentType
                addNullableInt64 trackFileCommand "SizeBytes" trackFile.SizeBytes
                trackFileCommand.Parameters.AddWithValue("IsCached", trackFile.IsCached) |> ignore
                trackFileCommand.Parameters.AddWithValue("DiscoveredAtUtc", trackFile.DiscoveredAtUtc) |> ignore
                let! inserted = trackFileCommand.ExecuteScalarAsync(cancellationToken)

                match inserted with
                | null
                | :? DBNull ->
                    use softDeleteCommand = new NpgsqlCommand(softDeleteUnreferencedTrackSql, connection, transaction)
                    softDeleteCommand.Parameters.AddWithValue("TrackId", trackFile.TrackId) |> ignore
                    softDeleteCommand.Parameters.AddWithValue("DeletedAtUtc", trackFile.DiscoveredAtUtc) |> ignore
                    let! _ = softDeleteCommand.ExecuteNonQueryAsync(cancellationToken)
                    return false
                | _ -> return true
            with ex ->
                return! Error(databaseError "LibraryScanRepository.insertDiscoveredTrackInTransaction" ex)
        }
