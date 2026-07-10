namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open Web10.Radio.Database

[<RequireQualifiedAccess>]
type AdminContentMutation<'T> =
    | Applied of 'T
    | NotFound
    | Conflict

type DonationGoal =
    { Id: Guid
      Title: string
      GoalStars: int
      RaisedStars: int }

type DonationGoalToUpsert =
    { Id: Guid
      Title: string
      GoalStars: int
      UpdatedAtUtc: DateTimeOffset }

type SocialLink =
    { Id: Guid
      Kind: string
      Name: string
      Handle: string
      Url: string
      Glyph: string
      Color: string
      QrImageUrl: string
      IsFeatured: bool
      Position: int }

type SocialLinkReplacement =
    { Id: Guid
      Kind: string
      Name: string
      Handle: string option
      Url: string
      Glyph: string option
      Color: string option
      QrImageUrl: string option
      IsFeatured: bool }

type AdminTrack =
    { Id: Guid
      Title: string
      Artist: string
      Album: string
      DurationMs: int
      HasCachedFile: bool }

type AdminQueueItem =
    { Id: Guid
      TrackId: Guid
      Source: string
      Status: string
      Priority: int
      RequestedAtUtc: DateTimeOffset }

type AdminQueueToCreate =
    { Id: Guid
      TrackId: Guid
      RequestedAtUtc: DateTimeOffset }

type PlaylistSummary =
    { Id: Guid
      Name: string
      Description: string option
      IsActive: bool
      ItemCount: int }

type PlaylistToCreate =
    { Id: Guid
      Name: string
      Description: string option
      IsActive: bool
      CreatedAtUtc: DateTimeOffset
      UpdatedAtUtc: DateTimeOffset }

type PlaylistUpdate =
    { Name: string
      Description: string option
      IsActive: bool
      UpdatedAtUtc: DateTimeOffset }

type PlaylistItem =
    { Id: Guid
      TrackId: Guid
      Title: string
      Artist: string
      Position: int }

type PlaylistItemToCreate =
    { Id: Guid
      TrackId: Guid
      CreatedAtUtc: DateTimeOffset }

type PlaylistItemReplacement =
    { Id: Guid
      TrackId: Guid }

type AdditionalStorageBackend =
    { Id: Guid
      Name: string
      Type: string
      LocalRoot: string option
      S3Bucket: string option
      IsEnabled: bool }

type AdditionalStorageBackendReplacement =
    { Id: Guid
      Name: string
      Type: string
      LocalRoot: string option
      S3Bucket: string option
      IsEnabled: bool }

module AdminContentRepository =
    let private databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)
    let private isUniqueViolation (ex: PostgresException) = ex.SqlState = PostgresErrorCodes.UniqueViolation
    let private isForeignKeyViolation (ex: PostgresException) = ex.SqlState = PostgresErrorCodes.ForeignKeyViolation

    let private addNullableText (command: NpgsqlCommand) name (value: string option) =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Text)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value

    let private readDonationGoal (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          Title = reader.GetString(1)
          GoalStars = reader.GetInt32(2)
          RaisedStars = reader.GetInt32(3) }

    let private readSocialLink (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          Kind = reader.GetString(1)
          Name = reader.GetString(2)
          Handle = if reader.IsDBNull(3) then "" else reader.GetString(3)
          Url = reader.GetString(4)
          Glyph = if reader.IsDBNull(5) then "" else reader.GetString(5)
          Color = if reader.IsDBNull(6) then "" else reader.GetString(6)
          QrImageUrl = if reader.IsDBNull(7) then "" else reader.GetString(7)
          IsFeatured = reader.GetBoolean(8)
          Position = reader.GetInt32(9) }

    let private readAdminTrack (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          Title = reader.GetString(1)
          Artist = reader.GetString(2)
          Album = if reader.IsDBNull(3) then "" else reader.GetString(3)
          DurationMs = if reader.IsDBNull(4) then 0 else reader.GetInt32(4)
          HasCachedFile = reader.GetBoolean(5) }

    let private readQueueItem (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          TrackId = reader.GetGuid(1)
          Source = reader.GetString(2)
          Status = reader.GetString(3)
          Priority = reader.GetInt32(4)
          RequestedAtUtc = reader.GetFieldValue<DateTimeOffset>(5) }

    let private readPlaylistSummary (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          Name = reader.GetString(1)
          Description = if reader.IsDBNull(2) then None else Some(reader.GetString(2))
          IsActive = reader.GetBoolean(3)
          ItemCount = reader.GetInt32(4) }

    let private readPlaylistItem (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          TrackId = reader.GetGuid(1)
          Title = reader.GetString(2)
          Artist = reader.GetString(3)
          Position = reader.GetInt32(4) }

    let private readStorageBackend (reader: NpgsqlDataReader) : AdditionalStorageBackend =
        { Id = reader.GetGuid(0)
          Name = reader.GetString(1)
          Type = reader.GetString(2)
          LocalRoot = if reader.IsDBNull(3) then None else Some(reader.GetString(3))
          S3Bucket = if reader.IsDBNull(4) then None else Some(reader.GetString(4))
          IsEnabled = reader.GetBoolean(5) }

    let private readAll (reader: NpgsqlDataReader) (mapper: NpgsqlDataReader -> 'T) (token: CancellationToken) : Task<'T list> =
        task {
            let values = ResizeArray<'T>()
            let mutable reading = true
            while reading do
                let! found = reader.ReadAsync(token)
                if found then values.Add(mapper reader) else reading <- false
            return List.ofSeq values
        }

    let upsertDonationGoal
        (dataSource: NpgsqlDataSource)
        (goal: DonationGoalToUpsert)
        (cancellationToken: CancellationToken)
        : Task<Result<DonationGoal, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("""INSERT INTO "DonationGoals" ("Id", "Title", "GoalStars", "RaisedStars", "IsActive", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @Title, @GoalStars, 0, true, false, @UpdatedAtUtc, @UpdatedAtUtc)
ON CONFLICT ("IsActive") WHERE "IsDeleted" = false AND "IsActive" = true
DO UPDATE SET "Title" = EXCLUDED."Title", "GoalStars" = EXCLUDED."GoalStars", "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc"
RETURNING "Id", "Title", "GoalStars", "RaisedStars";""", connection)
                command.Parameters.AddWithValue("Id", goal.Id) |> ignore
                command.Parameters.AddWithValue("Title", goal.Title) |> ignore
                command.Parameters.AddWithValue("GoalStars", goal.GoalStars) |> ignore
                command.Parameters.AddWithValue("UpdatedAtUtc", goal.UpdatedAtUtc) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)
                return if found then Ok(readDonationGoal reader) else Error(DatabaseError("AdminContentRepository.upsertDonationGoal", "The upsert did not return a goal."))
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminContentRepository.upsertDonationGoal" ex)
        }

    let listSocialLinks (dataSource: NpgsqlDataSource) (cancellationToken: CancellationToken) : Task<Result<SocialLink list, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("""SELECT "Id", "Kind", "Name", "Handle", "Url", "Glyph", "Color", "QrImageUrl", "IsFeatured", "Position"
FROM "SocialLinks" WHERE "IsDeleted" = false ORDER BY "Position" ASC, "Id" ASC;""", connection)
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! values = readAll reader readSocialLink cancellationToken
                return Ok values
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminContentRepository.listSocialLinks" ex)
        }

    let private lookupLiveId (tableName: string) (id: Guid) (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (token: CancellationToken) : Task<bool> =
        task {
            use command = new NpgsqlCommand(sprintf "SELECT \"IsDeleted\" FROM \"%s\" WHERE \"Id\" = @Id FOR UPDATE;" tableName, connection, transaction)
            command.Parameters.AddWithValue("Id", id) |> ignore
            use! reader = command.ExecuteReaderAsync(token)
            let! found = reader.ReadAsync(token)
            return found && not (reader.GetBoolean(0))
        }

    let replaceSocialLinks
        (dataSource: NpgsqlDataSource)
        (links: SocialLinkReplacement list)
        (updatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<SocialLink list>, RepositoryError>> =
        if links |> List.map _.Id |> Set.ofList |> Set.count <> List.length links then
            Task.FromResult(Ok AdminContentMutation.Conflict)
        else
            DatabaseSession.withTransactionResult
                dataSource
                (fun connection transaction token ->
                    task {
                        try
                            let mutable missing = false
                            for link in links do
                                let! exists = lookupLiveId "SocialLinks" link.Id connection transaction token
                                if not exists then
                                    use historical = new NpgsqlCommand("SELECT 1 FROM \"SocialLinks\" WHERE \"Id\" = @Id;", connection, transaction)
                                    historical.Parameters.AddWithValue("Id", link.Id) |> ignore
                                    let! known = historical.ExecuteScalarAsync(token)
                                    if not (isNull known) then missing <- true
                            if missing then return Ok AdminContentMutation.NotFound
                            else
                                for (position, link) in links |> List.indexed do
                                    use command = new NpgsqlCommand("""INSERT INTO "SocialLinks" ("Id", "Kind", "Name", "Handle", "Url", "Glyph", "Color", "QrImageUrl", "IsFeatured", "Position", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @Kind, @Name, @Handle, @Url, @Glyph, @Color, @QrImageUrl, @IsFeatured, @Position, false, @UpdatedAtUtc, @UpdatedAtUtc)
ON CONFLICT ("Id") DO UPDATE SET "Kind" = EXCLUDED."Kind", "Name" = EXCLUDED."Name", "Handle" = EXCLUDED."Handle", "Url" = EXCLUDED."Url", "Glyph" = EXCLUDED."Glyph", "Color" = EXCLUDED."Color", "QrImageUrl" = EXCLUDED."QrImageUrl", "IsFeatured" = EXCLUDED."IsFeatured", "Position" = EXCLUDED."Position", "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc";""", connection, transaction)
                                    command.Parameters.AddWithValue("Id", link.Id) |> ignore
                                    command.Parameters.AddWithValue("Kind", link.Kind) |> ignore
                                    command.Parameters.AddWithValue("Name", link.Name) |> ignore
                                    addNullableText command "Handle" link.Handle
                                    command.Parameters.AddWithValue("Url", link.Url) |> ignore
                                    addNullableText command "Glyph" link.Glyph
                                    addNullableText command "Color" link.Color
                                    addNullableText command "QrImageUrl" link.QrImageUrl
                                    command.Parameters.AddWithValue("IsFeatured", link.IsFeatured) |> ignore
                                    command.Parameters.AddWithValue("Position", position) |> ignore
                                    command.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                                    do! (command.ExecuteNonQueryAsync(token) :> Task)
                                use deleteOmitted = new NpgsqlCommand("""UPDATE "SocialLinks" SET "IsDeleted" = true, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "IsDeleted" = false AND NOT ("Id" = ANY(@Ids));""", connection, transaction)
                                deleteOmitted.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                                deleteOmitted.Parameters.AddWithValue("Ids", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid, links |> List.map _.Id |> List.toArray) |> ignore
                                let! _ = deleteOmitted.ExecuteNonQueryAsync(token)
                                use select = new NpgsqlCommand("""SELECT "Id", "Kind", "Name", "Handle", "Url", "Glyph", "Color", "QrImageUrl", "IsFeatured", "Position"
FROM "SocialLinks" WHERE "IsDeleted" = false ORDER BY "Position" ASC, "Id" ASC;""", connection, transaction)
                                use! reader = select.ExecuteReaderAsync(token)
                                let! values = readAll reader readSocialLink token
                                return Ok(AdminContentMutation.Applied values)
                        with
                        | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                        | :? PostgresException as ex when isUniqueViolation ex -> return Ok AdminContentMutation.Conflict
                        | :? PostgresException as ex when isForeignKeyViolation ex -> return Ok AdminContentMutation.NotFound
                        | ex -> return Error(databaseError "AdminContentRepository.replaceSocialLinks" ex)
                    })
                cancellationToken

    let listActiveTracks
        (dataSource: NpgsqlDataSource)
        (query: string)
        (limit: int)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminTrack list, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("""SELECT track."Id", track."Title", track."Artist", track."Album", track."DurationMs",
       EXISTS (SELECT 1 FROM "TrackFiles" AS file WHERE file."TrackId" = track."Id" AND file."IsDeleted" = false AND file."IsCached" = true AND file."CachePath" IS NOT NULL AND btrim(file."CachePath") <> '')
FROM "Tracks" AS track
WHERE track."IsDeleted" = false
  AND (@Query = '' OR track."Title" ILIKE '%' || @Query || '%' OR track."Artist" ILIKE '%' || @Query || '%' OR COALESCE(track."Album", '') ILIKE '%' || @Query || '%')
ORDER BY track."CreatedAtUtc" DESC, track."Id" ASC
LIMIT @Limit;""", connection)
                command.Parameters.AddWithValue("Query", query) |> ignore
                command.Parameters.AddWithValue("Limit", limit) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! values = readAll reader readAdminTrack cancellationToken
                return Ok values
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminContentRepository.listActiveTracks" ex)
        }

    let enqueueAdminTrack
        (dataSource: NpgsqlDataSource)
        (request: AdminQueueToCreate)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<AdminQueueItem>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        use playable = new NpgsqlCommand("""SELECT 1 FROM "Tracks" AS track
WHERE track."Id" = @TrackId AND track."IsDeleted" = false
  AND EXISTS (SELECT 1 FROM "TrackFiles" AS file WHERE file."TrackId" = track."Id" AND file."IsDeleted" = false AND file."IsCached" = true AND file."CachePath" IS NOT NULL AND btrim(file."CachePath") <> '')
FOR UPDATE;""", connection, transaction)
                        playable.Parameters.AddWithValue("TrackId", request.TrackId) |> ignore
                        let! found = playable.ExecuteScalarAsync(token)
                        if isNull found then return Ok AdminContentMutation.NotFound
                        else
                            use command = new NpgsqlCommand("""INSERT INTO "PlaybackQueue" ("Id", "TrackId", "TrackRequestId", "PlaylistItemId", "Source", "Status", "Priority", "RequestedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @TrackId, NULL, NULL, 'admin', 'Queued', 0, @RequestedAtUtc, false, @RequestedAtUtc, @RequestedAtUtc)
RETURNING "Id", "TrackId", "Source", "Status", "Priority", "RequestedAtUtc";""", connection, transaction)
                            command.Parameters.AddWithValue("Id", request.Id) |> ignore
                            command.Parameters.AddWithValue("TrackId", request.TrackId) |> ignore
                            command.Parameters.AddWithValue("RequestedAtUtc", request.RequestedAtUtc) |> ignore
                            use! reader = command.ExecuteReaderAsync(token)
                            let! inserted = reader.ReadAsync(token)
                            return if inserted then Ok(AdminContentMutation.Applied(readQueueItem reader)) else Error(DatabaseError("AdminContentRepository.enqueueAdminTrack", "The insert did not return a queue item."))
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | :? PostgresException as ex when isUniqueViolation ex -> return Ok AdminContentMutation.Conflict
                    | :? PostgresException as ex when isForeignKeyViolation ex -> return Ok AdminContentMutation.NotFound
                    | ex -> return Error(databaseError "AdminContentRepository.enqueueAdminTrack" ex)
                })
            cancellationToken

    let listPlaylists (dataSource: NpgsqlDataSource) (cancellationToken: CancellationToken) : Task<Result<PlaylistSummary list, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("""SELECT playlist."Id", playlist."Name", playlist."Description", playlist."IsActive", COUNT(item."Id")::integer
FROM "Playlists" AS playlist
LEFT JOIN "PlaylistItems" AS item ON item."PlaylistId" = playlist."Id" AND item."IsDeleted" = false
WHERE playlist."IsDeleted" = false
GROUP BY playlist."Id"
ORDER BY playlist."CreatedAtUtc" DESC, playlist."Id" ASC;""", connection)
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! values = readAll reader readPlaylistSummary cancellationToken
                return Ok values
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminContentRepository.listPlaylists" ex)
        }

    let private deactivateOtherPlaylists retainedId updatedAtUtc connection transaction token =
        task {
            use command = new NpgsqlCommand("""UPDATE "Playlists" SET "IsActive" = false, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "IsDeleted" = false AND "IsActive" = true AND "Id" <> @RetainedId;""", connection, transaction)
            command.Parameters.AddWithValue("RetainedId", retainedId) |> ignore
            command.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
            return! command.ExecuteNonQueryAsync(token)
        }

    let createPlaylist
        (dataSource: NpgsqlDataSource)
        (playlist: PlaylistToCreate)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<PlaylistSummary>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        if playlist.IsActive then let! _ = deactivateOtherPlaylists playlist.Id playlist.UpdatedAtUtc connection transaction token in ()
                        use command = new NpgsqlCommand("""INSERT INTO "Playlists" ("Id", "Name", "Description", "IsActive", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @Name, @Description, @IsActive, false, @CreatedAtUtc, @UpdatedAtUtc)
RETURNING "Id", "Name", "Description", "IsActive", 0;""", connection, transaction)
                        command.Parameters.AddWithValue("Id", playlist.Id) |> ignore
                        command.Parameters.AddWithValue("Name", playlist.Name) |> ignore
                        addNullableText command "Description" playlist.Description
                        command.Parameters.AddWithValue("IsActive", playlist.IsActive) |> ignore
                        command.Parameters.AddWithValue("CreatedAtUtc", playlist.CreatedAtUtc) |> ignore
                        command.Parameters.AddWithValue("UpdatedAtUtc", playlist.UpdatedAtUtc) |> ignore
                        use! reader = command.ExecuteReaderAsync(token)
                        let! found = reader.ReadAsync(token)
                        return if found then Ok(AdminContentMutation.Applied(readPlaylistSummary reader)) else Error(DatabaseError("AdminContentRepository.createPlaylist", "The insert did not return a playlist."))
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | :? PostgresException as ex when isUniqueViolation ex -> return Ok AdminContentMutation.Conflict
                    | ex -> return Error(databaseError "AdminContentRepository.createPlaylist" ex)
                })
            cancellationToken

    let updatePlaylist
        (dataSource: NpgsqlDataSource)
        (playlistId: Guid)
        (update: PlaylistUpdate)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<PlaylistSummary>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        let! exists = lookupLiveId "Playlists" playlistId connection transaction token
                        if not exists then return Ok AdminContentMutation.NotFound
                        else
                            if update.IsActive then let! _ = deactivateOtherPlaylists playlistId update.UpdatedAtUtc connection transaction token in ()
                            use command = new NpgsqlCommand("""UPDATE "Playlists" SET "Name" = @Name, "Description" = @Description, "IsActive" = @IsActive, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "Id" = @Id AND "IsDeleted" = false
RETURNING "Id", "Name", "Description", "IsActive", (SELECT COUNT(*)::integer FROM "PlaylistItems" WHERE "PlaylistId" = "Playlists"."Id" AND "IsDeleted" = false);""", connection, transaction)
                            command.Parameters.AddWithValue("Id", playlistId) |> ignore
                            command.Parameters.AddWithValue("Name", update.Name) |> ignore
                            addNullableText command "Description" update.Description
                            command.Parameters.AddWithValue("IsActive", update.IsActive) |> ignore
                            command.Parameters.AddWithValue("UpdatedAtUtc", update.UpdatedAtUtc) |> ignore
                            use! reader = command.ExecuteReaderAsync(token)
                            let! found = reader.ReadAsync(token)
                            return if found then Ok(AdminContentMutation.Applied(readPlaylistSummary reader)) else Ok AdminContentMutation.NotFound
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | :? PostgresException as ex when isUniqueViolation ex -> return Ok AdminContentMutation.Conflict
                    | ex -> return Error(databaseError "AdminContentRepository.updatePlaylist" ex)
                })
            cancellationToken

    let listPlaylistItems
        (dataSource: NpgsqlDataSource)
        (playlistId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaylistItem list, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("""SELECT item."Id", item."TrackId", track."Title", track."Artist", item."Position"
FROM "PlaylistItems" AS item
INNER JOIN "Playlists" AS playlist ON playlist."Id" = item."PlaylistId" AND playlist."IsDeleted" = false
INNER JOIN "Tracks" AS track ON track."Id" = item."TrackId" AND track."IsDeleted" = false
WHERE item."PlaylistId" = @PlaylistId AND item."IsDeleted" = false
ORDER BY item."Position" ASC, item."Id" ASC;""", connection)
                command.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! values = readAll reader readPlaylistItem cancellationToken
                return Ok values
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminContentRepository.listPlaylistItems" ex)
        }

    let private activeTrackExists trackId connection transaction token =
        task {
            use command = new NpgsqlCommand("SELECT 1 FROM \"Tracks\" WHERE \"Id\" = @Id AND \"IsDeleted\" = false FOR KEY SHARE;", connection, transaction)
            command.Parameters.AddWithValue("Id", trackId) |> ignore
            let! found = command.ExecuteScalarAsync(token)
            return not (isNull found)
        }

    let createPlaylistItem
        (dataSource: NpgsqlDataSource)
        (playlistId: Guid)
        (item: PlaylistItemToCreate)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<PlaylistItem>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        let! playlistExists = lookupLiveId "Playlists" playlistId connection transaction token
                        let! trackExists = activeTrackExists item.TrackId connection transaction token
                        if not playlistExists || not trackExists then return Ok AdminContentMutation.NotFound
                        else
                            use command = new NpgsqlCommand("""INSERT INTO "PlaylistItems" ("Id", "PlaylistId", "TrackId", "Position", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @PlaylistId, @TrackId, (SELECT COALESCE(MAX("Position") + 1, 0) FROM "PlaylistItems" WHERE "PlaylistId" = @PlaylistId AND "IsDeleted" = false), false, @CreatedAtUtc, @CreatedAtUtc)
RETURNING "Id", "TrackId", (SELECT "Title" FROM "Tracks" WHERE "Id" = @TrackId), (SELECT "Artist" FROM "Tracks" WHERE "Id" = @TrackId), "Position";""", connection, transaction)
                            command.Parameters.AddWithValue("Id", item.Id) |> ignore
                            command.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
                            command.Parameters.AddWithValue("TrackId", item.TrackId) |> ignore
                            command.Parameters.AddWithValue("CreatedAtUtc", item.CreatedAtUtc) |> ignore
                            use! reader = command.ExecuteReaderAsync(token)
                            let! found = reader.ReadAsync(token)
                            return if found then Ok(AdminContentMutation.Applied(readPlaylistItem reader)) else Error(DatabaseError("AdminContentRepository.createPlaylistItem", "The insert did not return an item."))
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | :? PostgresException as ex when isUniqueViolation ex -> return Ok AdminContentMutation.Conflict
                    | :? PostgresException as ex when isForeignKeyViolation ex -> return Ok AdminContentMutation.NotFound
                    | ex -> return Error(databaseError "AdminContentRepository.createPlaylistItem" ex)
                })
            cancellationToken

    let replacePlaylistItems
        (dataSource: NpgsqlDataSource)
        (playlistId: Guid)
        (items: PlaylistItemReplacement list)
        (updatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<PlaylistItem list>, RepositoryError>> =
        if items |> List.map _.Id |> Set.ofList |> Set.count <> List.length items then Task.FromResult(Ok AdminContentMutation.Conflict)
        else
            DatabaseSession.withTransactionResult
                dataSource
                (fun connection transaction token ->
                    task {
                        try
                            let! playlistExists = lookupLiveId "Playlists" playlistId connection transaction token
                            if not playlistExists then return Ok AdminContentMutation.NotFound
                            else
                                let mutable invalid = false
                                for item in items do
                                    let! trackExists = activeTrackExists item.TrackId connection transaction token
                                    if not trackExists then invalid <- true
                                    use existing = new NpgsqlCommand("SELECT \"PlaylistId\", \"IsDeleted\" FROM \"PlaylistItems\" WHERE \"Id\" = @Id FOR UPDATE;", connection, transaction)
                                    existing.Parameters.AddWithValue("Id", item.Id) |> ignore
                                    use! reader = existing.ExecuteReaderAsync(token)
                                    let! found = reader.ReadAsync(token)
                                    if found && (reader.GetBoolean(1) || reader.GetGuid(0) <> playlistId) then invalid <- true
                                if invalid then return Ok AdminContentMutation.NotFound
                                else
                                    use deleteOmitted = new NpgsqlCommand("""UPDATE "PlaylistItems" SET "IsDeleted" = true, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "PlaylistId" = @PlaylistId AND "IsDeleted" = false AND NOT ("Id" = ANY(@Ids));""", connection, transaction)
                                    deleteOmitted.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
                                    deleteOmitted.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                                    deleteOmitted.Parameters.AddWithValue("Ids", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid, items |> List.map _.Id |> List.toArray) |> ignore
                                    let! _ = deleteOmitted.ExecuteNonQueryAsync(token)
                                    use shiftPositions = new NpgsqlCommand("""UPDATE "PlaylistItems"
SET "Position" = "Position" + (SELECT COALESCE(MAX("Position"), 0) + @Count + 1 FROM "PlaylistItems" WHERE "PlaylistId" = @PlaylistId AND "IsDeleted" = false),
    "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "PlaylistId" = @PlaylistId AND "IsDeleted" = false;""", connection, transaction)
                                    shiftPositions.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
                                    shiftPositions.Parameters.AddWithValue("Count", List.length items) |> ignore
                                    shiftPositions.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                                    let! _ = shiftPositions.ExecuteNonQueryAsync(token)
                                    for (position, item) in items |> List.indexed do
                                        use command = new NpgsqlCommand("""INSERT INTO "PlaylistItems" ("Id", "PlaylistId", "TrackId", "Position", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @PlaylistId, @TrackId, @Position, false, @UpdatedAtUtc, @UpdatedAtUtc)
ON CONFLICT ("Id") DO UPDATE SET "TrackId" = EXCLUDED."TrackId", "Position" = EXCLUDED."Position", "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc";""", connection, transaction)
                                        command.Parameters.AddWithValue("Id", item.Id) |> ignore
                                        command.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
                                        command.Parameters.AddWithValue("TrackId", item.TrackId) |> ignore
                                        command.Parameters.AddWithValue("Position", position) |> ignore
                                        command.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                                        do! (command.ExecuteNonQueryAsync(token) :> Task)
                                    use select = new NpgsqlCommand("""SELECT item."Id", item."TrackId", track."Title", track."Artist", item."Position"
FROM "PlaylistItems" AS item INNER JOIN "Tracks" AS track ON track."Id" = item."TrackId" AND track."IsDeleted" = false
WHERE item."PlaylistId" = @PlaylistId AND item."IsDeleted" = false ORDER BY item."Position" ASC, item."Id" ASC;""", connection, transaction)
                                    select.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
                                    use! reader = select.ExecuteReaderAsync(token)
                                    let! values = readAll reader readPlaylistItem token
                                    return Ok(AdminContentMutation.Applied values)
                        with
                        | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                        | :? PostgresException as ex when isUniqueViolation ex -> return Ok AdminContentMutation.Conflict
                        | :? PostgresException as ex when isForeignKeyViolation ex -> return Ok AdminContentMutation.NotFound
                        | ex -> return Error(databaseError "AdminContentRepository.replacePlaylistItems" ex)
                    })
                cancellationToken

    let listAdditionalStorageBackends (dataSource: NpgsqlDataSource) (cancellationToken: CancellationToken) : Task<Result<AdditionalStorageBackend list, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("""SELECT "Id", "Name", "Type", "LocalRoot", "S3Bucket", "IsEnabled"
FROM "StorageBackends" WHERE "IsDeleted" = false ORDER BY "CreatedAtUtc" ASC, "Id" ASC;""", connection)
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! values = readAll reader readStorageBackend cancellationToken
                return Ok values
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminContentRepository.listAdditionalStorageBackends" ex)
        }

    let replaceAdditionalStorageBackends
        (dataSource: NpgsqlDataSource)
        (backends: AdditionalStorageBackendReplacement list)
        (updatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<AdditionalStorageBackend list>, RepositoryError>> =
        if backends |> List.map _.Id |> Set.ofList |> Set.count <> List.length backends then Task.FromResult(Ok AdminContentMutation.Conflict)
        else
            DatabaseSession.withTransactionResult
                dataSource
                (fun connection transaction token ->
                    task {
                        try
                            let mutable missing = false
                            for backend in backends do
                                let! active = lookupLiveId "StorageBackends" backend.Id connection transaction token
                                if not active then
                                    use historical = new NpgsqlCommand("SELECT 1 FROM \"StorageBackends\" WHERE \"Id\" = @Id;", connection, transaction)
                                    historical.Parameters.AddWithValue("Id", backend.Id) |> ignore
                                    let! known = historical.ExecuteScalarAsync(token)
                                    if not (isNull known) then missing <- true
                            if missing then return Ok AdminContentMutation.NotFound
                            else
                                for backend in backends do
                                    use command = new NpgsqlCommand("""INSERT INTO "StorageBackends" ("Id", "Name", "Type", "LocalRoot", "S3Bucket", "IsEnabled", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @Name, @Type, @LocalRoot, @S3Bucket, @IsEnabled, false, @UpdatedAtUtc, @UpdatedAtUtc)
ON CONFLICT ("Id") DO UPDATE SET "Name" = EXCLUDED."Name", "Type" = EXCLUDED."Type", "LocalRoot" = EXCLUDED."LocalRoot", "S3Bucket" = EXCLUDED."S3Bucket", "IsEnabled" = EXCLUDED."IsEnabled", "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc";""", connection, transaction)
                                    command.Parameters.AddWithValue("Id", backend.Id) |> ignore
                                    command.Parameters.AddWithValue("Name", backend.Name) |> ignore
                                    command.Parameters.AddWithValue("Type", backend.Type) |> ignore
                                    addNullableText command "LocalRoot" backend.LocalRoot
                                    addNullableText command "S3Bucket" backend.S3Bucket
                                    command.Parameters.AddWithValue("IsEnabled", backend.IsEnabled) |> ignore
                                    command.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                                    do! (command.ExecuteNonQueryAsync(token) :> Task)
                                use deleteOmitted = new NpgsqlCommand("""UPDATE "StorageBackends" SET "IsDeleted" = true, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "IsDeleted" = false AND NOT ("Id" = ANY(@Ids));""", connection, transaction)
                                deleteOmitted.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                                deleteOmitted.Parameters.AddWithValue("Ids", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid, backends |> List.map _.Id |> List.toArray) |> ignore
                                let! _ = deleteOmitted.ExecuteNonQueryAsync(token)
                                use select = new NpgsqlCommand("""SELECT "Id", "Name", "Type", "LocalRoot", "S3Bucket", "IsEnabled"
FROM "StorageBackends" WHERE "IsDeleted" = false ORDER BY "CreatedAtUtc" ASC, "Id" ASC;""", connection, transaction)
                                use! reader = select.ExecuteReaderAsync(token)
                                let! values = readAll reader readStorageBackend token
                                return Ok(AdminContentMutation.Applied values)
                        with
                        | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                        | :? PostgresException as ex when isUniqueViolation ex -> return Ok AdminContentMutation.Conflict
                        | :? PostgresException as ex when isForeignKeyViolation ex -> return Ok AdminContentMutation.NotFound
                        | ex -> return Error(databaseError "AdminContentRepository.replaceAdditionalStorageBackends" ex)
                    })
                cancellationToken
