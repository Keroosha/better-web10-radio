namespace Web10.Radio.Database.Repositories

open System
open Dodo.Primitives
open System.Globalization
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
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

type Banner =
    { Id: Guid
      Type: string
      Title: string
      Subtitle: string
      Text: string
      Style: string
      ScreenPosition: string
      Accent: string
      Enabled: bool
      SortOrder: int
      RotationSeconds: int }

type BannerReplacement =
    { Id: Guid
      Type: string
      Title: string
      Subtitle: string option
      Text: string option
      Style: string
      ScreenPosition: string
      Accent: string option
      Enabled: bool
      RotationSeconds: int option }

type AdminTrack =
    { Id: Guid
      Title: string
      Artist: string
      Album: string
      DurationMs: int
      HasCachedFile: bool
      CoverImageUrl: string
      MetadataSource: string
      StorageBackendId: Guid option }

type AdminTrackPage =
    { Items: AdminTrack list
      NextCursor: string option }

type AdminQueueItem =
    { Id: Guid
      TrackId: Guid
      Source: string
      Status: string
      Priority: int64
      PlaylistId: Guid option
      RequestedAtUtc: DateTimeOffset }

type AdminQueueToCreate =
    { Id: Guid
      TrackId: Guid
      RequestedAtUtc: DateTimeOffset }

[<RequireQualifiedAccess>]
type PlaylistType =
    | General
    | OncePerSongs
    | OncePerMinutes
    | OncePerHour

[<RequireQualifiedAccess>]
type PlaylistSource =
    | Manual
    | AllStorage

[<RequireQualifiedAccess>]
type PlaylistOrder =
    | Sequential
    | Shuffle
    | Random

type PlaylistSchedule =
    { Id: Guid option
      DaysOfWeek: int list
      StartTime: TimeSpan
      EndTime: TimeSpan
      StartDate: DateOnly option
      EndDate: DateOnly option
      TimeZoneId: string }

type PlaylistSummary =
    { Id: Guid
      Name: string
      Description: string option
      IsActive: bool
      Type: PlaylistType
      Source: PlaylistSource
      Order: PlaylistOrder
      Weight: int
      IsJingle: bool
      Interrupt: bool
      AvoidDuplicates: bool
      PlayEverySongs: int option
      PlayEveryMinutes: int option
      PlayAtMinute: int option
      IsSystem: bool
      ItemCount: int
      Schedules: PlaylistSchedule list }

type PlaylistToCreate =
    { Id: Guid
      Name: string
      Description: string option
      IsActive: bool
      Type: PlaylistType
      Source: PlaylistSource
      Order: PlaylistOrder
      Weight: int
      IsJingle: bool
      Interrupt: bool
      AvoidDuplicates: bool
      PlayEverySongs: int option
      PlayEveryMinutes: int option
      PlayAtMinute: int option
      IsSystem: bool
      Schedules: PlaylistSchedule list
      CreatedAtUtc: DateTimeOffset
      UpdatedAtUtc: DateTimeOffset }

type PlaylistUpdate =
    { Name: string
      Description: string option
      IsActive: bool
      Type: PlaylistType
      Source: PlaylistSource
      Order: PlaylistOrder
      Weight: int
      IsJingle: bool
      Interrupt: bool
      AvoidDuplicates: bool
      PlayEverySongs: int option
      PlayEveryMinutes: int option
      PlayAtMinute: int option
      Schedules: PlaylistSchedule list
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
          HasCachedFile = reader.GetBoolean(5)
          CoverImageUrl = if reader.IsDBNull(6) then "" else reader.GetString(6)
          MetadataSource = if reader.IsDBNull(7) then "Filename" else reader.GetString(7)
          StorageBackendId = if reader.IsDBNull(8) then None else Some(reader.GetGuid(8)) }

    [<Literal>]
    let private adminTrackByIdSql = """SELECT track."Id", track."Title", track."Artist", track."Album", track."DurationMs",
       EXISTS (SELECT 1 FROM "TrackFiles" AS file WHERE file."TrackId" = track."Id" AND file."IsDeleted" = false AND file."IsCached" = true AND file."CachePath" IS NOT NULL AND btrim(file."CachePath") <> ''),
       CASE
           WHEN cover."Source" = 'LegacyExternal' THEN COALESCE(cover."ExternalUrl", '')
           WHEN cover."Sha256" IS NOT NULL THEN '/api/v0/player/assets/cover/' || track."Id"::text || '?v=' || cover."Sha256"
           ELSE ''
       END,
       track."MetadataSource",
       (SELECT file."StorageBackendId" FROM "TrackFiles" AS file WHERE file."TrackId" = track."Id" AND file."IsDeleted" = false AND file."StorageBackendId" IS NOT NULL LIMIT 1)
FROM "Tracks" AS track
LEFT JOIN LATERAL (
    SELECT asset."Source", asset."ExternalUrl", asset."Sha256"
    FROM "TrackAssets" AS asset
    WHERE asset."TrackId" = track."Id" AND asset."Kind" = 'Cover' AND asset."IsDeleted" = false
    LIMIT 1
) AS cover ON true
WHERE track."Id" = @TrackId AND track."IsDeleted" = false;"""

    let private loadAdminTrackInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (trackId: Guid)
        (token: CancellationToken)
        : Task<AdminTrack option> =
        task {
            use command = new NpgsqlCommand(adminTrackByIdSql, connection, transaction)
            command.Parameters.AddWithValue("TrackId", trackId) |> ignore
            use! reader = command.ExecuteReaderAsync(token)
            let! found = reader.ReadAsync(token)
            return if found then Some(readAdminTrack reader) else None
        }

    let private activeTrackForUpdate
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (trackId: Guid)
        (token: CancellationToken)
        : Task<bool> =
        task {
            use command = new NpgsqlCommand("""SELECT 1 FROM "Tracks" WHERE "Id" = @TrackId AND "IsDeleted" = false FOR UPDATE;""", connection, transaction)
            command.Parameters.AddWithValue("TrackId", trackId) |> ignore
            let! value = command.ExecuteScalarAsync(token)
            return not (isNull value) && not (value :? DBNull)
        }

    let private readQueueItem (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          TrackId = reader.GetGuid(1)
          Source = reader.GetString(2)
          Status = reader.GetString(3)
          Priority = reader.GetInt64(4)
          PlaylistId = if reader.IsDBNull(5) then None else Some(reader.GetGuid(5))
          RequestedAtUtc = reader.GetFieldValue<DateTimeOffset>(6) }

    let private playlistTypeLiteral = function
        | PlaylistType.General -> "General"
        | PlaylistType.OncePerSongs -> "OncePerSongs"
        | PlaylistType.OncePerMinutes -> "OncePerMinutes"
        | PlaylistType.OncePerHour -> "OncePerHour"

    let private playlistSourceLiteral = function
        | PlaylistSource.Manual -> "Manual"
        | PlaylistSource.AllStorage -> "AllStorage"

    let private playlistOrderLiteral = function
        | PlaylistOrder.Sequential -> "Sequential"
        | PlaylistOrder.Shuffle -> "Shuffle"
        | PlaylistOrder.Random -> "Random"

    let private parsePlaylistType = function
        | "General" -> PlaylistType.General
        | "OncePerSongs" -> PlaylistType.OncePerSongs
        | "OncePerMinutes" -> PlaylistType.OncePerMinutes
        | "OncePerHour" -> PlaylistType.OncePerHour
        | value -> invalidArg "type" (sprintf "Unknown playlist type '%s'." value)

    let private parsePlaylistSource = function
        | "Manual" -> PlaylistSource.Manual
        | "AllStorage" -> PlaylistSource.AllStorage
        | value -> invalidArg "source" (sprintf "Unknown playlist source '%s'." value)

    let private parsePlaylistOrder = function
        | "Sequential" -> PlaylistOrder.Sequential
        | "Shuffle" -> PlaylistOrder.Shuffle
        | "Random" -> PlaylistOrder.Random
        | value -> invalidArg "order" (sprintf "Unknown playlist order '%s'." value)

    let private readPlaylistSummary (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          Name = reader.GetString(1)
          Description = if reader.IsDBNull(2) then None else Some(reader.GetString(2))
          IsActive = reader.GetBoolean(3)
          Type = reader.GetString(4) |> parsePlaylistType
          Source = reader.GetString(5) |> parsePlaylistSource
          Order = reader.GetString(6) |> parsePlaylistOrder
          Weight = reader.GetInt16(7) |> int
          IsJingle = reader.GetBoolean(8)
          Interrupt = reader.GetBoolean(9)
          AvoidDuplicates = reader.GetBoolean(10)
          PlayEverySongs = if reader.IsDBNull(11) then None else Some(reader.GetInt32(11))
          PlayEveryMinutes = if reader.IsDBNull(12) then None else Some(reader.GetInt32(12))
          PlayAtMinute = if reader.IsDBNull(13) then None else Some(reader.GetInt32(13))
          IsSystem = reader.GetBoolean(14)
          ItemCount = reader.GetInt32(15)
          Schedules = [] }

    let private readPlaylistSchedule (reader: NpgsqlDataReader) : PlaylistSchedule =
        { Id = Some(reader.GetGuid(0))
          DaysOfWeek = reader.GetFieldValue<int16 array>(1) |> Array.map int |> Array.toList
          StartTime = reader.GetFieldValue<TimeSpan>(2)
          EndTime = reader.GetFieldValue<TimeSpan>(3)
          StartDate = if reader.IsDBNull(4) then None else Some(reader.GetFieldValue<DateOnly>(4))
          EndDate = if reader.IsDBNull(5) then None else Some(reader.GetFieldValue<DateOnly>(5))
          TimeZoneId = reader.GetString(6) }

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

    let private encodeTrackCursor (createdAtUtc: DateTimeOffset) (id: Guid) =
        let json = sprintf "{\"createdAtUtc\":\"%s\",\"id\":\"%s\"}" (createdAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) (id.ToString("D"))
        Encoding.UTF8.GetBytes(json)
        |> Convert.ToBase64String
        |> fun value -> value.TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private tryDecodeTrackCursor (value: string) =
        try
            if String.IsNullOrWhiteSpace value then None
            else
                let normalized = value.Replace('-', '+').Replace('_', '/')
                let padded = normalized + String.replicate ((4 - normalized.Length % 4) % 4) "="
                let bytes = Convert.FromBase64String(padded)
                use document = JsonDocument.Parse(bytes)
                let root = document.RootElement
                let createdAt = root.GetProperty("createdAtUtc").GetString()
                let idText = root.GetProperty("id").GetString()
                let mutable createdAtUtc = DateTimeOffset.MinValue
                let mutable id = Guid.Empty
                if String.IsNullOrWhiteSpace createdAt || not (DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, &createdAtUtc)) then None
                elif String.IsNullOrWhiteSpace idText || not (Guid.TryParse(idText, &id)) || id = Guid.Empty then None
                else Some(createdAtUtc.ToUniversalTime(), id)
        with _ -> None

    let private addNullableDate (command: NpgsqlCommand) name (value: DateOnly option) =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Date)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value

    let private addNullableInt (command: NpgsqlCommand) name (value: int option) =
        let parameter = command.Parameters.Add(name, NpgsqlDbType.Integer)
        parameter.Value <- value |> Option.map box |> Option.defaultValue DBNull.Value

    let private addScheduleParameters (command: NpgsqlCommand) (schedule: PlaylistSchedule) =
        command.Parameters.Add("DaysOfWeek", NpgsqlDbType.Array ||| NpgsqlDbType.Smallint).Value <- (schedule.DaysOfWeek |> List.map int16 |> List.toArray)
        command.Parameters.AddWithValue("StartTime", schedule.StartTime) |> ignore
        command.Parameters.AddWithValue("EndTime", schedule.EndTime) |> ignore
        addNullableDate command "StartDate" schedule.StartDate
        addNullableDate command "EndDate" schedule.EndDate
        command.Parameters.AddWithValue("TimeZoneId", schedule.TimeZoneId) |> ignore

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

    let private readBanner (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          Type = reader.GetString(1)
          Title = reader.GetString(2)
          Subtitle = if reader.IsDBNull(3) then "" else reader.GetString(3)
          Text = if reader.IsDBNull(4) then "" else reader.GetString(4)
          Style = reader.GetString(5)
          ScreenPosition = reader.GetString(6)
          Accent = if reader.IsDBNull(7) then "" else reader.GetString(7)
          Enabled = reader.GetBoolean(8)
          SortOrder = reader.GetInt32(9)
          RotationSeconds = if reader.IsDBNull(10) then 0 else reader.GetInt32(10) }

    [<Literal>]
    let private bannerSelectSql = """SELECT "Id", "Type", "Title", "Subtitle", "Text", "Style", "ScreenPosition", "Accent", "Enabled", "SortOrder", "RotationSeconds"
FROM "Banners" WHERE "IsDeleted" = false ORDER BY "SortOrder" ASC, "Id" ASC;"""

    let listBanners (dataSource: NpgsqlDataSource) (cancellationToken: CancellationToken) : Task<Result<Banner list, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(bannerSelectSql, connection)
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! values = readAll reader readBanner cancellationToken
                return Ok values
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminContentRepository.listBanners" ex)
        }

    let replaceBanners
        (dataSource: NpgsqlDataSource)
        (banners: BannerReplacement list)
        (updatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<Banner list>, RepositoryError>> =
        if banners |> List.map _.Id |> Set.ofList |> Set.count <> List.length banners then
            Task.FromResult(Ok AdminContentMutation.Conflict)
        else
            DatabaseSession.withTransactionResult
                dataSource
                (fun connection transaction token ->
                    task {
                        try
                            let mutable missing = false
                            for banner in banners do
                                let! exists = lookupLiveId "Banners" banner.Id connection transaction token
                                if not exists then
                                    use historical = new NpgsqlCommand("SELECT 1 FROM \"Banners\" WHERE \"Id\" = @Id;", connection, transaction)
                                    historical.Parameters.AddWithValue("Id", banner.Id) |> ignore
                                    let! known = historical.ExecuteScalarAsync(token)
                                    if not (isNull known) then missing <- true
                            if missing then return Ok AdminContentMutation.NotFound
                            else
                                for (position, banner) in banners |> List.indexed do
                                    use command = new NpgsqlCommand("""INSERT INTO "Banners" ("Id", "Type", "Title", "Subtitle", "Text", "Style", "ScreenPosition", "Accent", "Enabled", "SortOrder", "RotationSeconds", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @Type, @Title, @Subtitle, @Text, @Style, @ScreenPosition, @Accent, @Enabled, @SortOrder, @RotationSeconds, false, @UpdatedAtUtc, @UpdatedAtUtc)
ON CONFLICT ("Id") DO UPDATE SET "Type" = EXCLUDED."Type", "Title" = EXCLUDED."Title", "Subtitle" = EXCLUDED."Subtitle", "Text" = EXCLUDED."Text", "Style" = EXCLUDED."Style", "ScreenPosition" = EXCLUDED."ScreenPosition", "Accent" = EXCLUDED."Accent", "Enabled" = EXCLUDED."Enabled", "SortOrder" = EXCLUDED."SortOrder", "RotationSeconds" = EXCLUDED."RotationSeconds", "IsDeleted" = false, "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc";""", connection, transaction)
                                    command.Parameters.AddWithValue("Id", banner.Id) |> ignore
                                    command.Parameters.AddWithValue("Type", banner.Type) |> ignore
                                    command.Parameters.AddWithValue("Title", banner.Title) |> ignore
                                    addNullableText command "Subtitle" banner.Subtitle
                                    addNullableText command "Text" banner.Text
                                    command.Parameters.AddWithValue("Style", banner.Style) |> ignore
                                    command.Parameters.AddWithValue("ScreenPosition", banner.ScreenPosition) |> ignore
                                    addNullableText command "Accent" banner.Accent
                                    command.Parameters.AddWithValue("Enabled", banner.Enabled) |> ignore
                                    command.Parameters.AddWithValue("SortOrder", position) |> ignore
                                    addNullableInt command "RotationSeconds" banner.RotationSeconds
                                    command.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                                    do! (command.ExecuteNonQueryAsync(token) :> Task)
                                use deleteOmitted = new NpgsqlCommand("""UPDATE "Banners" SET "IsDeleted" = true, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "IsDeleted" = false AND NOT ("Id" = ANY(@Ids));""", connection, transaction)
                                deleteOmitted.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                                deleteOmitted.Parameters.AddWithValue("Ids", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid, banners |> List.map _.Id |> List.toArray) |> ignore
                                let! _ = deleteOmitted.ExecuteNonQueryAsync(token)
                                use select = new NpgsqlCommand(bannerSelectSql, connection, transaction)
                                use! reader = select.ExecuteReaderAsync(token)
                                let! values = readAll reader readBanner token
                                return Ok(AdminContentMutation.Applied values)
                        with
                        | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                        | :? PostgresException as ex when isUniqueViolation ex -> return Ok AdminContentMutation.Conflict
                        | :? PostgresException as ex when isForeignKeyViolation ex -> return Ok AdminContentMutation.NotFound
                        | ex -> return Error(databaseError "AdminContentRepository.replaceBanners" ex)
                    })
                cancellationToken

    let listActiveTracksPage
        (dataSource: NpgsqlDataSource)
        (query: string)
        (limit: int)
        (cursor: string option)
        (storageBackendId: Guid option)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminTrackPage, RepositoryError>> =
        task {
            try
                if limit < 1 || limit > 100 then
                    return Error(DatabaseError("AdminContentRepository.listActiveTracksPage", "The limit must be between 1 and 100."))
                else
                    let decodedCursor =
                        match cursor with
                        | None -> Ok None
                        | Some value ->
                            match tryDecodeTrackCursor value with
                            | Some decoded -> Ok(Some decoded)
                            | None -> Error(DatabaseError("AdminContentRepository.listActiveTracksPage", "The cursor is invalid."))
                    match decodedCursor with
                    | Error error -> return Error error
                    | Ok decodedCursor ->
                        use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                        use command = new NpgsqlCommand("""SELECT track."Id", track."Title", track."Artist", track."Album", track."DurationMs",
       EXISTS (SELECT 1 FROM "TrackFiles" AS file WHERE file."TrackId" = track."Id" AND file."IsDeleted" = false AND file."IsCached" = true AND file."CachePath" IS NOT NULL AND btrim(file."CachePath") <> ''),
       CASE
           WHEN cover."Source" = 'LegacyExternal' THEN COALESCE(cover."ExternalUrl", '')
           WHEN cover."Sha256" IS NOT NULL THEN '/api/v0/player/assets/cover/' || track."Id"::text || '?v=' || cover."Sha256"
           ELSE ''
       END,
       track."MetadataSource",
       (SELECT file."StorageBackendId" FROM "TrackFiles" AS file WHERE file."TrackId" = track."Id" AND file."IsDeleted" = false AND file."StorageBackendId" IS NOT NULL LIMIT 1),
       track."CreatedAtUtc"
FROM "Tracks" AS track
LEFT JOIN LATERAL (
    SELECT asset."Source", asset."ExternalUrl", asset."Sha256"
    FROM "TrackAssets" AS asset
    WHERE asset."TrackId" = track."Id" AND asset."Kind" = 'Cover' AND asset."IsDeleted" = false
    LIMIT 1
) AS cover ON true
WHERE track."IsDeleted" = false
  AND (@Query = '' OR track."Title" ILIKE '%' || @Query || '%' OR track."Artist" ILIKE '%' || @Query || '%' OR COALESCE(track."Album", '') ILIKE '%' || @Query || '%')
  AND (@FilterStorage = false OR EXISTS (SELECT 1 FROM "TrackFiles" AS storageFile WHERE storageFile."TrackId" = track."Id" AND storageFile."IsDeleted" = false AND storageFile."StorageBackendId" = @StorageBackendId))
  AND (@HasCursor = false OR track."CreatedAtUtc" < @CursorCreatedAt OR (track."CreatedAtUtc" = @CursorCreatedAt AND track."Id" > @CursorId))
ORDER BY track."CreatedAtUtc" DESC, track."Id" ASC
LIMIT @Limit;""", connection)
                        command.Parameters.AddWithValue("Query", query.Trim()) |> ignore
                        command.Parameters.AddWithValue("Limit", limit + 1) |> ignore
                        match storageBackendId with
                        | Some backendId ->
                            command.Parameters.AddWithValue("FilterStorage", true) |> ignore
                            command.Parameters.AddWithValue("StorageBackendId", backendId) |> ignore
                        | None ->
                            command.Parameters.AddWithValue("FilterStorage", false) |> ignore
                            command.Parameters.Add("StorageBackendId", NpgsqlDbType.Uuid).Value <- DBNull.Value
                        match decodedCursor with
                        | None ->
                            command.Parameters.AddWithValue("HasCursor", false) |> ignore
                            command.Parameters.Add("CursorCreatedAt", NpgsqlDbType.TimestampTz).Value <- DBNull.Value
                            command.Parameters.Add("CursorId", NpgsqlDbType.Uuid).Value <- DBNull.Value
                        | Some(createdAt, id) ->
                            command.Parameters.AddWithValue("HasCursor", true) |> ignore
                            command.Parameters.AddWithValue("CursorCreatedAt", createdAt) |> ignore
                            command.Parameters.AddWithValue("CursorId", id) |> ignore
                        use! reader = command.ExecuteReaderAsync(cancellationToken)
                        let rows = ResizeArray<AdminTrack * DateTimeOffset>()
                        let mutable reading = true
                        while reading do
                            let! found = reader.ReadAsync(cancellationToken)
                            if found then rows.Add(readAdminTrack reader, reader.GetFieldValue<DateTimeOffset>(9)) else reading <- false
                        let hasNext = rows.Count > limit
                        let pageRows = rows |> Seq.truncate limit |> Seq.toList
                        let nextCursor =
                            if hasNext then
                                pageRows |> List.tryLast |> Option.map (fun (track, createdAt) -> encodeTrackCursor createdAt track.Id)
                            else None
                        return Ok { Items = pageRows |> List.map fst; NextCursor = nextCursor }
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminContentRepository.listActiveTracksPage" ex)
        }

    let listActiveTracks
        (dataSource: NpgsqlDataSource)
        (query: string)
        (limit: int)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminTrack list, RepositoryError>> =
        task {
            let! result = listActiveTracksPage dataSource query limit None None cancellationToken
            return result |> Result.map _.Items
        }

    let updateTrackMetadataInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (trackId: Guid)
        (title: string)
        (artist: string)
        (album: string option)
        (updatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<AdminTrack>, RepositoryError>> =
        taskResult {
            try
                let! exists = activeTrackForUpdate connection transaction trackId cancellationToken
                if not exists then
                    return AdminContentMutation.NotFound
                else
                    use update = new NpgsqlCommand("""UPDATE "Tracks"
SET "Title" = @Title, "Artist" = @Artist, "Album" = @Album,
    "MetadataSource" = 'Manual', "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "Id" = @TrackId AND "IsDeleted" = false;""", connection, transaction)
                    update.Parameters.AddWithValue("TrackId", trackId) |> ignore
                    update.Parameters.AddWithValue("Title", title) |> ignore
                    update.Parameters.AddWithValue("Artist", artist) |> ignore
                    addNullableText update "Album" album
                    update.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                    let! _ = update.ExecuteNonQueryAsync(cancellationToken)
                    let! projection = loadAdminTrackInTransaction connection transaction trackId cancellationToken
                    return
                        match projection with
                        | Some value -> AdminContentMutation.Applied value
                        | None -> AdminContentMutation.NotFound
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return! Error(databaseError "AdminContentRepository.updateTrackMetadataInTransaction" ex)
        }

    let replaceTrackCoverInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (trackId: Guid)
        (cachePath: string)
        (contentType: string)
        (sizeBytes: int64)
        (sha256: string)
        (updatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<AdminTrack>, RepositoryError>> =
        taskResult {
            try
                let! exists = activeTrackForUpdate connection transaction trackId cancellationToken
                if not exists then
                    return AdminContentMutation.NotFound
                else
                    use delete = new NpgsqlCommand("""UPDATE "TrackAssets"
SET "IsDeleted" = true, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "TrackId" = @TrackId AND "Kind" = 'Cover' AND "IsDeleted" = false;""", connection, transaction)
                    delete.Parameters.AddWithValue("TrackId", trackId) |> ignore
                    delete.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                    let! _ = delete.ExecuteNonQueryAsync(cancellationToken)
                    use insert = new NpgsqlCommand("""INSERT INTO "TrackAssets"
    ("Id", "TrackId", "Kind", "Source", "CachePath", "ContentType", "SizeBytes", "Sha256", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@AssetId, @TrackId, 'Cover', 'Manual', @CachePath, @ContentType, @SizeBytes, @Sha256, false, @UpdatedAtUtc, @UpdatedAtUtc);""", connection, transaction)
                    insert.Parameters.AddWithValue("AssetId", Uuid.CreateVersion7().ToGuidBigEndian()) |> ignore
                    insert.Parameters.AddWithValue("TrackId", trackId) |> ignore
                    insert.Parameters.AddWithValue("CachePath", cachePath) |> ignore
                    insert.Parameters.AddWithValue("ContentType", contentType) |> ignore
                    insert.Parameters.AddWithValue("SizeBytes", sizeBytes) |> ignore
                    insert.Parameters.AddWithValue("Sha256", sha256) |> ignore
                    insert.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                    let! _ = insert.ExecuteNonQueryAsync(cancellationToken)
                    use touch = new NpgsqlCommand("""UPDATE "Tracks" SET "UpdatedAtUtc" = @UpdatedAtUtc WHERE "Id" = @TrackId;""", connection, transaction)
                    touch.Parameters.AddWithValue("TrackId", trackId) |> ignore
                    touch.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                    let! _ = touch.ExecuteNonQueryAsync(cancellationToken)
                    let! projection = loadAdminTrackInTransaction connection transaction trackId cancellationToken
                    return
                        match projection with
                        | Some value -> AdminContentMutation.Applied value
                        | None -> AdminContentMutation.NotFound
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | :? PostgresException as ex when isUniqueViolation ex -> return AdminContentMutation.Conflict
            | :? PostgresException as ex when isForeignKeyViolation ex -> return AdminContentMutation.NotFound
            | ex -> return! Error(databaseError "AdminContentRepository.replaceTrackCoverInTransaction" ex)
        }

    let removeTrackCoverInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (trackId: Guid)
        (updatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<AdminTrack>, RepositoryError>> =
        taskResult {
            try
                let! exists = activeTrackForUpdate connection transaction trackId cancellationToken
                if not exists then
                    return AdminContentMutation.NotFound
                else
                    use delete = new NpgsqlCommand("""UPDATE "TrackAssets"
SET "IsDeleted" = true, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "TrackId" = @TrackId AND "Kind" = 'Cover' AND "IsDeleted" = false;""", connection, transaction)
                    delete.Parameters.AddWithValue("TrackId", trackId) |> ignore
                    delete.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                    let! _ = delete.ExecuteNonQueryAsync(cancellationToken)
                    use touch = new NpgsqlCommand("""UPDATE "Tracks" SET "UpdatedAtUtc" = @UpdatedAtUtc WHERE "Id" = @TrackId;""", connection, transaction)
                    touch.Parameters.AddWithValue("TrackId", trackId) |> ignore
                    touch.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                    let! _ = touch.ExecuteNonQueryAsync(cancellationToken)
                    let! projection = loadAdminTrackInTransaction connection transaction trackId cancellationToken
                    return
                        match projection with
                        | Some value -> AdminContentMutation.Applied value
                        | None -> AdminContentMutation.NotFound
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return! Error(databaseError "AdminContentRepository.removeTrackCoverInTransaction" ex)
        }

    let private enqueueAdminTrackInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (request: AdminQueueToCreate)
        (outboxEvent: OutboxEventToAppend option)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<AdminQueueItem>, RepositoryError>> =
        taskResult {
            try
                use playable = new NpgsqlCommand("""SELECT 1 FROM "Tracks" AS track
WHERE track."Id" = @TrackId AND track."IsDeleted" = false
  AND EXISTS (SELECT 1 FROM "TrackFiles" AS file WHERE file."TrackId" = track."Id" AND file."IsDeleted" = false AND file."IsCached" = true AND file."CachePath" IS NOT NULL AND btrim(file."CachePath") <> '')
FOR UPDATE;""", connection, transaction)
                playable.Parameters.AddWithValue("TrackId", request.TrackId) |> ignore
                let! found = playable.ExecuteScalarAsync(cancellationToken)
                if isNull found then
                    return AdminContentMutation.NotFound
                else
                    use command = new NpgsqlCommand("""INSERT INTO "PlaybackQueue" ("Id", "TrackId", "TrackRequestId", "PlaylistItemId", "PlaylistId", "Source", "Status", "Priority", "RequestedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @TrackId, NULL, NULL, NULL, 'admin', 'Queued', 0, @RequestedAtUtc, false, @RequestedAtUtc, @RequestedAtUtc)
RETURNING "Id", "TrackId", "Source", "Status", "Priority", "PlaylistId", "RequestedAtUtc";""", connection, transaction)
                    command.Parameters.AddWithValue("Id", request.Id) |> ignore
                    command.Parameters.AddWithValue("TrackId", request.TrackId) |> ignore
                    command.Parameters.AddWithValue("RequestedAtUtc", request.RequestedAtUtc) |> ignore
                    use! reader = command.ExecuteReaderAsync(cancellationToken)
                    let! inserted = reader.ReadAsync(cancellationToken)
                    if not inserted then
                        return! Error(DatabaseError("AdminContentRepository.enqueueAdminTrack", "The insert did not return a queue item."))
                    else
                        let item = readQueueItem reader
                        reader.Close()
                        match outboxEvent with
                        | None -> return AdminContentMutation.Applied item
                        | Some event ->
                            do! OutboxEventRepository.appendInTransaction connection transaction event cancellationToken |> TaskResult.mapError id
                            return AdminContentMutation.Applied item
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | :? PostgresException as ex when isUniqueViolation ex -> return AdminContentMutation.Conflict
            | :? PostgresException as ex when isForeignKeyViolation ex -> return AdminContentMutation.NotFound
            | ex -> return! Error(databaseError "AdminContentRepository.enqueueAdminTrack" ex)
        }

    let enqueueAdminTrack
        (dataSource: NpgsqlDataSource)
        (request: AdminQueueToCreate)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<AdminQueueItem>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token -> enqueueAdminTrackInTransaction connection transaction request None token)
            cancellationToken

    let enqueueAdminTrackWithEvent
        (dataSource: NpgsqlDataSource)
        (request: AdminQueueToCreate)
        (event: OutboxEventToAppend)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<AdminQueueItem>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token -> enqueueAdminTrackInTransaction connection transaction request (Some event) token)
            cancellationToken
    let private playlistSelectSql = """SELECT playlist."Id", playlist."Name", playlist."Description", playlist."IsActive", playlist."Type", playlist."Source", playlist."Order", playlist."Weight", playlist."IsJingle", playlist."Interrupt", playlist."AvoidDuplicates", playlist."PlayEverySongs", playlist."PlayEveryMinutes", playlist."PlayAtMinute", playlist."IsSystem", COUNT(item."Id")::integer
FROM "Playlists" AS playlist
LEFT JOIN "PlaylistItems" AS item ON item."PlaylistId" = playlist."Id" AND item."IsDeleted" = false
WHERE playlist."Id" = @PlaylistId AND playlist."IsDeleted" = false
GROUP BY playlist."Id"
"""

    let private commandWithTransaction sql (connection: NpgsqlConnection) (transaction: NpgsqlTransaction option) =
        match transaction with
        | Some value -> new NpgsqlCommand(sql, connection, value)
        | None -> new NpgsqlCommand(sql, connection)

    let private loadPlaylistSchedules (connection: NpgsqlConnection) (transaction: NpgsqlTransaction option) (playlistId: Guid) (token: CancellationToken) : Task<PlaylistSchedule list> =
        task {
            use command = commandWithTransaction """SELECT "Id", "DaysOfWeek", "StartTime", "EndTime", "StartDate", "EndDate", "TimeZoneId"
FROM "PlaylistSchedules" WHERE "PlaylistId" = @PlaylistId AND "IsDeleted" = false ORDER BY "StartTime" ASC, "EndTime" ASC, "Id" ASC;""" connection transaction
            command.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
            use! reader = command.ExecuteReaderAsync(token)
            return! readAll reader readPlaylistSchedule token
        }

    let private loadPlaylistSummary (connection: NpgsqlConnection) (transaction: NpgsqlTransaction option) (playlistId: Guid) (token: CancellationToken) : Task<PlaylistSummary option> =
        task {
            use command = commandWithTransaction playlistSelectSql connection transaction
            command.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
            use! reader = command.ExecuteReaderAsync(token)
            let! found = reader.ReadAsync(token)
            if not found then return None
            else
                let summary = readPlaylistSummary reader
                reader.Close()
                let! schedules = loadPlaylistSchedules connection transaction playlistId token
                return Some { summary with Schedules = schedules }
        }

    let private replacePlaylistSchedules
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (playlistId: Guid)
        (schedules: PlaylistSchedule list)
        (updatedAtUtc: DateTimeOffset)
        (token: CancellationToken)
        : Task<AdminContentMutation<unit>> =
        task {
            let ids = schedules |> List.choose (fun schedule -> schedule.Id)
            if schedules.Length > 32 || Set.ofList ids |> Set.count <> List.length ids then
                return AdminContentMutation.Conflict
            else
                let persisted : PlaylistSchedule list = schedules |> List.map (fun schedule -> { schedule with Id = schedule.Id |> Option.orElseWith (fun () -> Some(Uuid.CreateVersion7().ToGuidBigEndian())) })
                let mutable invalid = false
                let mutable notFound = false
                for schedule in persisted do
                    match schedule.Id with
                    | None -> invalid <- true
                    | Some scheduleId ->
                        use existing = new NpgsqlCommand("SELECT \"PlaylistId\", \"IsDeleted\" FROM \"PlaylistSchedules\" WHERE \"Id\" = @Id FOR UPDATE;", connection, transaction)
                        existing.Parameters.AddWithValue("Id", scheduleId) |> ignore
                        use! reader = existing.ExecuteReaderAsync(token)
                        let! found = reader.ReadAsync(token)
                        if found && (reader.GetBoolean(1) || reader.GetGuid(0) <> playlistId) then notFound <- true
                if invalid then return AdminContentMutation.Conflict
                elif notFound then return AdminContentMutation.NotFound
                else
                    for schedule in persisted do
                        let scheduleId = schedule.Id.Value
                        use command = new NpgsqlCommand("""INSERT INTO "PlaylistSchedules" ("Id", "PlaylistId", "DaysOfWeek", "StartTime", "EndTime", "StartDate", "EndDate", "TimeZoneId", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @PlaylistId, @DaysOfWeek, @StartTime, @EndTime, @StartDate, @EndDate, @TimeZoneId, false, @UpdatedAtUtc, @UpdatedAtUtc)
ON CONFLICT ("Id") DO UPDATE SET "DaysOfWeek" = EXCLUDED."DaysOfWeek", "StartTime" = EXCLUDED."StartTime", "EndTime" = EXCLUDED."EndTime", "StartDate" = EXCLUDED."StartDate", "EndDate" = EXCLUDED."EndDate", "TimeZoneId" = EXCLUDED."TimeZoneId", "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc";""", connection, transaction)
                        command.Parameters.AddWithValue("Id", scheduleId) |> ignore
                        command.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
                        addScheduleParameters command schedule
                        command.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                        do! (command.ExecuteNonQueryAsync(token) :> Task)
                    use deleteOmitted = new NpgsqlCommand("""UPDATE "PlaylistSchedules" SET "IsDeleted" = true, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "PlaylistId" = @PlaylistId AND "IsDeleted" = false AND NOT ("Id" = ANY(@Ids));""", connection, transaction)
                    deleteOmitted.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
                    deleteOmitted.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                    deleteOmitted.Parameters.AddWithValue("Ids", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid, persisted |> List.choose (fun schedule -> schedule.Id) |> List.toArray) |> ignore
                    let! _ = deleteOmitted.ExecuteNonQueryAsync(token)
                    return AdminContentMutation.Applied ()
        }

    let private ensureSchedulerState (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (playlistId: Guid) (updatedAtUtc: DateTimeOffset) (token: CancellationToken) =
        task {
            use command = new NpgsqlCommand("""INSERT INTO "PlaylistSchedulerState" ("PlaylistId", "ShuffleSeed", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@PlaylistId, @ShuffleSeed, @UpdatedAtUtc, @UpdatedAtUtc)
ON CONFLICT ("PlaylistId") DO NOTHING;""", connection, transaction)
            command.Parameters.AddWithValue("PlaylistId", playlistId) |> ignore
            command.Parameters.AddWithValue("ShuffleSeed", Uuid.CreateVersion7().ToGuidBigEndian()) |> ignore
            command.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
            let! _ = command.ExecuteNonQueryAsync(token)
            return ()
        }

    let listPlaylists (dataSource: NpgsqlDataSource) (cancellationToken: CancellationToken) : Task<Result<PlaylistSummary list, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand(playlistSelectSql.Replace("WHERE playlist.\"Id\" = @PlaylistId AND ", "WHERE ") + " ORDER BY playlist.\"CreatedAtUtc\" DESC, playlist.\"Id\" ASC;", connection)
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! baseValues = readAll reader readPlaylistSummary cancellationToken
                reader.Close()
                let summaries = ResizeArray<PlaylistSummary>()
                for summary in baseValues do
                    let! schedules = loadPlaylistSchedules connection None summary.Id cancellationToken
                    summaries.Add({ summary with Schedules = schedules })
                return Ok(List.ofSeq summaries)
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminContentRepository.listPlaylists" ex)
        }

    let createPlaylistWithEvent
        (dataSource: NpgsqlDataSource)
        (playlist: PlaylistToCreate)
        (outboxEvent: OutboxEventToAppend option)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<PlaylistSummary>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        use command = new NpgsqlCommand("""INSERT INTO "Playlists" ("Id", "Name", "Description", "IsActive", "Type", "Source", "Order", "Weight", "IsJingle", "Interrupt", "AvoidDuplicates", "PlayEverySongs", "PlayEveryMinutes", "PlayAtMinute", "IsSystem", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @Name, @Description, @IsActive, @Type, @Source, @Order, @Weight, @IsJingle, @Interrupt, @AvoidDuplicates, @PlayEverySongs, @PlayEveryMinutes, @PlayAtMinute, @IsSystem, false, @CreatedAtUtc, @UpdatedAtUtc);""", connection, transaction)
                        command.Parameters.AddWithValue("Id", playlist.Id) |> ignore
                        command.Parameters.AddWithValue("Name", playlist.Name) |> ignore
                        addNullableText command "Description" playlist.Description
                        command.Parameters.AddWithValue("IsActive", playlist.IsActive) |> ignore
                        command.Parameters.AddWithValue("Type", playlist.Type |> playlistTypeLiteral) |> ignore
                        command.Parameters.AddWithValue("Source", playlist.Source |> playlistSourceLiteral) |> ignore
                        command.Parameters.AddWithValue("Order", playlist.Order |> playlistOrderLiteral) |> ignore
                        command.Parameters.AddWithValue("Weight", int16 playlist.Weight) |> ignore
                        command.Parameters.AddWithValue("IsJingle", playlist.IsJingle) |> ignore
                        command.Parameters.AddWithValue("Interrupt", playlist.Interrupt) |> ignore
                        command.Parameters.AddWithValue("AvoidDuplicates", playlist.AvoidDuplicates) |> ignore
                        addNullableInt command "PlayEverySongs" playlist.PlayEverySongs
                        addNullableInt command "PlayEveryMinutes" playlist.PlayEveryMinutes
                        addNullableInt command "PlayAtMinute" playlist.PlayAtMinute
                        command.Parameters.AddWithValue("IsSystem", playlist.IsSystem) |> ignore
                        command.Parameters.AddWithValue("CreatedAtUtc", playlist.CreatedAtUtc) |> ignore
                        command.Parameters.AddWithValue("UpdatedAtUtc", playlist.UpdatedAtUtc) |> ignore
                        let! _ = command.ExecuteNonQueryAsync(token)
                        let! schedulesResult = replacePlaylistSchedules connection transaction playlist.Id playlist.Schedules playlist.UpdatedAtUtc token
                        match schedulesResult with
                        | AdminContentMutation.Conflict -> return Ok AdminContentMutation.Conflict
                        | AdminContentMutation.NotFound -> return Ok AdminContentMutation.NotFound
                        | AdminContentMutation.Applied () ->
                            do! ensureSchedulerState connection transaction playlist.Id playlist.UpdatedAtUtc token
                            let! _ =
                                match outboxEvent with
                                | Some event -> OutboxEventRepository.appendInTransaction connection transaction event token |> TaskResult.mapError id
                                | None -> Task.FromResult(Ok ())
                            let! summary = loadPlaylistSummary connection (Some transaction) playlist.Id token
                            match summary with
                            | Some value -> return Ok(AdminContentMutation.Applied value)
                            | None -> return Error(DatabaseError("AdminContentRepository.createPlaylist", "The insert did not return a playlist."))
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | :? PostgresException as ex when isUniqueViolation ex -> return Ok AdminContentMutation.Conflict
                    | :? PostgresException as ex when isForeignKeyViolation ex -> return Ok AdminContentMutation.NotFound
                    | ex -> return Error(databaseError "AdminContentRepository.createPlaylist" ex)
                })
            cancellationToken
    let createPlaylist
        (dataSource: NpgsqlDataSource)
        (playlist: PlaylistToCreate)
        (cancellationToken: CancellationToken) =
        createPlaylistWithEvent dataSource playlist None cancellationToken

    let ensureAllTracksPlaylist
        (dataSource: NpgsqlDataSource)
        (playlistId: Guid)
        (updatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<PlaylistSummary>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        use existing = new NpgsqlCommand("SELECT \"Id\" FROM \"Playlists\" WHERE \"IsDeleted\" = false AND \"IsActive\" = true AND \"IsSystem\" = true AND \"Source\" = 'AllStorage' LIMIT 1 FOR UPDATE;", connection, transaction)
                        use! reader = existing.ExecuteReaderAsync(token)
                        let! found = reader.ReadAsync(token)
                        let existingId = if found then Some(reader.GetGuid(0)) else None
                        reader.Close()
                        let targetId = existingId |> Option.defaultValue playlistId
                        if existingId.IsNone then
                            use insert = new NpgsqlCommand("""INSERT INTO "Playlists" ("Id", "Name", "Description", "IsActive", "Type", "Source", "Order", "Weight", "IsJingle", "Interrupt", "AvoidDuplicates", "PlayEverySongs", "PlayEveryMinutes", "PlayAtMinute", "IsSystem", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, 'All tracks', NULL, true, 'General', 'AllStorage', 'Shuffle', 3, false, false, true, NULL, NULL, NULL, true, false, @UpdatedAtUtc, @UpdatedAtUtc);""", connection, transaction)
                            insert.Parameters.AddWithValue("Id", targetId) |> ignore
                            insert.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                            let! _ = insert.ExecuteNonQueryAsync(token)
                            ()
                        else
                            ()
                        do! ensureSchedulerState connection transaction targetId updatedAtUtc token
                        let! summary = loadPlaylistSummary connection (Some transaction) targetId token
                        match summary with
                        | Some value -> return Ok(AdminContentMutation.Applied value)
                        | None -> return Error(DatabaseError("AdminContentRepository.ensureAllTracksPlaylist", "The All tracks playlist was not returned."))
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | :? PostgresException as ex when isUniqueViolation ex -> return Ok AdminContentMutation.Conflict
                    | ex -> return Error(databaseError "AdminContentRepository.ensureAllTracksPlaylist" ex)
                })
            cancellationToken

    let ensureAllStoragePlaylist = ensureAllTracksPlaylist
    let updatePlaylistWithEvent
        (dataSource: NpgsqlDataSource)
        (playlistId: Guid)
        (update: PlaylistUpdate)
        (outboxEvent: OutboxEventToAppend option)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminContentMutation<PlaylistSummary>, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        use current = new NpgsqlCommand("SELECT \"IsSystem\", \"Type\", \"Source\" FROM \"Playlists\" WHERE \"Id\" = @Id AND \"IsDeleted\" = false FOR UPDATE;", connection, transaction)
                        current.Parameters.AddWithValue("Id", playlistId) |> ignore
                        use! currentReader = current.ExecuteReaderAsync(token)
                        let! found = currentReader.ReadAsync(token)
                        if not found then return Ok AdminContentMutation.NotFound
                        else
                            let isSystem = currentReader.GetBoolean(0)
                            let currentType = currentReader.GetString(1) |> parsePlaylistType
                            let currentSource = currentReader.GetString(2) |> parsePlaylistSource
                            currentReader.Close()
                            if isSystem && (currentType <> update.Type || currentSource <> update.Source) then return Ok AdminContentMutation.Conflict
                            else
                                use command = new NpgsqlCommand("""UPDATE "Playlists" SET "Name" = @Name, "Description" = @Description, "IsActive" = @IsActive, "Type" = @Type, "Source" = @Source, "Order" = @Order, "Weight" = @Weight, "IsJingle" = @IsJingle, "Interrupt" = @Interrupt, "AvoidDuplicates" = @AvoidDuplicates, "PlayEverySongs" = @PlayEverySongs, "PlayEveryMinutes" = @PlayEveryMinutes, "PlayAtMinute" = @PlayAtMinute, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "Id" = @Id AND "IsDeleted" = false;""", connection, transaction)
                                command.Parameters.AddWithValue("Id", playlistId) |> ignore
                                command.Parameters.AddWithValue("Name", update.Name) |> ignore
                                addNullableText command "Description" update.Description
                                command.Parameters.AddWithValue("IsActive", update.IsActive) |> ignore
                                command.Parameters.AddWithValue("Type", update.Type |> playlistTypeLiteral) |> ignore
                                command.Parameters.AddWithValue("Source", update.Source |> playlistSourceLiteral) |> ignore
                                command.Parameters.AddWithValue("Order", update.Order |> playlistOrderLiteral) |> ignore
                                command.Parameters.AddWithValue("Weight", int16 update.Weight) |> ignore
                                command.Parameters.AddWithValue("IsJingle", update.IsJingle) |> ignore
                                command.Parameters.AddWithValue("Interrupt", update.Interrupt) |> ignore
                                command.Parameters.AddWithValue("AvoidDuplicates", update.AvoidDuplicates) |> ignore
                                addNullableInt command "PlayEverySongs" update.PlayEverySongs
                                addNullableInt command "PlayEveryMinutes" update.PlayEveryMinutes
                                addNullableInt command "PlayAtMinute" update.PlayAtMinute
                                command.Parameters.AddWithValue("UpdatedAtUtc", update.UpdatedAtUtc) |> ignore
                                let! _ = command.ExecuteNonQueryAsync(token)
                                let! schedulesResult = replacePlaylistSchedules connection transaction playlistId update.Schedules update.UpdatedAtUtc token
                                match schedulesResult with
                                | AdminContentMutation.Conflict -> return Ok AdminContentMutation.Conflict
                                | AdminContentMutation.NotFound -> return Ok AdminContentMutation.NotFound
                                | AdminContentMutation.Applied () ->
                                    do! ensureSchedulerState connection transaction playlistId update.UpdatedAtUtc token
                                    let! _ =
                                        match outboxEvent with
                                        | Some event -> OutboxEventRepository.appendInTransaction connection transaction event token |> TaskResult.mapError id
                                        | None -> Task.FromResult(Ok ())
                                    let! summary = loadPlaylistSummary connection (Some transaction) playlistId token
                                    match summary with
                                    | Some value -> return Ok(AdminContentMutation.Applied value)
                                    | None -> return Ok AdminContentMutation.NotFound
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | :? PostgresException as ex when isUniqueViolation ex -> return Ok AdminContentMutation.Conflict
                    | :? PostgresException as ex when isForeignKeyViolation ex -> return Ok AdminContentMutation.NotFound
                    | ex -> return Error(databaseError "AdminContentRepository.updatePlaylist" ex)
                })
            cancellationToken
    let updatePlaylist
        (dataSource: NpgsqlDataSource)
        (playlistId: Guid)
        (update: PlaylistUpdate)
        (cancellationToken: CancellationToken) =
        updatePlaylistWithEvent dataSource playlistId update None cancellationToken

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
    let private activePlaylistSource (playlistId: Guid) (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (token: CancellationToken) : Task<PlaylistSource option> =
        task {
            use command = new NpgsqlCommand("SELECT \"Source\" FROM \"Playlists\" WHERE \"Id\" = @Id AND \"IsDeleted\" = false FOR UPDATE;", connection, transaction)
            command.Parameters.AddWithValue("Id", playlistId) |> ignore
            use! reader = command.ExecuteReaderAsync(token)
            let! found = reader.ReadAsync(token)
            return if found then Some(parsePlaylistSource (reader.GetString(0))) else None
        }

    let getPlaylistSource
        (dataSource: NpgsqlDataSource)
        (playlistId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<PlaylistSource option, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("SELECT \"Source\" FROM \"Playlists\" WHERE \"Id\" = @Id AND \"IsDeleted\" = false;", connection)
                command.Parameters.AddWithValue("Id", playlistId) |> ignore
                let! value = command.ExecuteScalarAsync(cancellationToken)
                return if isNull value then Ok None else Ok(Some(parsePlaylistSource(string value)))
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminContentRepository.getPlaylistSource" ex)
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
                        let! playlistSource = activePlaylistSource playlistId connection transaction token
                        let! trackExists = activeTrackExists item.TrackId connection transaction token
                        if playlistSource.IsNone || not trackExists then return Ok AdminContentMutation.NotFound
                        elif playlistSource = Some PlaylistSource.AllStorage then return Ok AdminContentMutation.Conflict
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
                            let! playlistSource = activePlaylistSource playlistId connection transaction token
                            if playlistSource.IsNone then return Ok AdminContentMutation.NotFound
                            elif playlistSource = Some PlaylistSource.AllStorage then return Ok AdminContentMutation.Conflict
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
