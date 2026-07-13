namespace Web10.Radio.Database.Repositories

open System
open Dodo.Primitives
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

type DiscoveredCover =
    { CachePath: string
      ContentType: string
      SizeBytes: int64
      Sha256: string }

type DiscoveredTrackFile =
    { TrackId: Guid
      TrackFileId: Guid
      StorageBackendId: Guid option
      StoragePath: string
      CachePath: string option
      IsCached: bool
      Title: string
      Artist: string
      Album: string option
      DurationMs: int option
      MetadataSource: string
      Cover: DiscoveredCover option
      ContentType: string option
      SizeBytes: int64 option
      DiscoveredAtUtc: DateTimeOffset }

type DiscoveredTrackIdentity =
    { TrackId: Guid
      TrackFileId: Guid }

[<RequireQualifiedAccess>]
type DiscoveredTrackResult =
    | Created of DiscoveredTrackIdentity
    | Updated of DiscoveredTrackIdentity

type ActiveTrackFileState =
    { TrackId: Guid
      TrackFileId: Guid
      IsCached: bool }

type LibraryScanJobStatusRecord =
    { Id: Guid
      StorageBackendId: Guid option
      Status: string
      DiscoveredCount: int
      RequestedAtUtc: DateTimeOffset
      StartedAtUtc: DateTimeOffset option
      FinishedAtUtc: DateTimeOffset option
      FailureReason: string option }

type CreateOrGetActiveLibraryScanJobResult =
    | Created of LibraryScanJobStatusRecord
    | Existing of LibraryScanJobStatusRecord
    | StorageBackendNotFound

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
    let private getStorageBackendForManagementSql = """SELECT "Id", "Name", "Type", "LocalRoot", "S3Bucket"
FROM "StorageBackends"
WHERE "Id" = @StorageBackendId
  AND "IsDeleted" = false
LIMIT 1;"""
    [<Literal>]
    let private activeJobForStorageBackendSql = """SELECT
    "Id", "StorageBackendId", "Status", "DiscoveredCount", "RequestedAtUtc", "StartedAtUtc", "FinishedAtUtc", "FailureReason"
FROM "LibraryScanJobs"
WHERE "IsDeleted" = false
  AND "Status" IN ('Queued', 'Running')
  AND "StorageBackendId" IS NOT DISTINCT FROM @StorageBackendId
ORDER BY "RequestedAtUtc" ASC, "CreatedAtUtc" ASC, "Id" ASC
LIMIT 1
FOR UPDATE;"""

    [<Literal>]
    let private insertActiveJobSql = """INSERT INTO "LibraryScanJobs" (
    "Id", "StorageBackendId", "Status", "DiscoveredCount", "RequestedAtUtc", "StartedAtUtc", "FinishedAtUtc", "FailureReason",
    "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
SELECT
    @JobId, @StorageBackendId, 'Queued', 0, @RequestedAtUtc, NULL, NULL, NULL,
    false, @RequestedAtUtc, @RequestedAtUtc
WHERE @StorageBackendId IS NULL
   OR EXISTS (
       SELECT 1
       FROM "StorageBackends"
       WHERE "Id" = @StorageBackendId
         AND "IsDeleted" = false
         AND "IsEnabled" = true
   )
ON CONFLICT DO NOTHING
RETURNING "Id", "StorageBackendId", "Status", "DiscoveredCount", "RequestedAtUtc", "StartedAtUtc", "FinishedAtUtc", "FailureReason";"""

    [<Literal>]
    let private activeStorageBackendExistsSql = """SELECT EXISTS (
    SELECT 1
    FROM "StorageBackends"
    WHERE "Id" = @StorageBackendId
      AND "IsDeleted" = false
      AND "IsEnabled" = true
);"""

    [<Literal>]
    let private getJobStatusSql = """SELECT
    "Id", "StorageBackendId", "Status", "DiscoveredCount", "RequestedAtUtc", "StartedAtUtc", "FinishedAtUtc", "FailureReason"
FROM "LibraryScanJobs"
WHERE "Id" = @JobId
  AND "IsDeleted" = false
LIMIT 1;"""

    [<Literal>]
    let private incrementDiscoveredCountSql = """UPDATE "LibraryScanJobs"
SET "DiscoveredCount" = "DiscoveredCount" + 1,
    "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "Id" = @JobId
  AND "IsDeleted" = false
  AND "Status" = 'Running'
  AND "ClaimOwner" = @ClaimOwner
  AND "ClaimAttempt" = @ClaimAttempt;"""

    [<Literal>]
    let private activeTrackFileSql = """SELECT "Id", "TrackId"
FROM "TrackFiles"
WHERE "IsDeleted" = false
  AND "StorageBackendId" IS NOT DISTINCT FROM @StorageBackendId
  AND "StoragePath" = @StoragePath
FOR UPDATE;"""

    [<Literal>]
    let private activeTrackFileStateSql = """SELECT "TrackId", "Id", "IsCached"
FROM "TrackFiles"
WHERE "IsDeleted" = false
  AND "StorageBackendId" IS NOT DISTINCT FROM @StorageBackendId
  AND "StoragePath" = @StoragePath
LIMIT 1;"""

    [<Literal>]
    let private insertTrackSql = """INSERT INTO "Tracks" (
    "Id", "Title", "Artist", "Album", "DurationMs", "MetadataSource",
    "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
VALUES (@TrackId, @Title, @Artist, @Album, @DurationMs, @MetadataSource,
        false, @DiscoveredAtUtc, @DiscoveredAtUtc);"""

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
    let private addNullableInt32 (command: NpgsqlCommand) name value =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Integer)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value
        parameter |> ignore

    let private readNullableGuid (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetGuid(ordinal))

    let private readNullableString (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetString(ordinal))

    let private readNullableDateTimeOffset (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetFieldValue<DateTimeOffset>(ordinal))

    let private readJobStatus (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          StorageBackendId = readNullableGuid reader 1
          Status = reader.GetString(2)
          DiscoveredCount = reader.GetInt32(3)
          RequestedAtUtc = reader.GetFieldValue<DateTimeOffset>(4)
          StartedAtUtc = readNullableDateTimeOffset reader 5
          FinishedAtUtc = readNullableDateTimeOffset reader 6
          FailureReason = readNullableString reader 7 }

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

    let getStorageBackendForManagement
        (dataSource: NpgsqlDataSource)
        (storageBackendId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<StorageBackendRecord option, RepositoryError>> =
        taskResult {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(getStorageBackendForManagementSql, connection)
                command.Parameters.AddWithValue("StorageBackendId", storageBackendId) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! hasRow = reader.ReadAsync(cancellationToken)
                if hasRow then
                    return Some { Id = Some(reader.GetGuid(0)); Name = reader.GetString(1); Type = reader.GetString(2); LocalRoot = readNullableString reader 3; S3Bucket = readNullableString reader 4 }
                else
                    return None
            with ex ->
                return! Error(databaseError "LibraryScanRepository.getStorageBackendForManagement" ex)
        }

    let private tryReadActiveJobInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (storageBackendId: Guid option)
        (cancellationToken: CancellationToken)
        : Task<Result<LibraryScanJobStatusRecord option, RepositoryError>> =
        taskResult {
            try

                use command = new NpgsqlCommand(activeJobForStorageBackendSql, connection, transaction)
                addNullableUuid command "StorageBackendId" storageBackendId
                let! reader = command.ExecuteReaderAsync(cancellationToken)
                use reader = reader
                let! hasRow = reader.ReadAsync(cancellationToken)
                return if hasRow then Some(readJobStatus reader) else None
            with ex ->
                return! Error(databaseError "LibraryScanRepository.tryReadActiveJobInTransaction" ex)
        }

    let private activeStorageBackendExistsInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (storageBackendId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(activeStorageBackendExistsSql, connection, transaction)
                command.Parameters.AddWithValue("StorageBackendId", storageBackendId) |> ignore
                let! exists = command.ExecuteScalarAsync(cancellationToken)
                return Convert.ToBoolean(exists)
            with ex ->
                return! Error(databaseError "LibraryScanRepository.activeStorageBackendExistsInTransaction" ex)
        }

    let createOrGetActiveJob
        (dataSource: NpgsqlDataSource)
        (candidateJobId: Guid)
        (storageBackendId: Guid option)
        (requestedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<CreateOrGetActiveLibraryScanJobResult, RepositoryError>> =
        taskResult {
            try
                return!
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction cancellationToken ->
                            taskResult {
                                let! backendIsAvailable =
                                    match storageBackendId with
                                    | Some backendId ->
                                        activeStorageBackendExistsInTransaction connection transaction backendId cancellationToken
                                    | None -> Task.FromResult(Ok true)

                                if not backendIsAvailable then
                                    return StorageBackendNotFound
                                else
                                    let! existing = tryReadActiveJobInTransaction connection transaction storageBackendId cancellationToken

                                    match existing with
                                    | Some job -> return Existing job
                                    | None ->
                                        use insertCommand = new NpgsqlCommand(insertActiveJobSql, connection, transaction)
                                        insertCommand.Parameters.AddWithValue("JobId", candidateJobId) |> ignore
                                        addNullableUuid insertCommand "StorageBackendId" storageBackendId
                                        insertCommand.Parameters.AddWithValue("RequestedAtUtc", requestedAtUtc) |> ignore
                                        let! insertedJob =
                                            task {
                                                let! reader = insertCommand.ExecuteReaderAsync(cancellationToken)
                                                use reader = reader
                                                let! inserted = reader.ReadAsync(cancellationToken)
                                                return if inserted then Some(readJobStatus reader) else None
                                            }

                                        match insertedJob with
                                        | Some job -> return Created job
                                        | None ->
                                            let! winner = tryReadActiveJobInTransaction connection transaction storageBackendId cancellationToken

                                            match winner with
                                            | Some job -> return Existing job
                                            | None ->
                                                match storageBackendId with
                                                | Some backendId ->
                                                    let! exists = activeStorageBackendExistsInTransaction connection transaction backendId cancellationToken

                                                    if not exists then
                                                        return StorageBackendNotFound
                                                    else
                                                        return! Error(DatabaseError("LibraryScanRepository.createOrGetActiveJob", "active scan job disappeared before the unique-race winner could be read"))
                                                | None ->
                                                    return! Error(DatabaseError("LibraryScanRepository.createOrGetActiveJob", "active default scan job disappeared before the unique-race winner could be read"))
                            })
                        cancellationToken
            with ex ->
                return! Error(databaseError "LibraryScanRepository.createOrGetActiveJob" ex)
        }

    let getJobStatus
        (dataSource: NpgsqlDataSource)
        (jobId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<LibraryScanJobStatusRecord option, RepositoryError>> =
        taskResult {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(getJobStatusSql, connection)
                command.Parameters.AddWithValue("JobId", jobId) |> ignore
                let! reader = command.ExecuteReaderAsync(cancellationToken)
                use reader = reader
                let! hasRow = reader.ReadAsync(cancellationToken)
                return if hasRow then Some(readJobStatus reader) else None
            with ex ->
                return! Error(databaseError "LibraryScanRepository.getJobStatus" ex)
        }

    let incrementDiscoveredCountInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (jobId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (updatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(incrementDiscoveredCountSql, connection, transaction)
                command.Parameters.AddWithValue("JobId", jobId) |> ignore
                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                command.Parameters.AddWithValue("ClaimAttempt", claimAttempt) |> ignore
                command.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "LibraryScanRepository.incrementDiscoveredCountInTransaction" ex)
        }

    let private tryReadActiveTrackFileInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (storageBackendId: Guid option)
        (storagePath: string)
        (cancellationToken: CancellationToken)
        : Task<Result<DiscoveredTrackIdentity option, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(activeTrackFileSql, connection, transaction)
                addNullableUuid command "StorageBackendId" storageBackendId
                command.Parameters.AddWithValue("StoragePath", storagePath) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)
                return
                    if found then
                        Some
                            { TrackId = reader.GetGuid(1)
                              TrackFileId = reader.GetGuid(0) }
                    else
                        None
            with ex ->
                return! Error(databaseError "LibraryScanRepository.tryReadActiveTrackFileInTransaction" ex)
        }

    let activeTrackFileExists
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (storageBackendId: Guid option)
        (storagePath: string)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            let! existing = tryReadActiveTrackFileInTransaction connection transaction storageBackendId storagePath cancellationToken
            return existing |> Option.isSome
        }

    let tryGetActiveTrackFileState
        (dataSource: NpgsqlDataSource)
        (storageBackendId: Guid option)
        (storagePath: string)
        (cancellationToken: CancellationToken)
        : Task<Result<ActiveTrackFileState option, RepositoryError>> =
        taskResult {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(activeTrackFileStateSql, connection)
                addNullableUuid command "StorageBackendId" storageBackendId
                command.Parameters.AddWithValue("StoragePath", storagePath) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)
                return
                    if found then
                        Some
                            { TrackId = reader.GetGuid(0)
                              TrackFileId = reader.GetGuid(1)
                              IsCached = reader.GetBoolean(2) }
                    else
                        None
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return! Error(databaseError "LibraryScanRepository.tryGetActiveTrackFileState" ex)
        }

    let private updateTrackInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (trackFile: DiscoveredTrackFile)
        (metadataSource: string)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            use command = new NpgsqlCommand("""UPDATE "Tracks"
SET "Title" = CASE WHEN "MetadataSource" = 'Manual' THEN "Title" ELSE @Title END,
    "Artist" = CASE WHEN "MetadataSource" = 'Manual' THEN "Artist" ELSE @Artist END,
    "Album" = CASE WHEN "MetadataSource" = 'Manual' THEN "Album" ELSE @Album END,
    "DurationMs" = @DurationMs,
    "MetadataSource" = CASE WHEN "MetadataSource" = 'Manual' THEN "MetadataSource" ELSE @MetadataSource END,
    "IsDeleted" = false,
    "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "Id" = @TrackId;""", connection, transaction)
            command.Parameters.AddWithValue("TrackId", trackFile.TrackId) |> ignore
            command.Parameters.AddWithValue("Title", trackFile.Title) |> ignore
            command.Parameters.AddWithValue("Artist", trackFile.Artist) |> ignore
            addNullableText command "Album" trackFile.Album
            addNullableInt32 command "DurationMs" trackFile.DurationMs
            command.Parameters.AddWithValue("MetadataSource", metadataSource) |> ignore
            command.Parameters.AddWithValue("UpdatedAtUtc", trackFile.DiscoveredAtUtc) |> ignore
            let! _ = command.ExecuteNonQueryAsync(cancellationToken)
            return ()
        }

    let private updateTrackFileInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (trackFile: DiscoveredTrackFile)
        (trackFileId: Guid)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            use command = new NpgsqlCommand("""UPDATE "TrackFiles"
SET "CachePath" = @CachePath,
    "ContentType" = @ContentType,
    "SizeBytes" = @SizeBytes,
    "IsCached" = @IsCached,
    "IsDeleted" = false,
    "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "Id" = @TrackFileId AND "IsDeleted" = false;""", connection, transaction)
            command.Parameters.AddWithValue("TrackFileId", trackFileId) |> ignore
            addNullableText command "CachePath" trackFile.CachePath
            addNullableText command "ContentType" trackFile.ContentType
            addNullableInt64 command "SizeBytes" trackFile.SizeBytes
            command.Parameters.AddWithValue("IsCached", trackFile.IsCached) |> ignore
            command.Parameters.AddWithValue("UpdatedAtUtc", trackFile.DiscoveredAtUtc) |> ignore
            let! _ = command.ExecuteNonQueryAsync(cancellationToken)
            return ()
        }

    let private applyCoverInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (trackId: Guid)
        (cover: DiscoveredCover option)
        (updatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task =
        task {
            use existing = new NpgsqlCommand("""SELECT "Id", "Source", "Sha256"
FROM "TrackAssets"
WHERE "TrackId" = @TrackId AND "Kind" = 'Cover' AND "IsDeleted" = false
FOR UPDATE;""", connection, transaction)
            existing.Parameters.AddWithValue("TrackId", trackId) |> ignore
            use! reader = existing.ExecuteReaderAsync(cancellationToken)
            let! found = reader.ReadAsync(cancellationToken)
            let existingAsset =
                if found then
                    Some(reader.GetGuid(0), reader.GetString(1), if reader.IsDBNull(2) then None else Some(reader.GetString(2)))
                else
                    None
            reader.Close()

            match cover, existingAsset with
            | None, Some(assetId, source, _) when source = "Embedded" ->
                use delete = new NpgsqlCommand("""UPDATE "TrackAssets"
SET "IsDeleted" = true, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "Id" = @AssetId AND "IsDeleted" = false;""", connection, transaction)
                delete.Parameters.AddWithValue("AssetId", assetId) |> ignore
                delete.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                let! _ = delete.ExecuteNonQueryAsync(cancellationToken)
                return ()
            | None, _ ->
                // Manual and legacy external covers are owned outside the scanner.
                return ()
            | Some value, Some(assetId, source, oldSha) when source = "Manual" ->
                // An administrator-owned cover must never be overwritten by a scan.
                return ()
            | Some value, Some(assetId, source, oldSha) when source = "Embedded" && oldSha = Some value.Sha256 ->
                use update = new NpgsqlCommand("""UPDATE "TrackAssets"
SET "CachePath" = @CachePath, "ContentType" = @ContentType, "SizeBytes" = @SizeBytes,
    "Sha256" = @Sha256, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "Id" = @AssetId AND "IsDeleted" = false;""", connection, transaction)
                update.Parameters.AddWithValue("AssetId", assetId) |> ignore
                update.Parameters.AddWithValue("CachePath", value.CachePath) |> ignore
                update.Parameters.AddWithValue("ContentType", value.ContentType) |> ignore
                update.Parameters.AddWithValue("SizeBytes", value.SizeBytes) |> ignore
                update.Parameters.AddWithValue("Sha256", value.Sha256) |> ignore
                update.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                let! _ = update.ExecuteNonQueryAsync(cancellationToken)
                return ()
            | Some value, Some(assetId, _, _) ->
                use delete = new NpgsqlCommand("""UPDATE "TrackAssets"
SET "IsDeleted" = true, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "Id" = @AssetId AND "IsDeleted" = false;""", connection, transaction)
                delete.Parameters.AddWithValue("AssetId", assetId) |> ignore
                delete.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                let! _ = delete.ExecuteNonQueryAsync(cancellationToken)
                let newAssetId = Uuid.CreateVersion7().ToGuidBigEndian()
                use insert = new NpgsqlCommand("""INSERT INTO "TrackAssets"
    ("Id", "TrackId", "Kind", "Source", "CachePath", "ContentType", "SizeBytes", "Sha256", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@AssetId, @TrackId, 'Cover', 'Embedded', @CachePath, @ContentType, @SizeBytes, @Sha256, false, @UpdatedAtUtc, @UpdatedAtUtc);""", connection, transaction)
                insert.Parameters.AddWithValue("AssetId", newAssetId) |> ignore
                insert.Parameters.AddWithValue("TrackId", trackId) |> ignore
                insert.Parameters.AddWithValue("CachePath", value.CachePath) |> ignore
                insert.Parameters.AddWithValue("ContentType", value.ContentType) |> ignore
                insert.Parameters.AddWithValue("SizeBytes", value.SizeBytes) |> ignore
                insert.Parameters.AddWithValue("Sha256", value.Sha256) |> ignore
                insert.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                let! _ = insert.ExecuteNonQueryAsync(cancellationToken)
                return ()
            | Some value, None ->
                let newAssetId = Uuid.CreateVersion7().ToGuidBigEndian()
                use insert = new NpgsqlCommand("""INSERT INTO "TrackAssets"
    ("Id", "TrackId", "Kind", "Source", "CachePath", "ContentType", "SizeBytes", "Sha256", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@AssetId, @TrackId, 'Cover', 'Embedded', @CachePath, @ContentType, @SizeBytes, @Sha256, false, @UpdatedAtUtc, @UpdatedAtUtc);""", connection, transaction)
                insert.Parameters.AddWithValue("AssetId", newAssetId) |> ignore
                insert.Parameters.AddWithValue("TrackId", trackId) |> ignore
                insert.Parameters.AddWithValue("CachePath", value.CachePath) |> ignore
                insert.Parameters.AddWithValue("ContentType", value.ContentType) |> ignore
                insert.Parameters.AddWithValue("SizeBytes", value.SizeBytes) |> ignore
                insert.Parameters.AddWithValue("Sha256", value.Sha256) |> ignore
                insert.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                let! _ = insert.ExecuteNonQueryAsync(cancellationToken)
                return ()
        }

    let insertDiscoveredTrackInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (trackFile: DiscoveredTrackFile)
        (cancellationToken: CancellationToken)
        : Task<Result<DiscoveredTrackResult, RepositoryError>> =
        taskResult {
            try
                let metadataSource = if trackFile.MetadataSource = "Embedded" then "Embedded" else "Filename"
                let! active =
                    tryReadActiveTrackFileInTransaction
                        connection
                        transaction
                        trackFile.StorageBackendId
                        trackFile.StoragePath
                        cancellationToken

                match active with
                | Some existing ->
                    let! _ = updateTrackInTransaction connection transaction { trackFile with TrackId = existing.TrackId } metadataSource cancellationToken
                    let! _ = updateTrackFileInTransaction connection transaction trackFile existing.TrackFileId cancellationToken
                    let! _ = applyCoverInTransaction connection transaction existing.TrackId trackFile.Cover trackFile.DiscoveredAtUtc cancellationToken
                    return DiscoveredTrackResult.Updated existing
                | None ->
                    use trackCommand = new NpgsqlCommand(insertTrackSql, connection, transaction)
                    trackCommand.Parameters.AddWithValue("TrackId", trackFile.TrackId) |> ignore
                    trackCommand.Parameters.AddWithValue("Title", trackFile.Title) |> ignore
                    trackCommand.Parameters.AddWithValue("Artist", trackFile.Artist) |> ignore
                    addNullableText trackCommand "Album" trackFile.Album
                    addNullableInt32 trackCommand "DurationMs" trackFile.DurationMs
                    trackCommand.Parameters.AddWithValue("MetadataSource", metadataSource) |> ignore
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
                        let! winner =
                            tryReadActiveTrackFileInTransaction
                                connection
                                transaction
                                trackFile.StorageBackendId
                                trackFile.StoragePath
                                cancellationToken
                        match winner with
                        | None -> return! Error(databaseError "LibraryScanRepository.insertDiscoveredTrackInTransaction" (InvalidOperationException("The unique-path winner disappeared.")))
                        | Some existing ->
                            let! _ = updateTrackInTransaction connection transaction { trackFile with TrackId = existing.TrackId } metadataSource cancellationToken
                            let! _ = updateTrackFileInTransaction connection transaction trackFile existing.TrackFileId cancellationToken
                            let! _ = applyCoverInTransaction connection transaction existing.TrackId trackFile.Cover trackFile.DiscoveredAtUtc cancellationToken
                            return DiscoveredTrackResult.Updated existing
                    | _ ->
                        let! _ = applyCoverInTransaction connection transaction trackFile.TrackId trackFile.Cover trackFile.DiscoveredAtUtc cancellationToken
                        return DiscoveredTrackResult.Created { TrackId = trackFile.TrackId; TrackFileId = trackFile.TrackFileId }
            with ex ->
                return! Error(databaseError "LibraryScanRepository.insertDiscoveredTrackInTransaction" ex)
        }
