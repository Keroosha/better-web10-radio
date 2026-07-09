namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Npgsql
open Web10.Radio.Database

type ClaimedPlaybackQueueItem =
    { QueueItemId: Guid
      TrackId: Guid option }

module PlaybackQueueRepository =
    [<Literal>]
    let private claimNextSql = """WITH next_item AS (
    SELECT "Id"
    FROM "PlaybackQueue"
    WHERE "IsDeleted" = false
      AND "Status" = 'Queued'
    ORDER BY "Priority" DESC, "RequestedAtUtc" ASC, "CreatedAtUtc" ASC
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
UPDATE "PlaybackQueue" AS q
SET "Status" = 'Claimed',
    "ClaimedAtUtc" = @ClaimedAtUtc,
    "UpdatedAtUtc" = @ClaimedAtUtc
FROM next_item
WHERE q."Id" = next_item."Id"
RETURNING q."Id";"""

    let claimNext
        (dataSource: NpgsqlDataSource)
        (claimedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Guid option> =
        DatabaseSession.withTransaction
            dataSource
            (fun connection transaction cancellationToken ->
                task {
                    use command = new NpgsqlCommand(claimNextSql, connection, transaction)
                    command.Parameters.AddWithValue("ClaimedAtUtc", claimedAtUtc) |> ignore
                    let! claimed = command.ExecuteScalarAsync(cancellationToken)

                    match claimed with
                    | null -> return None
                    | :? DBNull -> return None
                    | :? Guid as id -> return Some id
                    | value -> return Some(unbox<Guid> value)
                })
            cancellationToken


    [<Literal>]
    let private claimPlaybackAdvisoryLockSql = """SELECT pg_advisory_xact_lock(hashtext('web10.radio.playback-claim'));"""

    [<Literal>]
    let private claimNextDetailedSql = """WITH next_item AS (
    SELECT "Id"
    FROM "PlaybackQueue"
    WHERE "IsDeleted" = false
      AND "Status" = 'Queued'
      AND NOT EXISTS (
          SELECT 1
          FROM "PlaybackQueue" active
          WHERE active."IsDeleted" = false
            AND active."Status" IN ('Claimed', 'Playing')
      )
    ORDER BY "Priority" DESC, "RequestedAtUtc" ASC, "CreatedAtUtc" ASC
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
UPDATE "PlaybackQueue" AS q
SET "Status" = 'Claimed',
    "ClaimedAtUtc" = @ClaimedAtUtc,
    "UpdatedAtUtc" = @ClaimedAtUtc
FROM next_item
WHERE q."Id" = next_item."Id"
RETURNING q."Id", q."TrackId";"""

    [<Literal>]
    let private findCachedTrackFileSql = """SELECT "CachePath"
FROM "TrackFiles"
WHERE "TrackId" = @TrackId
  AND "IsCached" = true
  AND "CachePath" IS NOT NULL
  AND "IsDeleted" = false
ORDER BY "UpdatedAtUtc" DESC, "CreatedAtUtc" DESC
LIMIT 1;"""

    [<Literal>]
    let private markPlayingSql = """UPDATE "PlaybackQueue"
SET "Status" = 'Playing',
    "StartedAtUtc" = @StartedAtUtc,
    "UpdatedAtUtc" = @StartedAtUtc
WHERE "Id" = @QueueItemId
  AND "IsDeleted" = false
  AND "Status" = 'Claimed';"""

    [<Literal>]
    let private markFailedSql = """UPDATE "PlaybackQueue"
SET "Status" = 'Failed',
    "FinishedAtUtc" = @FinishedAtUtc,
    "FailureReason" = @FailureReason,
    "UpdatedAtUtc" = @FinishedAtUtc
WHERE "Id" = @QueueItemId
  AND "IsDeleted" = false
  AND "Status" IN ('Claimed', 'Playing');"""

    let private databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)

    let private readNullableGuid (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetGuid(ordinal))

    let claimNextDetailedInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (claimedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<ClaimedPlaybackQueueItem option, RepositoryError>> =
        taskResult {
            try
                use lockCommand = new NpgsqlCommand(claimPlaybackAdvisoryLockSql, connection, transaction)
                let! _ = lockCommand.ExecuteNonQueryAsync(cancellationToken)

                use command = new NpgsqlCommand(claimNextDetailedSql, connection, transaction)
                command.Parameters.AddWithValue("ClaimedAtUtc", claimedAtUtc) |> ignore
                let! reader = command.ExecuteReaderAsync(cancellationToken)
                use reader = reader
                let! hasRow = reader.ReadAsync(cancellationToken)

                if hasRow then
                    return
                        Some
                            { QueueItemId = reader.GetGuid(0)
                              TrackId = readNullableGuid reader 1 }
                else
                    return None
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.claimNextDetailedInTransaction" ex)
        }

    let claimNextDetailed
        (dataSource: NpgsqlDataSource)
        (claimedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<ClaimedPlaybackQueueItem option, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken -> claimNextDetailedInTransaction connection transaction claimedAtUtc cancellationToken)
            cancellationToken

    let findCachedTrackFileInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (trackId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<string option, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(findCachedTrackFileSql, connection, transaction)
                command.Parameters.AddWithValue("TrackId", trackId) |> ignore
                let! result = command.ExecuteScalarAsync(cancellationToken)

                match result with
                | null -> return None
                | :? DBNull -> return None
                | :? string as cachePath -> return Some cachePath
                | value -> return Some(unbox<string> value)
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.findCachedTrackFileInTransaction" ex)
        }

    let findCachedTrackFile
        (dataSource: NpgsqlDataSource)
        (trackId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<string option, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken -> findCachedTrackFileInTransaction connection transaction trackId cancellationToken)
            cancellationToken

    let markPlayingInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (queueItemId: Guid)
        (startedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(markPlayingSql, connection, transaction)
                command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                command.Parameters.AddWithValue("StartedAtUtc", startedAtUtc) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.markPlayingInTransaction" ex)
        }

    let markPlaying
        (dataSource: NpgsqlDataSource)
        (queueItemId: Guid)
        (startedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken -> markPlayingInTransaction connection transaction queueItemId startedAtUtc cancellationToken)
            cancellationToken

    let markFailedInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (queueItemId: Guid)
        (finishedAtUtc: DateTimeOffset)
        (failureReason: string)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(markFailedSql, connection, transaction)
                command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                command.Parameters.AddWithValue("FinishedAtUtc", finishedAtUtc) |> ignore
                command.Parameters.AddWithValue("FailureReason", failureReason) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.markFailedInTransaction" ex)
        }

    let markFailed
        (dataSource: NpgsqlDataSource)
        (queueItemId: Guid)
        (finishedAtUtc: DateTimeOffset)
        (failureReason: string)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken -> markFailedInTransaction connection transaction queueItemId finishedAtUtc failureReason cancellationToken)
            cancellationToken