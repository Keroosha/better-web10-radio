namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Npgsql
open Web10.Radio.Database

type TrackSummary =
    { Id: Guid
      Title: string
      Artist: string }

type TrackSearchMatch =
    { Id: Guid
      Title: string
      Artist: string
      ExternalUrl: string option
      Similarity: float }
 
type CurrentPlayingTrack =
    { Title: string
      Artist: string
      ExternalUrl: string option }

type ActiveCover =
    { Source: string
      CachePath: string option
      ExternalUrl: string option
      ContentType: string option
      SizeBytes: int64 option
      Sha256: string option }


module TrackRepository =
    [<Literal>]
    let private getActiveByIdSql = """SELECT
    t."Id",
    t."Title",
    t."Artist",
    link."Url"
FROM "Tracks" AS t
LEFT JOIN LATERAL (
    SELECT tl."Url"
    FROM "TrackLinks" AS tl
    WHERE tl."TrackId" = t."Id"
      AND tl."IsDeleted" = false
    ORDER BY CASE WHEN tl."IsPrimary" THEN 0 ELSE 1 END, tl."CreatedAtUtc" ASC
    LIMIT 1
) AS link ON true
WHERE t."Id" = @Id
  AND t."IsDeleted" = false;"""
    [<Literal>]
    let private listActiveSql = """SELECT "Id", "Title", "Artist"
FROM "Tracks"
WHERE "IsDeleted" = false
ORDER BY "Artist" ASC, "Title" ASC;"""

    [<Literal>]
    let private searchActiveSql = """SELECT
    t."Id",
    t."Title",
    t."Artist",
    link."Url",
    GREATEST(
        similarity(lower(t."Title"), lower(trim(@Query))),
        similarity(lower(t."Artist" || ' — ' || t."Title"), lower(trim(@Query)))
    ) AS "Similarity"
FROM "Tracks" AS t
LEFT JOIN LATERAL (
    SELECT tl."Url"
    FROM "TrackLinks" AS tl
    WHERE tl."TrackId" = t."Id"
      AND tl."IsDeleted" = false
    ORDER BY CASE WHEN tl."IsPrimary" THEN 0 ELSE 1 END, tl."CreatedAtUtc" ASC
    LIMIT 1
) AS link ON true
WHERE t."IsDeleted" = false
  AND (
      lower(t."Title") % lower(trim(@Query))
      OR lower(t."Artist" || ' — ' || t."Title") % lower(trim(@Query))
  )
ORDER BY "Similarity" DESC, t."Artist" ASC, t."Title" ASC, t."Id" ASC
LIMIT @Limit;"""
 
    [<Literal>]
    let private currentPlayingSql = """SELECT
    t."Title",
    t."Artist",
    link."Url"
FROM "PlaybackQueue" AS q
INNER JOIN "Tracks" AS t ON t."Id" = q."TrackId" AND t."IsDeleted" = false
LEFT JOIN LATERAL (
    SELECT tl."Url"
    FROM "TrackLinks" AS tl
    WHERE tl."TrackId" = t."Id"

      AND tl."IsDeleted" = false
    ORDER BY CASE WHEN tl."IsPrimary" THEN 0 ELSE 1 END, tl."CreatedAtUtc" ASC
    LIMIT 1
) AS link ON true
WHERE q."IsDeleted" = false
  AND q."Status" = 'Playing'
ORDER BY q."StartedAtUtc" DESC NULLS LAST, q."UpdatedAtUtc" DESC
LIMIT 1;"""

    [<Literal>]
    let private activeCoverSql = """SELECT
    asset."Source", asset."CachePath", asset."ExternalUrl", asset."ContentType", asset."SizeBytes", asset."Sha256"
FROM "Tracks" AS track
INNER JOIN "TrackAssets" AS asset
    ON asset."TrackId" = track."Id"
   AND asset."Kind" = 'Cover'
   AND asset."IsDeleted" = false
WHERE track."Id" = @TrackId
  AND track."IsDeleted" = false
LIMIT 1;"""

    let listActive (dataSource: NpgsqlDataSource) (cancellationToken: CancellationToken) : Task<TrackSummary list> =
        DatabaseSession.withConnection
            dataSource
            (fun connection cancellationToken ->
                task {
                    use command = new NpgsqlCommand(listActiveSql, connection)
                    let! reader = command.ExecuteReaderAsync(cancellationToken)
                    use reader = reader
                    let tracks = ResizeArray<TrackSummary>()
                    let mutable keepReading = true

                    while keepReading do
                        let! hasRow = reader.ReadAsync(cancellationToken)

                        if hasRow then
                            tracks.Add
                                { Id = reader.GetGuid(0)
                                  Title = reader.GetString(1)
                                  Artist = reader.GetString(2) }
                        else
                            keepReading <- false

                    return List.ofSeq tracks
                })
            cancellationToken

    let searchActive
        (dataSource: NpgsqlDataSource)
        (query: string)
        (limit: int)
        (cancellationToken: CancellationToken)
        : Task<Result<TrackSearchMatch list, RepositoryError>> =
        let trimmedQuery =
            if String.IsNullOrWhiteSpace(query) then
                String.Empty
            else
                query.Trim()

        if String.IsNullOrEmpty(trimmedQuery) then
            taskResult { return [] }
        else
            taskResult {
                try
                    use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                    use! transaction = connection.BeginTransactionAsync(cancellationToken)

                    try
                        use readOnlyCommand = new NpgsqlCommand("SET TRANSACTION READ ONLY;", connection, transaction)
                        let! _ = readOnlyCommand.ExecuteNonQueryAsync(cancellationToken)

                        use thresholdCommand =
                            new NpgsqlCommand("SET LOCAL pg_trgm.similarity_threshold = 0.30;", connection, transaction)

                        let! _ = thresholdCommand.ExecuteNonQueryAsync(cancellationToken)

                        let! matches =
                            task {
                                use command = new NpgsqlCommand(searchActiveSql, connection, transaction)
                                command.Parameters.AddWithValue("Query", trimmedQuery) |> ignore
                                command.Parameters.AddWithValue("Limit", min 5 (max 1 limit)) |> ignore
                                let! reader = command.ExecuteReaderAsync(cancellationToken)
                                use reader = reader
                                let matches = ResizeArray<TrackSearchMatch>()
                                let mutable keepReading = true

                                while keepReading do
                                    let! hasRow = reader.ReadAsync(cancellationToken)

                                    if hasRow then
                                        matches.Add
                                            { Id = reader.GetGuid(0)
                                              Title = reader.GetString(1)
                                              Artist = reader.GetString(2)
                                              ExternalUrl = if reader.IsDBNull(3) then None else Some(reader.GetString(3))
                                              Similarity = float (reader.GetFloat(4)) }
                                    else
                                        keepReading <- false

                                return List.ofSeq matches
                            }

                        do! transaction.CommitAsync(cancellationToken)
                        return matches
                    with ex ->
                        try
                            do! transaction.RollbackAsync(CancellationToken.None)
                        with _ ->
                            ()

                        return raise ex
                with
                | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested ->
                    return raise ex
                | ex ->
                    return! Error(DatabaseError("TrackRepository.searchActive", ex.Message))
            }

    let tryGetActiveByIdInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (trackId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<TrackSearchMatch option, RepositoryError>> =
        task {
            try
                use command = new NpgsqlCommand(getActiveByIdSql, connection, transaction)
                command.Parameters.AddWithValue("Id", trackId) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)

                return
                    Ok(
                        if found then
                            Some
                                { Id = reader.GetGuid(0)
                                  Title = reader.GetString(1)
                                  Artist = reader.GetString(2)
                                  ExternalUrl = if reader.IsDBNull(3) then None else Some(reader.GetString(3))
                                  Similarity = 1.0 }
                        else
                            None
                    )
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(DatabaseError("TrackRepository.tryGetActiveByIdInTransaction", ex.Message))
        }

    let tryGetActiveById
        (dataSource: NpgsqlDataSource)
        (trackId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<TrackSearchMatch option, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token -> tryGetActiveByIdInTransaction connection transaction trackId token)
            cancellationToken
 
    let tryGetCurrentPlaying
        (dataSource: NpgsqlDataSource)
        (cancellationToken: CancellationToken)
        : Task<Result<CurrentPlayingTrack option, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(currentPlayingSql, connection)
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)

                return
                    Ok(
                        if found then
                            Some
                                { Title = reader.GetString(0)
                                  Artist = reader.GetString(1)
                                  ExternalUrl = if reader.IsDBNull(2) then None else Some(reader.GetString(2)) }
                        else
                            None
                    )
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(DatabaseError("TrackRepository.tryGetCurrentPlaying", ex.Message))
        }

    let tryGetActiveCover
        (dataSource: NpgsqlDataSource)
        (trackId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<ActiveCover option, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(activeCoverSql, connection)
                command.Parameters.AddWithValue("TrackId", trackId) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)
                return
                    Ok(
                        if found then
                            Some
                                { Source = reader.GetString(0)
                                  CachePath = if reader.IsDBNull(1) then None else Some(reader.GetString(1))
                                  ExternalUrl = if reader.IsDBNull(2) then None else Some(reader.GetString(2))
                                  ContentType = if reader.IsDBNull(3) then None else Some(reader.GetString(3))
                                  SizeBytes = if reader.IsDBNull(4) then None else Some(reader.GetInt64(4))
                                  Sha256 = if reader.IsDBNull(5) then None else Some(reader.GetString(5)) }
                        else
                            None
                    )
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(DatabaseError("TrackRepository.tryGetActiveCover", ex.Message))
        }

    let tryGetActiveTrackIdByStoragePath
        (dataSource: NpgsqlDataSource)
        (storageBackendId: Guid option)
        (storagePath: string)
        (cancellationToken: CancellationToken)
        : Task<Result<Guid option, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("""SELECT tf."TrackId"
FROM "TrackFiles" AS tf
INNER JOIN "Tracks" AS track ON track."Id" = tf."TrackId" AND track."IsDeleted" = false
WHERE tf."StorageBackendId" IS NOT DISTINCT FROM @StorageBackendId
  AND tf."StoragePath" = @StoragePath
  AND tf."IsDeleted" = false
LIMIT 1;""", connection)
                let backendParameter = command.Parameters.Add("StorageBackendId", NpgsqlTypes.NpgsqlDbType.Uuid)
                backendParameter.Value <- storageBackendId |> Option.map box |> Option.defaultValue DBNull.Value
                command.Parameters.AddWithValue("StoragePath", storagePath) |> ignore
                let! value = command.ExecuteScalarAsync(cancellationToken)
                return Ok(if isNull value || value :? DBNull then None else Some(value :?> Guid))
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(DatabaseError("TrackRepository.tryGetActiveTrackIdByStoragePath", ex.Message))
        }

    let isActiveAssetCachePathReferenced
        (dataSource: NpgsqlDataSource)
        (cachePath: string)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("""SELECT EXISTS (
    SELECT 1 FROM "TrackAssets"
    WHERE "CachePath" = @CachePath AND "IsDeleted" = false
);""", connection)
                command.Parameters.AddWithValue("CachePath", cachePath) |> ignore
                let! value = command.ExecuteScalarAsync(cancellationToken)
                return Ok(Convert.ToBoolean(value))
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(DatabaseError("TrackRepository.isActiveAssetCachePathReferenced", ex.Message))
        }
