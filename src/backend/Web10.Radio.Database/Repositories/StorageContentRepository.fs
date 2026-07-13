namespace Web10.Radio.Database.Repositories

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Npgsql
open NpgsqlTypes
open Web10.Radio.Database

[<RequireQualifiedAccess>]
type StorageSelectionKind =
    | File
    | Folder

type StorageSelection =
    { PhysicalPath: string
      Kind: StorageSelectionKind }

type StorageTrackFileRecord =
    { TrackFileId: Guid
      TrackId: Guid
      StoragePath: string
      CachePath: string option
      SizeBytes: int64 option
      UpdatedAtUtc: DateTimeOffset }

type StorageTrackRecord =
    { TrackId: Guid
      Title: string
      Artist: string }

type StoragePlaylistMembershipRecord =
    { PlaylistId: Guid
      PlaylistName: string
      TrackId: Guid
      PlaylistItemId: Guid
      Position: int }

type StorageCurrentPlaybackRecord =
    { QueueItemId: Guid
      TrackId: Guid
      ClaimOwner: Guid
      ClaimAttempt: int }

type StorageImpactRecord =
    { TrackFiles: StorageTrackFileRecord list
      Tracks: StorageTrackRecord list
      TracksToDelete: StorageTrackRecord list
      PlaylistMemberships: StoragePlaylistMembershipRecord list
      CurrentPlayback: StorageCurrentPlaybackRecord option
      QueuedTrackIds: Guid list }

type StorageDeleteMutation =
    { DetachedPlaylistItemCount: int
      DeletedTrackCount: int
      PlaybackAdvanced: bool }

[<RequireQualifiedAccess>]
module StorageContentRepository =
    let private addNullableUuid (command: NpgsqlCommand) name value =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Uuid)
        parameter.Value <- (value |> Option.map box |> Option.defaultValue (box DBNull.Value))
        parameter

    let private addSelections (command: NpgsqlCommand) (selections: StorageSelection list) =
        let paths = selections |> List.map _.PhysicalPath |> List.toArray
        let kinds =
            selections
            |> List.map (fun selection -> match selection.Kind with | StorageSelectionKind.File -> "file" | StorageSelectionKind.Folder -> "folder")
            |> List.toArray
        command.Parameters.Add("SelectorPaths", NpgsqlDbType.Array ||| NpgsqlDbType.Text).Value <- paths
        command.Parameters.Add("SelectorKinds", NpgsqlDbType.Array ||| NpgsqlDbType.Text).Value <- kinds

    let private loadTrackFilesSql = """WITH selectors AS (
    SELECT path, kind
    FROM unnest(CAST(@SelectorPaths AS text[]), CAST(@SelectorKinds AS text[])) AS value(path, kind)
)
SELECT tf."Id", tf."TrackId", tf."StoragePath", tf."CachePath", tf."SizeBytes", tf."UpdatedAtUtc"
FROM "TrackFiles" tf
WHERE tf."IsDeleted" = false
  AND tf."StorageBackendId" IS NOT DISTINCT FROM @StorageBackendId
  AND EXISTS (
      SELECT 1 FROM selectors s
      WHERE (s.kind = 'file' AND tf."StoragePath" = s.path)
         OR (s.kind = 'folder' AND left(tf."StoragePath", length(s.path) + 1) = s.path || '/')
  )
ORDER BY tf."TrackId", tf."Id"
FOR UPDATE;"""

    let private loadTracksSql = """SELECT t."Id", t."Title", t."Artist"
FROM "Tracks" t
WHERE t."IsDeleted" = false AND t."Id" = ANY(@TrackIds)
ORDER BY t."Id";"""

    let private loadTracksToDeleteSql = """SELECT t."Id", t."Title", t."Artist"
FROM "Tracks" t
WHERE t."IsDeleted" = false AND t."Id" = ANY(@TrackIds)
  AND NOT EXISTS (
      SELECT 1 FROM "TrackFiles" remaining
      WHERE remaining."TrackId" = t."Id" AND remaining."IsDeleted" = false
        AND NOT EXISTS (
            SELECT 1 FROM unnest(CAST(@SelectorPaths AS text[]), CAST(@SelectorKinds AS text[])) AS s(path, kind)
            WHERE (s.kind = 'file' AND remaining."StorageBackendId" IS NOT DISTINCT FROM @StorageBackendId AND remaining."StoragePath" = s.path)
               OR (s.kind = 'folder' AND remaining."StorageBackendId" IS NOT DISTINCT FROM @StorageBackendId AND left(remaining."StoragePath", length(s.path) + 1) = s.path || '/')
        )
  )
ORDER BY t."Id";"""

    let private loadMembershipsSql = """SELECT item."Id", item."PlaylistId", playlist."Name", item."TrackId", item."Position"
FROM "PlaylistItems" item
INNER JOIN "Playlists" playlist ON playlist."Id" = item."PlaylistId"
WHERE item."IsDeleted" = false AND playlist."IsDeleted" = false
  AND playlist."IsSystem" = false AND playlist."Source" <> 'AllStorage'
  AND item."TrackId" = ANY(@TrackIds)
ORDER BY item."TrackId", item."PlaylistId", item."Position", item."Id";"""

    let private loadCurrentSql = """SELECT q."Id", q."TrackId", q."ClaimOwner", q."ClaimAttempt"
FROM "PlaybackQueue" q
WHERE q."IsDeleted" = false AND q."Status" IN ('Claimed', 'Playing')
  AND q."TrackId" = ANY(@TrackIds)
ORDER BY q."UpdatedAtUtc" DESC, q."Id"
LIMIT 1
FOR UPDATE;"""

    let private loadQueuedSql = """SELECT DISTINCT q."TrackId"
FROM "PlaybackQueue" q
WHERE q."IsDeleted" = false AND q."Status" = 'Queued' AND q."TrackId" = ANY(@TrackIds)
ORDER BY q."TrackId";"""

    let private readTrackFile (reader: NpgsqlDataReader) =
        { TrackFileId = reader.GetGuid(0)
          TrackId = reader.GetGuid(1)
          StoragePath = reader.GetString(2)
          CachePath = if reader.IsDBNull(3) then None else Some(reader.GetString(3))
          SizeBytes = if reader.IsDBNull(4) then None else Some(reader.GetInt64(4))
          UpdatedAtUtc = reader.GetFieldValue<DateTimeOffset>(5) }

    let private readTrack (reader: NpgsqlDataReader) =
        { TrackId = reader.GetGuid(0); Title = reader.GetString(1); Artist = reader.GetString(2) }

    let loadImpactInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (storageBackendId: Guid option)
        (selections: StorageSelection list)
        (cancellationToken: CancellationToken)
        : Task<Result<StorageImpactRecord, RepositoryError>> =
        taskResult {
            try
                use filesCommand = new NpgsqlCommand(loadTrackFilesSql, connection, transaction)
                addNullableUuid filesCommand "StorageBackendId" storageBackendId |> ignore
                addSelections filesCommand selections
                use! filesReader = filesCommand.ExecuteReaderAsync(cancellationToken)
                let files = ResizeArray<StorageTrackFileRecord>()
                while! filesReader.ReadAsync(cancellationToken) do files.Add(readTrackFile filesReader)
                filesReader.Close()
                let trackIds = files |> Seq.map _.TrackId |> Seq.distinct |> Seq.toArray
                let readTracks sql includeSelectors = task {
                    use command = new NpgsqlCommand(sql, connection, transaction)
                    command.Parameters.Add("TrackIds", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid).Value <- trackIds
                    if includeSelectors then
                        addNullableUuid command "StorageBackendId" storageBackendId |> ignore
                        addSelections command selections
                    use! reader = command.ExecuteReaderAsync(cancellationToken)
                    let values = ResizeArray<StorageTrackRecord>()
                    while! reader.ReadAsync(cancellationToken) do values.Add(readTrack reader)
                    return List.ofSeq values
                }
                let! tracks = readTracks loadTracksSql false
                let! tracksToDelete = readTracks loadTracksToDeleteSql true
                use membershipCommand = new NpgsqlCommand(loadMembershipsSql, connection, transaction)
                membershipCommand.Parameters.Add("TrackIds", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid).Value <- tracksToDelete |> List.map _.TrackId |> List.toArray
                use! membershipReader = membershipCommand.ExecuteReaderAsync(cancellationToken)
                let memberships = ResizeArray<StoragePlaylistMembershipRecord>()
                while! membershipReader.ReadAsync(cancellationToken) do
                    memberships.Add({ PlaylistItemId = membershipReader.GetGuid(0); PlaylistId = membershipReader.GetGuid(1); PlaylistName = membershipReader.GetString(2); TrackId = membershipReader.GetGuid(3); Position = membershipReader.GetInt32(4) })
                membershipReader.Close()
                use currentCommand = new NpgsqlCommand(loadCurrentSql, connection, transaction)
                currentCommand.Parameters.Add("TrackIds", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid).Value <- tracksToDelete |> List.map _.TrackId |> List.toArray
                use! currentReader = currentCommand.ExecuteReaderAsync(cancellationToken)
                let! currentFound = currentReader.ReadAsync(cancellationToken)
                let current =
                    if currentFound then
                        Some { QueueItemId = currentReader.GetGuid(0); TrackId = currentReader.GetGuid(1); ClaimOwner = currentReader.GetGuid(2); ClaimAttempt = currentReader.GetInt32(3) }
                    else None
                currentReader.Close()
                use queuedCommand = new NpgsqlCommand(loadQueuedSql, connection, transaction)
                queuedCommand.Parameters.Add("TrackIds", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid).Value <- tracksToDelete |> List.map _.TrackId |> List.toArray
                use! queuedReader = queuedCommand.ExecuteReaderAsync(cancellationToken)
                let queued = ResizeArray<Guid>()
                while! queuedReader.ReadAsync(cancellationToken) do queued.Add(queuedReader.GetGuid(0))
                return { TrackFiles = List.ofSeq files; Tracks = tracks; TracksToDelete = tracksToDelete; PlaylistMemberships = List.ofSeq memberships; CurrentPlayback = current; QueuedTrackIds = List.ofSeq queued }
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return! Error(DatabaseError("StorageContentRepository.loadImpactInTransaction", ex.Message))
        }

    let private compactPlaylist
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (playlistId: Guid)
        (updatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken) =
        task {
            use shift = new NpgsqlCommand("""UPDATE "PlaylistItems" SET "Position" = "Position" + 1000000000, "UpdatedAtUtc" = @UpdatedAtUtc WHERE "PlaylistId" = @PlaylistId AND "IsDeleted" = false;""", connection, transaction)
            shift.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
            shift.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
            let! _ = shift.ExecuteNonQueryAsync(cancellationToken)
            use compact = new NpgsqlCommand("""WITH ordered AS (SELECT "Id", row_number() OVER (ORDER BY "Position", "Id") - 1 AS position FROM "PlaylistItems" WHERE "PlaylistId" = @PlaylistId AND "IsDeleted" = false) UPDATE "PlaylistItems" item SET "Position" = ordered.position, "UpdatedAtUtc" = @UpdatedAtUtc FROM ordered WHERE item."Id" = ordered."Id";""", connection, transaction)
            compact.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
            compact.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
            let! _ = compact.ExecuteNonQueryAsync(cancellationToken)
            return ()
        }

    let applyDeletionInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (impact: StorageImpactRecord)
        (nowUtc: DateTimeOffset)
        (skipCurrent: (NpgsqlConnection -> NpgsqlTransaction -> Guid list -> DateTimeOffset -> CancellationToken -> Task<Result<PlaybackCommandApplied option, RepositoryError>>))
        (appendSkipped: (NpgsqlConnection -> NpgsqlTransaction -> PlaybackCommandApplied -> CancellationToken -> Task<Result<unit, RepositoryError>>))
        (cancellationToken: CancellationToken)
        : Task<Result<StorageDeleteMutation, RepositoryError>> =
        taskResult {
            try
                let deletedTrackIds = impact.TracksToDelete |> List.map _.TrackId |> List.toArray
                let membershipIds = impact.PlaylistMemberships |> List.map _.PlaylistItemId |> List.toArray
                if membershipIds.Length > 0 then
                    use detach = new NpgsqlCommand("UPDATE \"PlaylistItems\" SET \"IsDeleted\" = true, \"UpdatedAtUtc\" = @UpdatedAtUtc WHERE \"Id\" = ANY(@Ids) AND \"IsDeleted\" = false;", connection, transaction)
                    detach.Parameters.Add("Ids", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid).Value <- membershipIds
                    detach.Parameters.AddWithValue("UpdatedAtUtc", nowUtc) |> ignore
                    let! _ = detach.ExecuteNonQueryAsync(cancellationToken)
                    ()
                for playlistId in (impact.PlaylistMemberships |> List.map _.PlaylistId |> List.distinct) do
                    let! _ = compactPlaylist connection transaction playlistId nowUtc cancellationToken
                    ()
                if impact.QueuedTrackIds.Length > 0 then
                    use queued = new NpgsqlCommand("UPDATE \"PlaybackQueue\" SET \"Status\" = 'Failed', \"FailureReason\" = 'Track content deleted from storage', \"FinishedAtUtc\" = @UpdatedAtUtc, \"UpdatedAtUtc\" = @UpdatedAtUtc WHERE \"TrackId\" = ANY(@TrackIds) AND \"Status\" = 'Queued' AND \"IsDeleted\" = false;", connection, transaction)
                    queued.Parameters.Add("TrackIds", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid).Value <- impact.QueuedTrackIds |> List.toArray
                    queued.Parameters.AddWithValue("UpdatedAtUtc", nowUtc) |> ignore
                    let! _ = queued.ExecuteNonQueryAsync(cancellationToken)
                    ()
                let! playback = skipCurrent connection transaction (deletedTrackIds |> Array.toList) nowUtc cancellationToken
                match playback with
                | Some command ->
                    let! _ = appendSkipped connection transaction command cancellationToken
                    ()
                | None -> ()
                if impact.TrackFiles.Length > 0 then
                    use files = new NpgsqlCommand("UPDATE \"TrackFiles\" SET \"IsDeleted\" = true, \"UpdatedAtUtc\" = @UpdatedAtUtc WHERE \"Id\" = ANY(@Ids) AND \"IsDeleted\" = false;", connection, transaction)
                    files.Parameters.Add("Ids", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid).Value <- impact.TrackFiles |> List.map _.TrackFileId |> List.toArray
                    files.Parameters.AddWithValue("UpdatedAtUtc", nowUtc) |> ignore
                    let! _ = files.ExecuteNonQueryAsync(cancellationToken)
                    ()
                if deletedTrackIds.Length > 0 then
                    use tracks = new NpgsqlCommand("UPDATE \"Tracks\" SET \"IsDeleted\" = true, \"UpdatedAtUtc\" = @UpdatedAtUtc WHERE \"Id\" = ANY(@Ids) AND \"IsDeleted\" = false;", connection, transaction)
                    tracks.Parameters.Add("Ids", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid).Value <- deletedTrackIds
                    tracks.Parameters.AddWithValue("UpdatedAtUtc", nowUtc) |> ignore
                    let! _ = tracks.ExecuteNonQueryAsync(cancellationToken)
                    ()
                return { DetachedPlaylistItemCount = membershipIds.Length; DeletedTrackCount = deletedTrackIds.Length; PlaybackAdvanced = playback.IsSome }
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return! Error(DatabaseError("StorageContentRepository.applyDeletionInTransaction", ex.Message))
        }
