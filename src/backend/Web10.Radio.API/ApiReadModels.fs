namespace Web10.Radio.API

open System
open Dodo.Primitives
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.IO
open Npgsql
open Web10.Radio.Database.Repositories

type ManagedCoverRead =
    { CachePath: string
      ContentType: string
      SizeBytes: int64
      Sha256: string }

[<RequireQualifiedAccess>]
module TrackAssetReadModel =
    let tryCanonicalCachePath (cacheRoot: string) (cachePath: string) =
        try
            let root = Path.GetFullPath cacheRoot |> fun value -> value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            let candidate = Path.GetFullPath cachePath
            let prefix = root + string Path.DirectorySeparatorChar
            if candidate.StartsWith(prefix, StringComparison.Ordinal) then Some candidate else None
        with
        | :? ArgumentException
        | :? IOException
        | :? NotSupportedException -> None

    let tryGetManagedCover
        (dataSource: NpgsqlDataSource)
        (trackId: Guid)
        (cancellationToken: CancellationToken)
        : Task<Result<ManagedCoverRead option, RepositoryError>> =
        task {
            let! result = TrackRepository.tryGetActiveCover dataSource trackId cancellationToken
            return
                result
                |> Result.map (function
                    | Some cover when (cover.Source = "Embedded" || cover.Source = "Manual") ->
                        match cover.CachePath, cover.ContentType, cover.SizeBytes, cover.Sha256 with
                        | Some cachePath, Some contentType, Some sizeBytes, Some sha256
                            when contentType = "image/jpeg" || contentType = "image/png" || contentType = "image/webp" ->
                            Some
                                { CachePath = cachePath
                                  ContentType = contentType
                                  SizeBytes = sizeBytes
                                  Sha256 = sha256 }
                        | _ -> None
                    | _ -> None)
        }

module private ApiReadModelHelpers =
    let databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)

    let readNullableString (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetString(ordinal))

    let readStringOrEmpty (reader: NpgsqlDataReader) ordinal =
        readNullableString reader ordinal |> Option.defaultValue String.Empty

    let readNullableGuidString (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull(ordinal) then String.Empty else reader.GetGuid(ordinal).ToString("D")

    let readNullableInt32 (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetInt32(ordinal))

    let readNullableDateTimeOffset (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetFieldValue<DateTimeOffset>(ordinal))

    let clampToInt32 (value: int64) =
        if value > int64 Int32.MaxValue then Int32.MaxValue
        elif value < int64 Int32.MinValue then Int32.MinValue
        else int value

    let positiveMillisecondsBetween (startUtc: DateTimeOffset) (nowUtc: DateTimeOffset) =
        let elapsed = nowUtc - startUtc

        if elapsed.TotalMilliseconds <= 0.0 then
            0
        elif elapsed.TotalMilliseconds >= float Int32.MaxValue then
            Int32.MaxValue
        else
            int elapsed.TotalMilliseconds

    let mapStreamStatus value =
        match value with
        | "Starting" -> "starting"
        | "Live" -> "live"
        | "Degraded"
        | "Restarting"
        | "Failed" -> "degraded"
        | "Offline" -> "offline"
        | _ -> "offline"

    let mapQueueStatus value =
        match value with
        | "Queued" -> "queued"
        | "Claimed" -> "claimed"
        | "Playing" -> "playing"
        | "Played" -> "played"
        | "Failed" -> "failed"
        | _ -> "failed"

    let mapNowPlayingSource hasTrack value =
        if not hasTrack then
            "fallback"
        else
            match value with
            | "request" -> "request"
            | "playlist"
            | "admin" -> "library"
            | "fallback" -> "fallback"
            | _ -> "fallback"

    let bitrateFromMetadata metadataJson =
        if String.IsNullOrWhiteSpace metadataJson then
            0
        else
            try
                use document = JsonDocument.Parse(metadataJson)
                let mutable bitrate = Unchecked.defaultof<JsonElement>

                if document.RootElement.TryGetProperty("bitrateKbps", &bitrate) && bitrate.ValueKind = JsonValueKind.Number then
                    let mutable value = 0

                    if bitrate.TryGetInt32(&value) then max 0 value else 0
                else
                    0
            with _ ->
                0

type private StreamHeartbeatRow =
    { Status: string
      HeartbeatAtUtc: DateTimeOffset
      FailureReason: string option
      MetadataJson: string }

module PlayerStateReadModel =
    open ApiReadModelHelpers

    [<Literal>]
    let private latestHeartbeatSql = """SELECT "Status", "HeartbeatAtUtc", "FailureReason", "Metadata"
FROM "StreamNodeHeartbeats"
WHERE "IsDeleted" = false
ORDER BY "HeartbeatAtUtc" DESC, "CreatedAtUtc" DESC
LIMIT 1;"""

    [<Literal>]
    let private nowPlayingSql = """SELECT
    q."Id",
    q."TrackId",
    t."Title",
    t."Artist",
    t."Album",
    COALESCE(cover."CoverImageUrl", ''),
    t."DurationMs",
    q."Source",
    q."StartedAtUtc",
    link."Url"
FROM "PlaybackQueue" q
LEFT JOIN "Tracks" t ON t."Id" = q."TrackId" AND t."IsDeleted" = false
LEFT JOIN LATERAL (
    SELECT tl."Url"
    FROM "TrackLinks" tl
    WHERE tl."TrackId" = t."Id"
      AND tl."IsDeleted" = false
    ORDER BY CASE WHEN tl."IsPrimary" THEN 0 ELSE 1 END, tl."CreatedAtUtc" ASC
    LIMIT 1
) link ON true
LEFT JOIN LATERAL (
    SELECT CASE
        WHEN asset."Source" = 'LegacyExternal' THEN COALESCE(asset."ExternalUrl", '')
        WHEN asset."Sha256" IS NOT NULL THEN '/api/v0/player/assets/cover/' || t."Id"::text || '?v=' || asset."Sha256"
        ELSE ''
    END AS "CoverImageUrl"
    FROM "TrackAssets" asset
    WHERE asset."TrackId" = t."Id"
      AND asset."Kind" = 'Cover'
      AND asset."IsDeleted" = false
    LIMIT 1
) cover ON true
WHERE q."IsDeleted" = false
  AND q."Status" = 'Playing'
ORDER BY q."StartedAtUtc" DESC NULLS LAST, q."UpdatedAtUtc" DESC
LIMIT 1;"""

    [<Literal>]
    let private queueSql = """SELECT
    q."Id",
    q."TrackId",
    t."Title",
    t."Artist",
    q."Source",
    q."Status"
FROM "PlaybackQueue" q
LEFT JOIN "Tracks" t ON t."Id" = q."TrackId" AND t."IsDeleted" = false
WHERE q."IsDeleted" = false
  AND q."Status" IN ('Queued', 'Claimed', 'Playing')
ORDER BY q."Priority" DESC, q."RequestedAtUtc" ASC, q."CreatedAtUtc" ASC
LIMIT 20;"""

    [<Literal>]
    let private donationGoalSql = """SELECT "Title", "RaisedStars", "GoalStars"
FROM "DonationGoals"
WHERE "IsDeleted" = false
  AND "IsActive" = true
ORDER BY "UpdatedAtUtc" DESC, "CreatedAtUtc" DESC
LIMIT 1;"""

    [<Literal>]
    let private recentDonationsSql = """SELECT "Id", COALESCE("PayerDisplayName", 'anonymous'), "AmountStars", "PaidAtUtc"
FROM "Payments"
WHERE "IsDeleted" = false
  AND "Purpose" = 'Donation'
  AND "Status" = 'Paid'
  AND "PaidAtUtc" IS NOT NULL
ORDER BY "PaidAtUtc" DESC, "CreatedAtUtc" DESC, "Id" DESC
LIMIT 10;"""

    [<Literal>]
    let private topDonatorSql = """WITH winner AS (
    SELECT "TelegramUserId", SUM("AmountStars")::bigint AS "AmountStars"
    FROM "Payments"
    WHERE "IsDeleted" = false
      AND "Purpose" = 'Donation'
      AND "Status" = 'Paid'
      AND "PaidAtUtc" IS NOT NULL
    GROUP BY "TelegramUserId"
    ORDER BY SUM("AmountStars") DESC, "TelegramUserId" ASC
    LIMIT 1
)
SELECT COALESCE((
    SELECT payment."PayerDisplayName"
    FROM "Payments" AS payment
    WHERE payment."IsDeleted" = false
      AND payment."Purpose" = 'Donation'
      AND payment."Status" = 'Paid'
      AND payment."PaidAtUtc" IS NOT NULL
      AND payment."TelegramUserId" = winner."TelegramUserId"
      AND payment."PayerDisplayName" IS NOT NULL
      AND btrim(payment."PayerDisplayName") <> ''
    ORDER BY payment."PaidAtUtc" DESC, payment."CreatedAtUtc" DESC, payment."Id" DESC
    LIMIT 1
), 'anonymous') AS "DisplayName", winner."AmountStars"
FROM winner;"""

    [<Literal>]
    let private superChatSql = """SELECT "Id", "DisplayName", "Text", "AmountStars", "Color", "SubmittedAtUtc"
FROM "SayMessages"
WHERE "IsDeleted" = false
  AND "Status" = 'Approved'
ORDER BY "SubmittedAtUtc" DESC, "CreatedAtUtc" DESC
LIMIT 10;"""

    [<Literal>]
    let private socialsSql = """SELECT "Id", "Kind", "Name", "Handle", "Url", "Glyph", "Color", "QrImageUrl", "IsFeatured"
FROM "SocialLinks"
WHERE "IsDeleted" = false
ORDER BY "Position" ASC, "CreatedAtUtc" ASC;"""

    [<Literal>]
    let private bannersSql = """SELECT "Id", "Type", "Title", "Subtitle", "Text", "Style", "ScreenPosition", "Accent", "Enabled", "SortOrder", "RotationSeconds"
FROM "Banners"
WHERE "IsDeleted" = false
ORDER BY "SortOrder" ASC, "CreatedAtUtc" ASC;"""

    [<Literal>]
    let private playbackStateSql = """SELECT "DesiredState"
FROM "StreamNodeControlState"
WHERE "SingletonKey" = 'primary' AND "IsDeleted" = false
LIMIT 1;"""

    [<Literal>]
    let private streamFileSql = """SELECT tf."CachePath", COALESCE(tf."ContentType", 'audio/mpeg')
FROM "PlaybackQueue" q
INNER JOIN "Tracks" t ON t."Id" = q."TrackId" AND t."IsDeleted" = false
INNER JOIN "TrackFiles" tf ON tf."TrackId" = t."Id"
WHERE q."IsDeleted" = false
  AND q."Status" = 'Playing'
  AND tf."IsDeleted" = false
  AND tf."IsCached" = true
  AND tf."CachePath" IS NOT NULL
  AND btrim(tf."CachePath") <> ''
ORDER BY q."StartedAtUtc" DESC NULLS LAST, tf."UpdatedAtUtc" DESC, tf."CreatedAtUtc" DESC
LIMIT 1;"""

    let private emptyNowPlaying nowUtc =
        { TrackId = "01920000-0000-7000-8000-0000000000ff"
          Title = String.Empty
          Artist = String.Empty
          Album = String.Empty
          Source = "fallback"
          ExternalUrl = String.Empty
          CoverImageUrl = String.Empty
          DurationMs = 0
          PositionMs = 0
          StartedAtUtc = ApiTime.toIsoUtc nowUtc }

    let private fallbackDonationGoal () =
        { Title = "Цель сбора"
          RaisedStars = 0
          GoalStars = 5000
          TopDonator = Unchecked.defaultof<TopDonatorDto>
          Recent = [] }

    let private overlayDefaults =
        { Style = "aero"
          Layout = "corners" }

    let private streamStateFromHeartbeat (timeProvider: TimeProvider) heartbeat =
        let nowUtc = timeProvider.GetUtcNow()

        match heartbeat with
        | None ->
            { Status = "offline"
              PublicAudioUrl = "/api/v0/player/stream"
              RtmpRelay = "telegram"
              BitrateKbps = 0
              StartedAtUtc = ApiTime.toIsoUtc nowUtc
              OfflineReason = "stream-node not connected" }
        | Some heartbeat when not (PersistedHeartbeatFreshness.isFresh nowUtc heartbeat.HeartbeatAtUtc) ->
            { Status = "offline"
              PublicAudioUrl = "/api/v0/player/stream"
              RtmpRelay = "telegram"
              BitrateKbps = 0
              StartedAtUtc = ApiTime.toIsoUtc heartbeat.HeartbeatAtUtc
              OfflineReason = "stream-node heartbeat stale" }
        | Some heartbeat ->
            let status = mapStreamStatus heartbeat.Status
            let offlineReason =
                match status with
                | "offline" -> heartbeat.FailureReason |> Option.defaultValue "stream-node not connected"
                | "degraded" -> heartbeat.FailureReason |> Option.defaultValue "stream degraded"
                | _ -> null

            let bitrate =
                match status with
                | "live"
                | "degraded" -> bitrateFromMetadata heartbeat.MetadataJson
                | _ -> 0

            { Status = status
              PublicAudioUrl = "/api/v0/player/stream"
              RtmpRelay = "telegram"
              BitrateKbps = bitrate
              StartedAtUtc = ApiTime.toIsoUtc heartbeat.HeartbeatAtUtc
              OfflineReason = offlineReason }

    let private isStreamReadableHeartbeat (timeProvider: TimeProvider) heartbeat =
        PersistedHeartbeatFreshness.isFresh (timeProvider.GetUtcNow()) heartbeat.HeartbeatAtUtc
        && match mapStreamStatus heartbeat.Status with
           | "live"
           | "degraded" -> true
           | _ -> false

    let private loadLatestHeartbeat (connection: NpgsqlConnection) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand(latestHeartbeatSql, connection)
            let! reader = command.ExecuteReaderAsync(cancellationToken)
            use reader = reader
            let! hasRow = reader.ReadAsync(cancellationToken)

            if hasRow then
                return
                    Some
                        { Status = reader.GetString(0)
                          HeartbeatAtUtc = reader.GetFieldValue<DateTimeOffset>(1)
                          FailureReason = readNullableString reader 2
                          MetadataJson = reader.GetString(3) }
            else
                return None
        }

    let private loadNowPlayingFromConnection (connection: NpgsqlConnection) (timeProvider: TimeProvider) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand(nowPlayingSql, connection)
            let! reader = command.ExecuteReaderAsync(cancellationToken)
            use reader = reader
            let! hasRow = reader.ReadAsync(cancellationToken)
            let nowUtc = timeProvider.GetUtcNow()
            if not hasRow then
                return emptyNowPlaying nowUtc
            else
                let hasTrack = not (reader.IsDBNull(1))
                let startedAtUtc = readNullableDateTimeOffset reader 8
                let startedForContract = startedAtUtc |> Option.defaultValue nowUtc
                let source = mapNowPlayingSource hasTrack (reader.GetString(7))
                let durationMs = readNullableInt32 reader 6 |> Option.defaultValue 0
                let elapsedMs = startedAtUtc |> Option.map (fun started -> positiveMillisecondsBetween started nowUtc) |> Option.defaultValue 0
                let positionMs = if durationMs > 0 then min elapsedMs durationMs else elapsedMs

                return
                    { TrackId = if hasTrack then reader.GetGuid(1).ToString("D") else "01920000-0000-7000-8000-0000000000ff"
                      Title = readStringOrEmpty reader 2
                      Artist = readStringOrEmpty reader 3
                      Album = readStringOrEmpty reader 4
                      Source = source
                      ExternalUrl = readStringOrEmpty reader 9
                      CoverImageUrl = readStringOrEmpty reader 5
                      DurationMs = durationMs
                      PositionMs = positionMs
                      StartedAtUtc = ApiTime.toIsoUtc startedForContract }
        }

    let private loadQueueFromConnection (connection: NpgsqlConnection) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand(queueSql, connection)
            let! reader = command.ExecuteReaderAsync(cancellationToken)
            use reader = reader
            let items = ResizeArray<QueueItemDto>()
            let mutable currentQueueItemId = String.Empty
            let mutable keepReading = true

            while keepReading do
                let! hasRow = reader.ReadAsync(cancellationToken)

                if hasRow then
                    let status = mapQueueStatus (reader.GetString(5))
                    let queueItemId = reader.GetGuid(0).ToString("D")

                    if status = "playing" && String.IsNullOrEmpty currentQueueItemId then
                        currentQueueItemId <- queueItemId

                    items.Add
                        { QueueItemId = queueItemId
                          TrackId = readNullableGuidString reader 1
                          Title = readStringOrEmpty reader 2
                          Artist = readStringOrEmpty reader 3
                          Source = reader.GetString(4)
                          Status = status }
                else
                    keepReading <- false

            return
                { CurrentQueueItemId = currentQueueItemId
                  Items = List.ofSeq items }
        }

    let private loadRecentDonations (connection: NpgsqlConnection) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand(recentDonationsSql, connection)
            let! reader = command.ExecuteReaderAsync(cancellationToken)
            use reader = reader
            let donations = ResizeArray<RecentDonationDto>()
            let mutable keepReading = true

            while keepReading do
                let! hasRow = reader.ReadAsync(cancellationToken)

                if hasRow then
                    donations.Add
                        { Id = reader.GetGuid(0).ToString("D")
                          DisplayName = reader.GetString(1)
                          AmountStars = reader.GetInt32(2)
                          PaidAtUtc = ApiTime.toIsoUtc (reader.GetFieldValue<DateTimeOffset>(3)) }
                else
                    keepReading <- false

            return List.ofSeq donations
        }

    let private loadTopDonator (connection: NpgsqlConnection) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand(topDonatorSql, connection)
            let! reader = command.ExecuteReaderAsync(cancellationToken)
            use reader = reader
            let! hasRow = reader.ReadAsync(cancellationToken)

            if hasRow then
                return
                    { DisplayName = reader.GetString(0)
                      AmountStars = clampToInt32 (reader.GetInt64(1)) }
            else
                return Unchecked.defaultof<TopDonatorDto>
        }

    let private loadDonationGoalFromConnection (connection: NpgsqlConnection) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand(donationGoalSql, connection)
            let! reader = command.ExecuteReaderAsync(cancellationToken)
            use reader = reader
            let! hasRow = reader.ReadAsync(cancellationToken)
            let mutable goal = fallbackDonationGoal ()

            if hasRow then
                goal <-
                    { goal with
                        Title = reader.GetString(0)
                        RaisedStars = reader.GetInt32(1)
                        GoalStars = reader.GetInt32(2) }

            do! reader.CloseAsync()
            let! recent = loadRecentDonations connection cancellationToken
            let! topDonator = loadTopDonator connection cancellationToken

            return
                { goal with
                    TopDonator = topDonator
                    Recent = recent }
        }

    let private loadSuperChatFromConnection (connection: NpgsqlConnection) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand(superChatSql, connection)
            let! reader = command.ExecuteReaderAsync(cancellationToken)
            use reader = reader
            let messages = ResizeArray<SuperChatMessageDto>()
            let mutable keepReading = true

            while keepReading do
                let! hasRow = reader.ReadAsync(cancellationToken)

                if hasRow then
                    messages.Add
                        { Id = reader.GetGuid(0).ToString("D")
                          DisplayName = reader.GetString(1)
                          Text = reader.GetString(2)
                          AmountStars = reader.GetInt32(3)
                          Color = readNullableString reader 4 |> Option.defaultValue "#e0439a"
                          SubmittedAtUtc = ApiTime.toIsoUtc (reader.GetFieldValue<DateTimeOffset>(5))
                          Status = "approved" }
                else
                    keepReading <- false

            return { Messages = List.ofSeq messages }
        }

    let private loadSocialsFromConnection (connection: NpgsqlConnection) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand(socialsSql, connection)
            let! reader = command.ExecuteReaderAsync(cancellationToken)
            use reader = reader
            let socials = ResizeArray<SocialLinkDto>()
            let mutable keepReading = true

            while keepReading do
                let! hasRow = reader.ReadAsync(cancellationToken)

                if hasRow then
                    socials.Add
                        { Id = reader.GetGuid(0).ToString("D")
                          Kind = reader.GetString(1)
                          Name = reader.GetString(2)
                          Handle = readStringOrEmpty reader 3
                          Url = reader.GetString(4)
                          Glyph = readStringOrEmpty reader 5
                          Color = readStringOrEmpty reader 6
                          QrImageUrl = readStringOrEmpty reader 7
                          IsFeatured = reader.GetBoolean(8) }
                else
                    keepReading <- false

            return List.ofSeq socials
        }

    let private loadBannersFromConnection (connection: NpgsqlConnection) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand(bannersSql, connection)
            let! reader = command.ExecuteReaderAsync(cancellationToken)
            use reader = reader
            let banners = ResizeArray<BannerDto>()
            let mutable keepReading = true

            while keepReading do
                let! hasRow = reader.ReadAsync(cancellationToken)

                if hasRow then
                    banners.Add
                        { Id = reader.GetGuid(0).ToString("D")
                          Type = reader.GetString(1)
                          Title = reader.GetString(2)
                          Subtitle = readStringOrEmpty reader 3
                          Text = readStringOrEmpty reader 4
                          Style = reader.GetString(5)
                          ScreenPosition = reader.GetString(6)
                          Accent = readStringOrEmpty reader 7
                          Enabled = reader.GetBoolean(8)
                          SortOrder = reader.GetInt32(9)
                          RotationSeconds = if reader.IsDBNull(10) then 0 else reader.GetInt32(10) }
                else
                    keepReading <- false

            return List.ofSeq banners
        }

    let private loadPlaybackStateFromConnection (connection: NpgsqlConnection) (cancellationToken: CancellationToken) =
        task {
            use command = new NpgsqlCommand(playbackStateSql, connection)
            let! value = command.ExecuteScalarAsync(cancellationToken)
            return
                match value with
                | :? string as desired ->
                    match desired with
                    | "Paused" -> "paused"
                    | "Stopped" -> "stopped"
                    | _ -> "playing"
                | _ -> "playing"
        }

    let loadSnapshot (dataSource: NpgsqlDataSource) (timeProvider: TimeProvider) (cancellationToken: CancellationToken) : Task<Result<PlayerStateDto, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                let serverTimeUtc = timeProvider.GetUtcNow()
                let! heartbeat = loadLatestHeartbeat connection cancellationToken
                let! nowPlaying = loadNowPlayingFromConnection connection timeProvider cancellationToken
                let! donationGoal = loadDonationGoalFromConnection connection cancellationToken
                let! superChat = loadSuperChatFromConnection connection cancellationToken
                let! socials = loadSocialsFromConnection connection cancellationToken
                let! banners = loadBannersFromConnection connection cancellationToken
                let! playbackState = loadPlaybackStateFromConnection connection cancellationToken
                let! queue = loadQueueFromConnection connection cancellationToken

                return
                    Ok
                        { ServerTimeUtc = ApiTime.toIsoUtc serverTimeUtc
                          Stream = streamStateFromHeartbeat timeProvider heartbeat
                          NowPlaying = nowPlaying
                          Queue = queue
                          DonationGoal = donationGoal
                          SuperChat = superChat
                          Socials = socials
                          Overlay = overlayDefaults
                          Banners = banners
                          PlaybackState = playbackState }
            with ex ->
                return Error(databaseError "PlayerStateReadModel.loadSnapshot" ex)
        }

    let loadStreamHealth (dataSource: NpgsqlDataSource) (timeProvider: TimeProvider) (cancellationToken: CancellationToken) : Task<Result<StreamHealthDto, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                let! heartbeat = loadLatestHeartbeat connection cancellationToken
                let stream = streamStateFromHeartbeat timeProvider heartbeat
                let lastHeartbeatUtc = heartbeat |> Option.map (fun row -> ApiTime.toIsoUtc row.HeartbeatAtUtc) |> Option.defaultValue null

                return
                    Ok
                        { Status = stream.Status
                          LastHeartbeatUtc = lastHeartbeatUtc
                          OfflineReason = stream.OfflineReason
                          TraceId = String.Empty }
            with ex ->
                return Error(databaseError "PlayerStateReadModel.loadStreamHealth" ex)
        }

    let loadCurrentSong (dataSource: NpgsqlDataSource) (timeProvider: TimeProvider) (cancellationToken: CancellationToken) : Task<Result<CurrentSongDto, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                let! nowPlaying = loadNowPlayingFromConnection connection timeProvider cancellationToken
                let fallbackText =
                    if String.IsNullOrEmpty nowPlaying.Artist && String.IsNullOrEmpty nowPlaying.Title then
                        String.Empty
                    else
                        sprintf "%s — %s" nowPlaying.Artist nowPlaying.Title

                return
                    Ok
                        { Title = nowPlaying.Title
                          Artist = nowPlaying.Artist
                          ExternalUrl = nowPlaying.ExternalUrl
                          FallbackText = fallbackText }
            with ex ->
                return Error(databaseError "PlayerStateReadModel.loadCurrentSong" ex)
        }

    let loadDonationGoal (dataSource: NpgsqlDataSource) (cancellationToken: CancellationToken) : Task<Result<DonationGoalDto, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                let! donationGoal = loadDonationGoalFromConnection connection cancellationToken
                return Ok donationGoal
            with ex ->
                return Error(databaseError "PlayerStateReadModel.loadDonationGoal" ex)
        }

    let loadSocialLinks (dataSource: NpgsqlDataSource) (cancellationToken: CancellationToken) : Task<Result<SocialLinkDto list, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                let! socials = loadSocialsFromConnection connection cancellationToken
                return Ok socials
            with ex ->
                return Error(databaseError "PlayerStateReadModel.loadSocialLinks" ex)
        }

    let loadStreamFile (dataSource: NpgsqlDataSource) (timeProvider: TimeProvider) (cancellationToken: CancellationToken) : Task<Result<StreamFileDto option, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                let! heartbeat = loadLatestHeartbeat connection cancellationToken

                match heartbeat with
                | Some current when isStreamReadableHeartbeat timeProvider current ->
                    use command = new NpgsqlCommand(streamFileSql, connection)
                    let! reader = command.ExecuteReaderAsync(cancellationToken)
                    use reader = reader
                    let! hasRow = reader.ReadAsync(cancellationToken)

                    if hasRow then
                        return
                            Ok(
                                Some
                                    { CachePath = reader.GetString(0)
                                      ContentType = readNullableString reader 1 |> Option.defaultValue "audio/mpeg" }
                            )
                    else
                        return Ok None
                | _ ->
                    return Ok None
            with ex ->
                return Error(databaseError "PlayerStateReadModel.loadStreamFile" ex)
        }

module TelegramWebhookInbox =
    let tryRecordRaw
        (dataSource: NpgsqlDataSource)
        (timeProvider: TimeProvider)
        (telegramUpdateId: int64)
        (payloadJson: string)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        let record =
            { Id = Uuid.CreateVersion7().ToGuidBigEndian()
              TelegramUpdateId = telegramUpdateId
              EventType = "telegram.webhook"
              ReceivedAtUtc = timeProvider.GetUtcNow()
              CorrelationId = None
              PayloadJson = payloadJson }

        TelegramUpdateInboxRepository.tryRecord dataSource record cancellationToken
