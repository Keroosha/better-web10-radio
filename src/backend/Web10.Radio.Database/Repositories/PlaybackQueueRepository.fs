namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Npgsql
open Web10.Radio.Database

type ClaimedPlaybackQueueItem =
    { QueueItemId: Guid
      TrackId: Guid option
      ClaimOwner: Guid
      ClaimAttempt: int
      LeaseExpiresAtUtc: DateTimeOffset }

type PlaybackQueueItem =
    { QueueItemId: Guid
      TrackId: Guid option
      TrackRequestId: Guid option
      PlaylistItemId: Guid option
      Source: string
      Status: string
      Priority: int
      RequestedAtUtc: DateTimeOffset }

type CurrentPlaybackAssignment =
    { QueueItemId: Guid
      ClaimOwner: Guid
      ClaimAttempt: int
      TrackId: Guid
      CachePath: string
      ContentType: string
      Title: string
      Artist: string
      DurationMs: int }

module PlaybackQueueRepository =
    [<Literal>]
    let private claimPlaybackAdvisoryLockSql = """SELECT pg_advisory_xact_lock(hashtext('web10.radio.playback-claim'));"""

    [<Literal>]
    let private claimNextDetailedSql = """WITH next_item AS (
    SELECT q."Id"
    FROM "PlaybackQueue" q
    WHERE q."IsDeleted" = false
      AND (
          (
              q."Status" IN ('Claimed', 'Playing')
              AND (q."ClaimLeaseExpiresAtUtc" IS NULL OR q."ClaimLeaseExpiresAtUtc" <= @ClaimedAtUtc)
              AND NOT EXISTS (
                  SELECT 1
                  FROM "PlaybackQueue" live
                  WHERE live."IsDeleted" = false
                    AND live."Status" IN ('Claimed', 'Playing')
                    AND live."ClaimLeaseExpiresAtUtc" > @ClaimedAtUtc
              )
          )
          OR (
              q."Status" = 'Queued'
              AND NOT EXISTS (
                  SELECT 1
                  FROM "PlaybackQueue" active
                  WHERE active."IsDeleted" = false
                    AND active."Status" IN ('Claimed', 'Playing')
              )
          )
      )
    ORDER BY
        CASE WHEN q."Status" IN ('Claimed', 'Playing') THEN 0 ELSE 1 END,
        q."Priority" DESC,
        q."RequestedAtUtc" ASC,
        q."CreatedAtUtc" ASC
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
UPDATE "PlaybackQueue" AS q
SET "Status" = 'Claimed',
    "ClaimOwner" = @ClaimOwner,
    "ClaimAttempt" = q."ClaimAttempt" + 1,
    "ClaimLeaseExpiresAtUtc" = @LeaseExpiresAtUtc,
    "ClaimedAtUtc" = @ClaimedAtUtc,
    "StartedAtUtc" = NULL,
    "FinishedAtUtc" = NULL,
    "FailureReason" = NULL,
    "UpdatedAtUtc" = @ClaimedAtUtc
FROM next_item
WHERE q."Id" = next_item."Id"
RETURNING q."Id", q."TrackId", q."ClaimOwner", q."ClaimAttempt", q."ClaimLeaseExpiresAtUtc";"""

    [<Literal>]
    let private getOwnedClaimSql = """SELECT "Id", "TrackId", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc"
FROM "PlaybackQueue"
WHERE "Id" = @QueueItemId
  AND "IsDeleted" = false
  AND "Status" = 'Claimed'
  AND "ClaimOwner" = @ClaimOwner
  AND "ClaimAttempt" = @ClaimAttempt
  AND "ClaimLeaseExpiresAtUtc" > @NowUtc
FOR UPDATE;"""

    [<Literal>]
    let private findCachedTrackFileSql = """SELECT tf."CachePath"
FROM "TrackFiles" AS tf
INNER JOIN "Tracks" AS t ON t."Id" = tf."TrackId" AND t."IsDeleted" = false
WHERE tf."TrackId" = @TrackId
  AND tf."IsCached" = true
  AND tf."CachePath" IS NOT NULL
  AND tf."IsDeleted" = false
ORDER BY tf."UpdatedAtUtc" DESC, tf."CreatedAtUtc" DESC
LIMIT 1;"""

    [<Literal>]
    let private getCurrentAssignmentSql = """SELECT q."Id", q."ClaimOwner", q."ClaimAttempt", t."Id", tf."CachePath",
       COALESCE(tf."ContentType", 'audio/mpeg'),
       COALESCE(t."Title", ''),
       COALESCE(t."Artist", ''),
       COALESCE(t."DurationMs", 0)
FROM "PlaybackQueue" AS q
INNER JOIN "Tracks" AS t
    ON t."Id" = q."TrackId"
   AND t."IsDeleted" = false
INNER JOIN LATERAL (
    SELECT track_file."CachePath", track_file."ContentType"
    FROM "TrackFiles" AS track_file
    WHERE track_file."TrackId" = t."Id"
      AND track_file."IsDeleted" = false
      AND track_file."IsCached" = true
      AND track_file."CachePath" IS NOT NULL
      AND btrim(track_file."CachePath") <> ''
    ORDER BY track_file."UpdatedAtUtc" DESC, track_file."CreatedAtUtc" DESC, track_file."Id" ASC
    LIMIT 1
) AS tf ON true
WHERE q."IsDeleted" = false
  AND q."Status" = 'Playing'
  AND q."ClaimOwner" IS NOT NULL
  AND q."ClaimAttempt" > 0
  AND q."ClaimLeaseExpiresAtUtc" IS NOT NULL
ORDER BY q."StartedAtUtc" DESC NULLS LAST, q."UpdatedAtUtc" DESC, q."CreatedAtUtc" DESC, q."Id" ASC
LIMIT 1;"""

    [<Literal>]
    let private enqueueNextActivePlaylistItemIfIdleSql = """WITH active_playlist AS (
    SELECT playlist."Id"
    FROM "Playlists" AS playlist
    WHERE playlist."IsDeleted" = false
      AND playlist."IsActive" = true
), latest_playlist_position AS (
    SELECT playlist_item."Position"
    FROM "PlaybackQueue" AS queue_item
    INNER JOIN "PlaylistItems" AS playlist_item
        ON playlist_item."Id" = queue_item."PlaylistItemId"
       AND playlist_item."IsDeleted" = false
    INNER JOIN active_playlist AS playlist
        ON playlist."Id" = playlist_item."PlaylistId"
    WHERE queue_item."IsDeleted" = false
      AND queue_item."Source" = 'playlist'
    ORDER BY queue_item."RequestedAtUtc" DESC, queue_item."CreatedAtUtc" DESC, queue_item."Id" DESC
    LIMIT 1
), next_playlist_item AS (
    SELECT playlist_item."Id", playlist_item."TrackId"
    FROM "PlaylistItems" AS playlist_item
    INNER JOIN active_playlist AS playlist
        ON playlist."Id" = playlist_item."PlaylistId"
    INNER JOIN "Tracks" AS track
        ON track."Id" = playlist_item."TrackId"
       AND track."IsDeleted" = false
    WHERE playlist_item."IsDeleted" = false
    ORDER BY
        CASE
            WHEN playlist_item."Position" > COALESCE((SELECT "Position" FROM latest_playlist_position), -1) THEN 0
            ELSE 1
        END,
        playlist_item."Position" ASC,
        playlist_item."Id" ASC
    LIMIT 1
), inserted AS (
    INSERT INTO "PlaybackQueue" (
        "Id", "TrackId", "TrackRequestId", "PlaylistItemId", "Source", "Status", "Priority",
        "RequestedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
    )
    SELECT @QueueItemId, next_item."TrackId", NULL, next_item."Id", 'playlist', 'Queued', 0,
           @EnqueuedAtUtc, false, @EnqueuedAtUtc, @EnqueuedAtUtc
    FROM next_playlist_item AS next_item
    WHERE NOT EXISTS (
        SELECT 1
        FROM "PlaybackQueue" AS active_queue_item
        WHERE active_queue_item."IsDeleted" = false
          AND active_queue_item."Status" IN ('Queued', 'Claimed', 'Playing')
    )
    RETURNING "Id", "TrackId", "TrackRequestId", "PlaylistItemId", "Source", "Status", "Priority", "RequestedAtUtc"
)
SELECT "Id", "TrackId", "TrackRequestId", "PlaylistItemId", "Source", "Status", "Priority", "RequestedAtUtc"
FROM inserted;"""

    [<Literal>]
    let private markPlayingSql = """UPDATE "PlaybackQueue"
SET "Status" = 'Playing',
    "StartedAtUtc" = @StartedAtUtc,
    "ClaimLeaseExpiresAtUtc" = @LeaseExpiresAtUtc,
    "UpdatedAtUtc" = @StartedAtUtc
WHERE "Id" = @QueueItemId
  AND "IsDeleted" = false
  AND "Status" = 'Claimed'
  AND "ClaimOwner" = @ClaimOwner
  AND "ClaimAttempt" = @ClaimAttempt
  AND "ClaimLeaseExpiresAtUtc" > @StartedAtUtc;"""

    [<Literal>]
    let private lockOwnedPlayingClaimSql = """SELECT 1
FROM "PlaybackQueue"
WHERE "Id" = @QueueItemId
  AND "IsDeleted" = false
  AND "Status" = 'Playing'
  AND "ClaimOwner" = @ClaimOwner
  AND "ClaimAttempt" = @ClaimAttempt
  AND "ClaimLeaseExpiresAtUtc" > @NowUtc
FOR UPDATE;"""

    [<Literal>]
    let private renewPlayingLeaseSql = """UPDATE "PlaybackQueue"
SET "ClaimLeaseExpiresAtUtc" = @LeaseExpiresAtUtc,
    "UpdatedAtUtc" = @RenewedAtUtc
WHERE "Id" = @QueueItemId
  AND "IsDeleted" = false
  AND "Status" = 'Playing'
  AND "ClaimOwner" = @ClaimOwner
  AND "ClaimAttempt" = @ClaimAttempt
  AND "ClaimLeaseExpiresAtUtc" > @RenewedAtUtc;"""

    [<Literal>]
    let private markPlayedSql = """UPDATE "PlaybackQueue"
SET "Status" = 'Played',
    "FinishedAtUtc" = @FinishedAtUtc,
    "FailureReason" = NULL,
    "ClaimOwner" = NULL,
    "ClaimLeaseExpiresAtUtc" = NULL,
    "UpdatedAtUtc" = @FinishedAtUtc
WHERE "Id" = @QueueItemId
  AND "IsDeleted" = false
  AND "Status" = 'Playing'
  AND "ClaimOwner" = @ClaimOwner
  AND "ClaimAttempt" = @ClaimAttempt;"""

    [<Literal>]
    let private markFailedSql = """UPDATE "PlaybackQueue"
SET "Status" = 'Failed',
    "FinishedAtUtc" = @FinishedAtUtc,
    "FailureReason" = @FailureReason,
    "ClaimOwner" = NULL,
    "ClaimLeaseExpiresAtUtc" = NULL,
    "UpdatedAtUtc" = @FinishedAtUtc
WHERE "Id" = @QueueItemId
  AND "IsDeleted" = false
  AND "Status" IN ('Claimed', 'Playing')
  AND "ClaimOwner" = @ClaimOwner
  AND "ClaimAttempt" = @ClaimAttempt;"""

    let private databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)

    let private readNullableGuid (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetGuid(ordinal))

    let private readClaimedItem (reader: NpgsqlDataReader) =
        { QueueItemId = reader.GetGuid(0)
          TrackId = readNullableGuid reader 1
          ClaimOwner = reader.GetGuid(2)
          ClaimAttempt = reader.GetInt32(3)
          LeaseExpiresAtUtc = reader.GetFieldValue<DateTimeOffset>(4) }

    let private readPlaybackQueueItem (reader: NpgsqlDataReader) =
        { QueueItemId = reader.GetGuid(0)
          TrackId = readNullableGuid reader 1
          TrackRequestId = readNullableGuid reader 2
          PlaylistItemId = readNullableGuid reader 3
          Source = reader.GetString(4)
          Status = reader.GetString(5)
          Priority = reader.GetInt32(6)
          RequestedAtUtc = reader.GetFieldValue<DateTimeOffset>(7) }

    let private readCurrentPlaybackAssignment (reader: NpgsqlDataReader) =
        { QueueItemId = reader.GetGuid(0)
          ClaimOwner = reader.GetGuid(1)
          ClaimAttempt = reader.GetInt32(2)
          TrackId = reader.GetGuid(3)
          CachePath = reader.GetString(4)
          ContentType = reader.GetString(5)
          Title = reader.GetString(6)
          Artist = reader.GetString(7)
          DurationMs = reader.GetInt32(8) }

    let claimNextDetailedInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (claimOwner: Guid)
        (claimedAtUtc: DateTimeOffset)
        (leaseExpiresAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<ClaimedPlaybackQueueItem option, RepositoryError>> =
        taskResult {
            try
                use lockCommand = new NpgsqlCommand(claimPlaybackAdvisoryLockSql, connection, transaction)
                let! _ = lockCommand.ExecuteNonQueryAsync(cancellationToken)

                use command = new NpgsqlCommand(claimNextDetailedSql, connection, transaction)
                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                command.Parameters.AddWithValue("ClaimedAtUtc", claimedAtUtc) |> ignore
                command.Parameters.AddWithValue("LeaseExpiresAtUtc", leaseExpiresAtUtc) |> ignore
                let! reader = command.ExecuteReaderAsync(cancellationToken)
                use reader = reader
                let! hasRow = reader.ReadAsync(cancellationToken)
                return if hasRow then Some(readClaimedItem reader) else None
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.claimNextDetailedInTransaction" ex)
        }

    let claimNextDetailed
        (dataSource: NpgsqlDataSource)
        (claimOwner: Guid)
        (claimedAtUtc: DateTimeOffset)
        (leaseExpiresAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<ClaimedPlaybackQueueItem option, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                claimNextDetailedInTransaction connection transaction claimOwner claimedAtUtc leaseExpiresAtUtc cancellationToken)
            cancellationToken

    let getOwnedClaimInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (queueItemId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (nowUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<ClaimedPlaybackQueueItem option, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(getOwnedClaimSql, connection, transaction)
                command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                command.Parameters.AddWithValue("ClaimAttempt", claimAttempt) |> ignore
                command.Parameters.AddWithValue("NowUtc", nowUtc) |> ignore
                let! reader = command.ExecuteReaderAsync(cancellationToken)
                use reader = reader
                let! hasRow = reader.ReadAsync(cancellationToken)
                return if hasRow then Some(readClaimedItem reader) else None
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.getOwnedClaimInTransaction" ex)
        }

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
                | null
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

    let getCurrentAssignment
        (dataSource: NpgsqlDataSource)
        (cancellationToken: CancellationToken)
        : Task<Result<CurrentPlaybackAssignment option, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                taskResult {
                    try
                        use command = new NpgsqlCommand(getCurrentAssignmentSql, connection, transaction)
                        let! reader = command.ExecuteReaderAsync(cancellationToken)
                        use reader = reader
                        let! hasRow = reader.ReadAsync(cancellationToken)
                        return if hasRow then Some(readCurrentPlaybackAssignment reader) else None
                    with ex ->
                        return! Error(databaseError "PlaybackQueueRepository.getCurrentAssignment" ex)
                })
            cancellationToken

    let markPlayingInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (queueItemId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (startedAtUtc: DateTimeOffset)
        (leaseExpiresAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(markPlayingSql, connection, transaction)
                command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                command.Parameters.AddWithValue("ClaimAttempt", claimAttempt) |> ignore
                command.Parameters.AddWithValue("StartedAtUtc", startedAtUtc) |> ignore
                command.Parameters.AddWithValue("LeaseExpiresAtUtc", leaseExpiresAtUtc) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.markPlayingInTransaction" ex)
        }

    let markPlaying
        (dataSource: NpgsqlDataSource)
        (queueItemId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (startedAtUtc: DateTimeOffset)
        (leaseExpiresAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                markPlayingInTransaction connection transaction queueItemId claimOwner claimAttempt startedAtUtc leaseExpiresAtUtc cancellationToken)
            cancellationToken

    let lockOwnedPlayingClaimInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (queueItemId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (nowUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(lockOwnedPlayingClaimSql, connection, transaction)
                command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                command.Parameters.AddWithValue("ClaimAttempt", claimAttempt) |> ignore
                command.Parameters.AddWithValue("NowUtc", nowUtc) |> ignore
                let! result = command.ExecuteScalarAsync(cancellationToken)
                return not (isNull result) && not (Convert.IsDBNull result)
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.lockOwnedPlayingClaimInTransaction" ex)
        }

    let renewPlayingLease
        (dataSource: NpgsqlDataSource)
        (queueItemId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (renewedAtUtc: DateTimeOffset)
        (leaseExpiresAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(renewPlayingLeaseSql, connection)
                command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                command.Parameters.AddWithValue("ClaimAttempt", claimAttempt) |> ignore
                command.Parameters.AddWithValue("RenewedAtUtc", renewedAtUtc) |> ignore
                command.Parameters.AddWithValue("LeaseExpiresAtUtc", leaseExpiresAtUtc) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.renewPlayingLease" ex)
        }

    let markPlayedInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (queueItemId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (finishedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(markPlayedSql, connection, transaction)
                command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                command.Parameters.AddWithValue("ClaimAttempt", claimAttempt) |> ignore
                command.Parameters.AddWithValue("FinishedAtUtc", finishedAtUtc) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return affected = 1
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.markPlayedInTransaction" ex)
        }

    let markPlayed
        (dataSource: NpgsqlDataSource)
        (queueItemId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (finishedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                markPlayedInTransaction connection transaction queueItemId claimOwner claimAttempt finishedAtUtc cancellationToken)
            cancellationToken

    let markFailedInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (queueItemId: Guid)
        (claimOwner: Guid)
        (claimAttempt: int)
        (finishedAtUtc: DateTimeOffset)
        (failureReason: string)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use command = new NpgsqlCommand(markFailedSql, connection, transaction)
                command.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                command.Parameters.AddWithValue("ClaimOwner", claimOwner) |> ignore
                command.Parameters.AddWithValue("ClaimAttempt", claimAttempt) |> ignore
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
        (claimOwner: Guid)
        (claimAttempt: int)
        (finishedAtUtc: DateTimeOffset)
        (failureReason: string)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                markFailedInTransaction connection transaction queueItemId claimOwner claimAttempt finishedAtUtc failureReason cancellationToken)
            cancellationToken

    let enqueueNextActivePlaylistItemIfIdleInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (candidateQueueItemId: Guid)
        (enqueuedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaybackQueueItem option, RepositoryError>> =
        taskResult {
            try
                use lockCommand = new NpgsqlCommand(claimPlaybackAdvisoryLockSql, connection, transaction)
                let! _ = lockCommand.ExecuteNonQueryAsync(cancellationToken)

                use command = new NpgsqlCommand(enqueueNextActivePlaylistItemIfIdleSql, connection, transaction)
                command.Parameters.AddWithValue("QueueItemId", candidateQueueItemId) |> ignore
                command.Parameters.AddWithValue("EnqueuedAtUtc", enqueuedAtUtc) |> ignore
                let! reader = command.ExecuteReaderAsync(cancellationToken)
                use reader = reader
                let! hasRow = reader.ReadAsync(cancellationToken)
                return if hasRow then Some(readPlaybackQueueItem reader) else None
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.enqueueNextActivePlaylistItemIfIdleInTransaction" ex)
        }

    let enqueueNextActivePlaylistItemIfIdle
        (dataSource: NpgsqlDataSource)
        (candidateQueueItemId: Guid)
        (enqueuedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaybackQueueItem option, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                enqueueNextActivePlaylistItemIfIdleInTransaction
                    connection
                    transaction
                    candidateQueueItemId
                    enqueuedAtUtc
                    cancellationToken)
            cancellationToken
