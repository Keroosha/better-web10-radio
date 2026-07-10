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
