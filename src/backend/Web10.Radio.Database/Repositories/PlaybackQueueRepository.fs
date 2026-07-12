namespace Web10.Radio.Database.Repositories

open System
open Dodo.Primitives
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Npgsql
open NpgsqlTypes
open Web10.Radio.Database

[<RequireQualifiedAccess>]
type PlaybackControlOutcome<'T> =
    | Applied of 'T
    | NotFound
    | Conflict

type PlaybackFence =
    { QueueItemId: Guid
      ClaimOwner: Guid
      ClaimAttempt: int }

type PlaybackCommandApplied =
    { Fence: PlaybackFence
      Generation: int64
      Action: string }

type ForcePlayNowApplied =
    { QueueItemId: Guid
      TrackId: Guid
      Interrupted: PlaybackFence option }

type ClaimedPlaybackQueueItem =
    { QueueItemId: Guid
      TrackId: Guid option
      PlaylistId: Guid option
      ClaimOwner: Guid
      ClaimAttempt: int
      LeaseExpiresAtUtc: DateTimeOffset }

type PlaybackQueueItem =
    { QueueItemId: Guid
      TrackId: Guid option
      TrackRequestId: Guid option
      PlaylistItemId: Guid option
      PlaylistId: Guid option
      Source: string
      Status: string
      Priority: int64
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
RETURNING q."Id", q."TrackId", q."PlaylistId", q."ClaimOwner", q."ClaimAttempt", q."ClaimLeaseExpiresAtUtc";"""

    [<Literal>]
    let private getOwnedClaimSql = """SELECT "Id", "TrackId", "PlaylistId", "ClaimOwner", "ClaimAttempt", "ClaimLeaseExpiresAtUtc"
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
    let private enqueueNextActivePlaylistItemIfIdleSql = """WITH active_playlists AS (
    SELECT playlist."Id", playlist."Source", playlist."IsJingle", playlist."Interrupt", playlist."Type",
           playlist."Order", playlist."Weight", playlist."AvoidDuplicates", playlist."PlayEverySongs",
           playlist."PlayEveryMinutes", playlist."PlayAtMinute", state."SongsSinceLast",
           state."LastQueuedAtUtc", state."Cursor", state."SelectionCredit"
    FROM "Playlists" AS playlist
    INNER JOIN "PlaylistSchedulerState" AS state ON state."PlaylistId" = playlist."Id"
       AND state."IsDeleted" = false
    WHERE playlist."IsDeleted" = false
      AND playlist."IsActive" = true
      AND (
          NOT EXISTS (
              SELECT 1 FROM "PlaylistSchedules" AS schedule
              WHERE schedule."PlaylistId" = playlist."Id" AND schedule."IsDeleted" = false
          )
          OR EXISTS (
              SELECT 1
              FROM "PlaylistSchedules" AS schedule
              WHERE schedule."PlaylistId" = playlist."Id" AND schedule."IsDeleted" = false
                AND CASE WHEN schedule."StartTime" = schedule."EndTime"
                         THEN ((@EnqueuedAtUtc AT TIME ZONE schedule."TimeZoneId")::time - schedule."StartTime") >= interval '0' AND ((@EnqueuedAtUtc AT TIME ZONE schedule."TimeZoneId")::time - schedule."StartTime") < interval '15 minutes'
                         WHEN schedule."StartTime" < schedule."EndTime"
                         THEN (@EnqueuedAtUtc AT TIME ZONE schedule."TimeZoneId")::time >= schedule."StartTime" AND (@EnqueuedAtUtc AT TIME ZONE schedule."TimeZoneId")::time < schedule."EndTime"
                         ELSE (@EnqueuedAtUtc AT TIME ZONE schedule."TimeZoneId")::time >= schedule."StartTime" OR (@EnqueuedAtUtc AT TIME ZONE schedule."TimeZoneId")::time < schedule."EndTime"
                    END
          )
      )
      AND (
          playlist."Type" = 'General'
          OR (playlist."Type" = 'OncePerSongs' AND state."SongsSinceLast" >= playlist."PlayEverySongs")
          OR (playlist."Type" = 'OncePerMinutes' AND (state."LastQueuedAtUtc" IS NULL OR state."LastQueuedAtUtc" <= @EnqueuedAtUtc - make_interval(mins => playlist."PlayEveryMinutes")))
          OR (playlist."Type" = 'OncePerHour' AND mod((EXTRACT(MINUTE FROM (@EnqueuedAtUtc AT TIME ZONE 'UTC'))::integer - playlist."PlayAtMinute" + 60), 60) < 15 AND (state."LastQueuedAtUtc" IS NULL OR state."LastQueuedAtUtc" <= @EnqueuedAtUtc - interval '30 minutes'))
      )
), manual_candidate AS (
    SELECT playlist."Id" AS "PlaylistId", item."TrackId", item."Id" AS "PlaylistItemId", item."Position",
           CASE WHEN playlist."IsJingle" THEN 'jingle' ELSE 'playlist' END AS "Source",
           CASE WHEN playlist."Interrupt" THEN 1 ELSE 0 END AS "InterruptRank",
           CASE playlist."Type" WHEN 'OncePerHour' THEN 4 WHEN 'OncePerSongs' THEN 3 WHEN 'OncePerMinutes' THEN 2 ELSE 1 END AS "CadenceRank",
           playlist."Order", playlist."Weight", playlist."SelectionCredit", playlist."Type"
    FROM active_playlists AS playlist
    LEFT JOIN LATERAL (
        SELECT prior_item."Position"
        FROM "PlaybackQueue" AS prior_queue
        INNER JOIN "PlaylistItems" AS prior_item ON prior_item."Id" = prior_queue."PlaylistItemId" AND prior_item."IsDeleted" = false
        WHERE prior_queue."PlaylistId" = playlist."Id" AND prior_queue."IsDeleted" = false AND prior_queue."Status" = 'Played'
        ORDER BY prior_queue."FinishedAtUtc" DESC NULLS LAST, prior_queue."UpdatedAtUtc" DESC, prior_queue."Id" DESC
        LIMIT 1
    ) AS last_item ON true
    INNER JOIN LATERAL (
        SELECT candidate."Id", candidate."TrackId", candidate."Position"
        FROM "PlaylistItems" AS candidate
        INNER JOIN "Tracks" AS track ON track."Id" = candidate."TrackId" AND track."IsDeleted" = false
        WHERE candidate."PlaylistId" = playlist."Id"
          AND candidate."IsDeleted" = false
          AND EXISTS (
              SELECT 1 FROM "TrackFiles" AS track_file
              WHERE track_file."TrackId" = candidate."TrackId" AND track_file."IsDeleted" = false
                AND track_file."IsCached" = true AND track_file."CachePath" IS NOT NULL AND btrim(track_file."CachePath") <> ''
          )
        ORDER BY CASE WHEN playlist."Order" = 'Sequential' AND (last_item."Position" IS NULL OR candidate."Position" > last_item."Position") THEN 0 WHEN playlist."Order" = 'Sequential' THEN 1 ELSE 0 END,
                 CASE WHEN playlist."AvoidDuplicates" AND EXISTS (
                     SELECT 1 FROM "PlaybackQueue" AS recent
                     WHERE recent."TrackId" = candidate."TrackId" AND recent."IsDeleted" = false
                       AND recent."Status" = 'Played' AND recent."FinishedAtUtc" > @EnqueuedAtUtc - interval '1 hour'
                 ) THEN 1 ELSE 0 END,
                 candidate."Position" ASC, candidate."Id" ASC
        LIMIT 1
    ) AS item ON true
    WHERE playlist."Source" = 'Manual'
), all_storage_candidate AS (
    SELECT playlist."Id" AS "PlaylistId", track."Id" AS "TrackId", NULL::uuid AS "PlaylistItemId", 0::integer AS "Position",
           CASE WHEN playlist."IsJingle" THEN 'jingle' ELSE 'playlist' END AS "Source",
           CASE WHEN playlist."Interrupt" THEN 1 ELSE 0 END AS "InterruptRank",
           CASE playlist."Type" WHEN 'OncePerHour' THEN 4 WHEN 'OncePerSongs' THEN 3 WHEN 'OncePerMinutes' THEN 2 ELSE 1 END AS "CadenceRank",
           playlist."Order", playlist."Weight", playlist."SelectionCredit", playlist."Type"
    FROM active_playlists AS playlist
    INNER JOIN LATERAL (
        SELECT track."Id"
        FROM "Tracks" AS track
        WHERE track."IsDeleted" = false
          AND EXISTS (
              SELECT 1 FROM "TrackFiles" AS track_file
              WHERE track_file."TrackId" = track."Id" AND track_file."IsDeleted" = false
                AND track_file."IsCached" = true AND track_file."CachePath" IS NOT NULL AND btrim(track_file."CachePath") <> ''
          )
        ORDER BY CASE WHEN playlist."AvoidDuplicates" AND EXISTS (
                     SELECT 1 FROM "PlaybackQueue" AS recent
                     WHERE recent."TrackId" = track."Id" AND recent."IsDeleted" = false
                       AND recent."Status" = 'Played' AND recent."FinishedAtUtc" > @EnqueuedAtUtc - interval '1 hour'
                 ) THEN 1 ELSE 0 END,
                 md5((track."Id"::text || playlist."Cursor"::text)), track."Id" ASC
        LIMIT 1
    ) AS track ON true
    WHERE playlist."Source" = 'AllStorage'
), candidates AS (
    SELECT * FROM manual_candidate
    UNION ALL
    SELECT * FROM all_storage_candidate
), next_item AS (
    SELECT *
    FROM candidates
    ORDER BY "InterruptRank" DESC,
             "CadenceRank" DESC,
             CASE WHEN "Type" = 'General' THEN ("SelectionCredit" + "Weight") ELSE 0 END DESC,
             "Order" ASC, "PlaylistId" ASC, "Position" ASC
    LIMIT 1
), inserted AS (
    INSERT INTO "PlaybackQueue" (
        "Id", "TrackId", "TrackRequestId", "PlaylistItemId", "PlaylistId", "Source", "Status", "Priority",
        "RequestedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
    )
    SELECT @QueueItemId, next_item."TrackId", NULL, next_item."PlaylistItemId", next_item."PlaylistId", next_item."Source", 'Queued',
           CASE WHEN next_item."InterruptRank" = 1 THEN 1000 ELSE 0 END,
           @EnqueuedAtUtc, false, @EnqueuedAtUtc, @EnqueuedAtUtc
    FROM next_item
    WHERE NOT EXISTS (
        SELECT 1 FROM "PlaybackQueue" AS active_queue_item
        WHERE active_queue_item."IsDeleted" = false AND active_queue_item."Status" IN ('Queued', 'Claimed', 'Playing')
    )
    RETURNING "Id", "TrackId", "TrackRequestId", "PlaylistItemId", "PlaylistId", "Source", "Status", "Priority", "RequestedAtUtc"
)
SELECT "Id", "TrackId", "TrackRequestId", "PlaylistItemId", "PlaylistId", "Source", "Status", "Priority", "RequestedAtUtc"
FROM inserted;"""

    [<Literal>]
    let private queuedItemsForUpdateSql = """SELECT "Id", "TrackId", "TrackRequestId", "PlaylistItemId", "PlaylistId", "Source", "Status", "Priority", "RequestedAtUtc"
FROM "PlaybackQueue"
WHERE "IsDeleted" = false
  AND "Status" = 'Queued'
ORDER BY "Priority" DESC, "RequestedAtUtc" ASC, "CreatedAtUtc" ASC, "Id" ASC
FOR UPDATE;"""

    [<Literal>]
    let private currentFenceForUpdateSql = """SELECT "Id", "ClaimOwner", "ClaimAttempt"
FROM "PlaybackQueue"
WHERE "IsDeleted" = false
  AND "Status" IN ('Playing', 'Claimed')
  AND "ClaimOwner" IS NOT NULL
  AND "ClaimAttempt" > 0
ORDER BY CASE WHEN "Status" = 'Playing' THEN 0 ELSE 1 END,
         "StartedAtUtc" DESC NULLS LAST,
         "UpdatedAtUtc" DESC,
         "CreatedAtUtc" DESC,
         "Id" ASC
FOR UPDATE
LIMIT 1;"""

    [<Literal>]
    let private playingFenceForUpdateSql = """SELECT "Id", "ClaimOwner", "ClaimAttempt"
FROM "PlaybackQueue"
WHERE "IsDeleted" = false
  AND "Status" = 'Playing'
  AND "ClaimOwner" IS NOT NULL
  AND "ClaimAttempt" > 0
ORDER BY "StartedAtUtc" DESC NULLS LAST,
         "UpdatedAtUtc" DESC,
         "CreatedAtUtc" DESC,
         "Id" ASC
FOR UPDATE
LIMIT 1;"""

    [<Literal>]
    let private playableTrackSql = """SELECT EXISTS (
    SELECT 1
    FROM "Tracks" AS track
    INNER JOIN "TrackFiles" AS track_file ON track_file."TrackId" = track."Id"
    WHERE track."Id" = @TrackId
      AND track."IsDeleted" = false
      AND track_file."IsDeleted" = false
      AND track_file."IsCached" = true
      AND track_file."CachePath" IS NOT NULL
      AND btrim(track_file."CachePath") <> ''
);"""

    [<Literal>]
    let private maxQueuedPrioritySql = """SELECT COALESCE(MAX("Priority"), 0)
FROM "PlaybackQueue"
WHERE "IsDeleted" = false
  AND "Status" = 'Queued';"""

    [<Literal>]
    let private insertControlCommandSql = """INSERT INTO "PlaybackControlCommands"
    ("Id", "Action", "QueueItemId", "ClaimOwner", "ClaimAttempt", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @Action, @QueueItemId, @ClaimOwner, @ClaimAttempt, false, @AtUtc, @AtUtc)
RETURNING "Generation";"""

    [<Literal>]
    let private markSkippedByFenceSql = """UPDATE "PlaybackQueue"
SET "Status" = 'Played',
    "FinishedAtUtc" = @AtUtc,
    "FailureReason" = NULL,
    "ClaimOwner" = NULL,
    "ClaimLeaseExpiresAtUtc" = NULL,
    "UpdatedAtUtc" = @AtUtc
WHERE "Id" = @QueueItemId
  AND "IsDeleted" = false
  AND "Status" IN ('Playing', 'Claimed')
  AND "ClaimOwner" = @ClaimOwner
  AND "ClaimAttempt" = @ClaimAttempt;"""

    [<Literal>]
    let private insertAdminQueueItemSql = """INSERT INTO "PlaybackQueue"
    ("Id", "TrackId", "TrackRequestId", "PlaylistItemId", "PlaylistId", "Source", "Status", "Priority", "RequestedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @TrackId, NULL, NULL, NULL, 'admin', 'Queued', @Priority, @AtUtc, false, @AtUtc, @AtUtc)
RETURNING "Id", "TrackId", "TrackRequestId", "PlaylistItemId", "PlaylistId", "Source", "Status", "Priority", "RequestedAtUtc";"""

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
          PlaylistId = readNullableGuid reader 2
          ClaimOwner = reader.GetGuid(3)
          ClaimAttempt = reader.GetInt32(4)
          LeaseExpiresAtUtc = reader.GetFieldValue<DateTimeOffset>(5) }

    let private readPlaybackQueueItem (reader: NpgsqlDataReader) =
        { QueueItemId = reader.GetGuid(0)
          TrackId = readNullableGuid reader 1
          TrackRequestId = readNullableGuid reader 2
          PlaylistItemId = readNullableGuid reader 3
          PlaylistId = readNullableGuid reader 4
          Source = reader.GetString(5)
          Status = reader.GetString(6)
          Priority = reader.GetInt64(7)
          RequestedAtUtc = reader.GetFieldValue<DateTimeOffset>(8) }

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
                use cadence = new NpgsqlCommand("""WITH completed AS (
    SELECT "PlaylistId" FROM "PlaybackQueue" WHERE "Id" = @QueueItemId AND "Source" = 'playlist'
)
UPDATE "PlaylistSchedulerState" AS state
SET "SongsSinceLast" = state."SongsSinceLast" + 1,
    "LastPlayedAtUtc" = CASE WHEN state."PlaylistId" = completed."PlaylistId" THEN @FinishedAtUtc ELSE state."LastPlayedAtUtc" END,
    "UpdatedAtUtc" = @FinishedAtUtc
FROM completed
WHERE state."IsDeleted" = false;""", connection, transaction)
                cadence.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                cadence.Parameters.AddWithValue("FinishedAtUtc", finishedAtUtc) |> ignore
                let! _ = cadence.ExecuteNonQueryAsync(cancellationToken)
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

                use stateCommand = new NpgsqlCommand("""INSERT INTO "PlaylistSchedulerState" ("PlaylistId", "ShuffleSeed", "CreatedAtUtc", "UpdatedAtUtc")
SELECT "Id", gen_random_uuid(), @EnqueuedAtUtc, @EnqueuedAtUtc
FROM "Playlists"
WHERE "IsDeleted" = false AND "IsActive" = true
ON CONFLICT ("PlaylistId") DO NOTHING;""", connection, transaction)
                stateCommand.Parameters.AddWithValue("EnqueuedAtUtc", enqueuedAtUtc) |> ignore
                let! _ = stateCommand.ExecuteNonQueryAsync(cancellationToken)

                use command = new NpgsqlCommand(enqueueNextActivePlaylistItemIfIdleSql, connection, transaction)
                command.Parameters.AddWithValue("QueueItemId", candidateQueueItemId) |> ignore
                command.Parameters.AddWithValue("EnqueuedAtUtc", enqueuedAtUtc) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! hasRow = reader.ReadAsync(cancellationToken)
                let item = if hasRow then Some(readPlaybackQueueItem reader) else None
                reader.Close()
                match item with
                | None -> return None
                | Some value ->
                    let! playlistId = value.PlaylistId |> Result.requireSome (DatabaseError("PlaybackQueueRepository.enqueueNextActivePlaylistItemIfIdleInTransaction", "Scheduler item has no playlist id."))
                    use update = new NpgsqlCommand("""UPDATE "PlaylistSchedulerState"
SET "SongsSinceLast" = 0,
    "LastQueuedAtUtc" = @EnqueuedAtUtc,
    "Cursor" = "Cursor" + 1,
    "SelectionCredit" = CASE WHEN EXISTS (SELECT 1 FROM "Playlists" WHERE "Id" = @PlaylistId AND "Type" = 'General') THEN 0 ELSE "SelectionCredit" END,
    "UpdatedAtUtc" = @EnqueuedAtUtc
WHERE "PlaylistId" = @PlaylistId AND "IsDeleted" = false;""", connection, transaction)
                    update.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
                    update.Parameters.AddWithValue("EnqueuedAtUtc", enqueuedAtUtc) |> ignore
                    let! _ = update.ExecuteNonQueryAsync(cancellationToken)
                    return Some value
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

    let private loadQueuedItemsForUpdate
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (cancellationToken: CancellationToken)
        : Task<PlaybackQueueItem list> =
        task {
            use command = new NpgsqlCommand(queuedItemsForUpdateSql, connection, transaction)
            use! reader = command.ExecuteReaderAsync(cancellationToken)
            let values = ResizeArray<PlaybackQueueItem>()
            let mutable reading = true
            while reading do
                let! found = reader.ReadAsync(cancellationToken)
                if found then values.Add(readPlaybackQueueItem reader) else reading <- false
            return List.ofSeq values
        }

    let private loadCurrentFenceForUpdate
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (cancellationToken: CancellationToken)
        : Task<PlaybackFence option> =
        task {
            use command = new NpgsqlCommand(currentFenceForUpdateSql, connection, transaction)
            use! reader = command.ExecuteReaderAsync(cancellationToken)
            let! found = reader.ReadAsync(cancellationToken)
            return
                if found then
                    Some
                        { QueueItemId = reader.GetGuid(0)
                          ClaimOwner = reader.GetGuid(1)
                          ClaimAttempt = reader.GetInt32(2) }
                else None
        }

    let private loadPlayingFenceForUpdate
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (cancellationToken: CancellationToken)
        : Task<PlaybackFence option> =
        task {
            use command = new NpgsqlCommand(playingFenceForUpdateSql, connection, transaction)
            use! reader = command.ExecuteReaderAsync(cancellationToken)
            let! found = reader.ReadAsync(cancellationToken)
            return
                if found then
                    Some
                        { QueueItemId = reader.GetGuid(0)
                          ClaimOwner = reader.GetGuid(1)
                          ClaimAttempt = reader.GetInt32(2) }
                else None
        }

    let private isPlayableTrack
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (trackId: Guid)
        (cancellationToken: CancellationToken)
        : Task<bool> =
        task {
            use command = new NpgsqlCommand(playableTrackSql, connection, transaction)
            command.Parameters.AddWithValue("TrackId", trackId) |> ignore
            let! result = command.ExecuteScalarAsync(cancellationToken)
            return Convert.ToBoolean(result)
        }

    let private insertControlCommand
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (action: string)
        (fence: PlaybackFence)
        (atUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<int64> =
        task {
            use command = new NpgsqlCommand(insertControlCommandSql, connection, transaction)
            command.Parameters.AddWithValue("Id", Uuid.CreateVersion7().ToGuidBigEndian()) |> ignore
            command.Parameters.AddWithValue("Action", action) |> ignore
            command.Parameters.AddWithValue("QueueItemId", fence.QueueItemId) |> ignore
            command.Parameters.AddWithValue("ClaimOwner", fence.ClaimOwner) |> ignore
            command.Parameters.AddWithValue("ClaimAttempt", fence.ClaimAttempt) |> ignore
            command.Parameters.AddWithValue("AtUtc", atUtc) |> ignore
            let! result = command.ExecuteScalarAsync(cancellationToken)
            return Convert.ToInt64(result)
        }

    let private markSkippedByFence
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (fence: PlaybackFence)
        (atUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<int> =
        task {
            use command = new NpgsqlCommand(markSkippedByFenceSql, connection, transaction)
            command.Parameters.AddWithValue("QueueItemId", fence.QueueItemId) |> ignore
            command.Parameters.AddWithValue("ClaimOwner", fence.ClaimOwner) |> ignore
            command.Parameters.AddWithValue("ClaimAttempt", fence.ClaimAttempt) |> ignore
            command.Parameters.AddWithValue("AtUtc", atUtc) |> ignore
            return! command.ExecuteNonQueryAsync(cancellationToken)
        }

    let private lockPlaybackControl
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (cancellationToken: CancellationToken)
        : Task<unit> =
        task {
            use command = new NpgsqlCommand(claimPlaybackAdvisoryLockSql, connection, transaction)
            let! _ = command.ExecuteNonQueryAsync(cancellationToken)
            return ()
        }

    let reorderQueuedInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (queueItemIds: Guid list)
        (nowUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaybackControlOutcome<PlaybackQueueItem list>, RepositoryError>> =
        taskResult {
            try
                do! lockPlaybackControl connection transaction cancellationToken
                let! existing = loadQueuedItemsForUpdate connection transaction cancellationToken
                let duplicateFree = (queueItemIds |> List.distinct |> List.length) = queueItemIds.Length
                let existingIds = existing |> List.map (fun item -> item.QueueItemId) |> Set.ofList
                let requestedIds = queueItemIds |> Set.ofList

                if not duplicateFree || requestedIds <> existingIds then
                    return PlaybackControlOutcome.Conflict
                else
                    let mutable allUpdated = true
                    for index, queueItemId in queueItemIds |> List.indexed do
                        use update = new NpgsqlCommand("""UPDATE "PlaybackQueue"
SET "Priority" = @Priority, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "Id" = @QueueItemId AND "IsDeleted" = false AND "Status" = 'Queued';""", connection, transaction)
                        update.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                        update.Parameters.AddWithValue("Priority", int64 (queueItemIds.Length - index)) |> ignore
                        update.Parameters.AddWithValue("UpdatedAtUtc", nowUtc) |> ignore
                        let! affected = update.ExecuteNonQueryAsync(cancellationToken)
                        if affected <> 1 then allUpdated <- false

                    if not allUpdated then
                        return PlaybackControlOutcome.Conflict
                    else
                        let! ordered = loadQueuedItemsForUpdate connection transaction cancellationToken
                        return PlaybackControlOutcome.Applied ordered
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.reorderQueuedInTransaction" ex)
        }

    let skipCurrentInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (nowUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaybackControlOutcome<PlaybackCommandApplied>, RepositoryError>> =
        taskResult {
            try
                do! lockPlaybackControl connection transaction cancellationToken
                let! current = loadCurrentFenceForUpdate connection transaction cancellationToken

                match current with
                | None -> return PlaybackControlOutcome.Conflict
                | Some fence ->
                    let! affected = markSkippedByFence connection transaction fence nowUtc cancellationToken
                    if affected <> 1 then
                        return PlaybackControlOutcome.Conflict
                    else
                        let! generation = insertControlCommand connection transaction "Skip" fence nowUtc cancellationToken
                        return
                            PlaybackControlOutcome.Applied
                                { Fence = fence
                                  Generation = generation
                                  Action = "Skip" }
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.skipCurrentInTransaction" ex)
        }

    let restartCurrentInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (nowUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaybackControlOutcome<PlaybackCommandApplied>, RepositoryError>> =
        taskResult {
            try
                do! lockPlaybackControl connection transaction cancellationToken
                let! current = loadPlayingFenceForUpdate connection transaction cancellationToken

                match current with
                | None -> return PlaybackControlOutcome.Conflict
                | Some fence ->
                    let! generation = insertControlCommand connection transaction "Restart" fence nowUtc cancellationToken
                    return
                        PlaybackControlOutcome.Applied
                            { Fence = fence
                              Generation = generation
                              Action = "Restart" }
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.restartCurrentInTransaction" ex)
        }

    let forcePlayNowInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (queueItemId: Guid)
        (trackId: Guid)
        (nowUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaybackControlOutcome<ForcePlayNowApplied>, RepositoryError>> =
        taskResult {
            try
                do! lockPlaybackControl connection transaction cancellationToken
                let! playable = isPlayableTrack connection transaction trackId cancellationToken
                if not playable then
                    return PlaybackControlOutcome.NotFound
                else
                    let! current = loadCurrentFenceForUpdate connection transaction cancellationToken
                    let! _ = loadQueuedItemsForUpdate connection transaction cancellationToken
                    use maxCommand = new NpgsqlCommand(maxQueuedPrioritySql, connection, transaction)
                    let! maxValue = maxCommand.ExecuteScalarAsync(cancellationToken)
                    let maxPriority = Convert.ToInt64(maxValue)

                    if maxPriority = Int64.MaxValue then
                        return PlaybackControlOutcome.Conflict
                    else
                        use insert = new NpgsqlCommand(insertAdminQueueItemSql, connection, transaction)
                        insert.Parameters.AddWithValue("Id", queueItemId) |> ignore
                        insert.Parameters.AddWithValue("TrackId", trackId) |> ignore
                        insert.Parameters.AddWithValue("Priority", maxPriority + 1L) |> ignore
                        insert.Parameters.AddWithValue("AtUtc", nowUtc) |> ignore

                        let! insertedItem =
                            task {
                                use! reader = insert.ExecuteReaderAsync(cancellationToken)
                                let! inserted = reader.ReadAsync(cancellationToken)
                                return if inserted then Some(readPlaybackQueueItem reader) else None
                            }

                        match insertedItem with
                        | None -> return PlaybackControlOutcome.Conflict
                        | Some item ->
                            match current with
                            | None ->
                                return
                                    PlaybackControlOutcome.Applied
                                        { QueueItemId = item.QueueItemId
                                          TrackId = trackId
                                          Interrupted = None }
                            | Some fence ->
                                let! affected = markSkippedByFence connection transaction fence nowUtc cancellationToken
                                if affected <> 1 then
                                    return PlaybackControlOutcome.Conflict
                                else
                                    let! _ = insertControlCommand connection transaction "Skip" fence nowUtc cancellationToken
                                    return
                                        PlaybackControlOutcome.Applied
                                            { QueueItemId = item.QueueItemId
                                              TrackId = trackId
                                              Interrupted = Some fence }
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.forcePlayNowInTransaction" ex)
        }

    let reorderQueued
        (dataSource: NpgsqlDataSource)
        (queueItemIds: Guid list)
        (nowUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaybackControlOutcome<PlaybackQueueItem list>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken -> reorderQueuedInTransaction connection transaction queueItemIds nowUtc cancellationToken)
            cancellationToken

    let private removeQueuedItemInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (queueItemId: Guid)
        (nowUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaybackControlOutcome<unit>, RepositoryError>> =
        taskResult {
            try
                do! lockPlaybackControl connection transaction cancellationToken
                use update = new NpgsqlCommand("""UPDATE "PlaybackQueue"
SET "IsDeleted" = true, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "Id" = @QueueItemId AND "IsDeleted" = false AND "Status" = 'Queued';""", connection, transaction)
                update.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                update.Parameters.AddWithValue("UpdatedAtUtc", nowUtc) |> ignore
                let! affected = update.ExecuteNonQueryAsync(cancellationToken)
                if affected = 1 then
                    return PlaybackControlOutcome.Applied()
                else
                    use existing = new NpgsqlCommand("""SELECT "Status" FROM "PlaybackQueue" WHERE "Id" = @QueueItemId AND "IsDeleted" = false;""", connection, transaction)
                    existing.Parameters.AddWithValue("QueueItemId", queueItemId) |> ignore
                    let! status = existing.ExecuteScalarAsync(cancellationToken)
                    if isNull status then return PlaybackControlOutcome.NotFound
                    else return PlaybackControlOutcome.Conflict
            with ex ->
                return! Error(databaseError "PlaybackQueueRepository.removeQueuedItemInTransaction" ex)
        }

    let removeQueuedItem
        (dataSource: NpgsqlDataSource)
        (queueItemId: Guid)
        (nowUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaybackControlOutcome<unit>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken -> removeQueuedItemInTransaction connection transaction queueItemId nowUtc cancellationToken)
            cancellationToken

    let skipCurrent
        (dataSource: NpgsqlDataSource)
        (nowUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaybackControlOutcome<PlaybackCommandApplied>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken -> skipCurrentInTransaction connection transaction nowUtc cancellationToken)
            cancellationToken

    let restartCurrent
        (dataSource: NpgsqlDataSource)
        (nowUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaybackControlOutcome<PlaybackCommandApplied>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken -> restartCurrentInTransaction connection transaction nowUtc cancellationToken)
            cancellationToken

    let forcePlayNow
        (dataSource: NpgsqlDataSource)
        (queueItemId: Guid)
        (trackId: Guid)
        (nowUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaybackControlOutcome<ForcePlayNowApplied>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken -> forcePlayNowInTransaction connection transaction queueItemId trackId nowUtc cancellationToken)
            cancellationToken
