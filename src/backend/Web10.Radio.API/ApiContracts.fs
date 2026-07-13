namespace Web10.Radio.API

open System
open System.Diagnostics
open System.Globalization
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

[<RequireQualifiedAccess>]
module ApiTime =
    let toIsoUtc (value: DateTimeOffset) =
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)

[<RequireQualifiedAccess>]
module ApiTrace =
    let traceId (context: HttpContext) =
        let current = Activity.Current

        if isNull current then
            context.TraceIdentifier
        else
            let currentTraceId = current.TraceId.ToString()

            if String.IsNullOrWhiteSpace currentTraceId then
                context.TraceIdentifier
            else
                currentTraceId

[<RequireQualifiedAccess>]
module ApiJson =
    [<Literal>]
    let JsonContentType = "application/json; charset=utf-8"

    [<Literal>]
    let ProblemContentType = "application/problem+json; charset=utf-8"

    let options =
        let options = JsonSerializerOptions(JsonSerializerDefaults.Web)
        options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        options.DictionaryKeyPolicy <- JsonNamingPolicy.CamelCase
        options

    let write (context: HttpContext) (statusCode: int) (contentType: string) (value: 'T) : Task =
        task {
            context.Response.StatusCode <- statusCode
            context.Response.ContentType <- contentType
            do! JsonSerializer.SerializeAsync(context.Response.Body, value, options, context.RequestAborted)
        }
        :> Task

    let toElement (value: 'T) =
        JsonSerializer.SerializeToElement(value, options)

type ProblemDetailsDto =
    { Type: string
      Title: string
      Status: int
      TraceId: string
      Code: string
      Message: string }

[<RequireQualifiedAccess>]
module ApiProblems =
    let private problemType (code: string) =
        sprintf "https://web10.radio/problems/%s" (code.Replace('.', '-'))

    let write (context: HttpContext) (status: int) (code: string) (title: string) (message: string) : Task =
        let problem =
            { Type = problemType code
              Title = title
              Status = status
              TraceId = ApiTrace.traceId context
              Code = code
              Message = message }

        ApiJson.write context status ApiJson.ProblemContentType problem

type StreamStateDto =
    { Status: string
      PublicAudioUrl: string
      RtmpRelay: string
      BitrateKbps: int
      StartedAtUtc: string
      OfflineReason: string }

type NowPlayingDto =
    { TrackId: string
      Title: string
      Artist: string
      Album: string
      Source: string
      ExternalUrl: string
      CoverImageUrl: string
      DurationMs: int
      PositionMs: int
      StartedAtUtc: string }

type QueueItemDto =
    { QueueItemId: string
      TrackId: string
      Title: string
      Artist: string
      Source: string
      Status: string }

type QueueStateDto =
    { CurrentQueueItemId: string
      Items: QueueItemDto list }

type TopDonatorDto =
    { DisplayName: string
      AmountStars: int }

type RecentDonationDto =
    { Id: string
      DisplayName: string
      AmountStars: int
      PaidAtUtc: string }

type DonationGoalDto =
    { Title: string
      RaisedStars: int
      GoalStars: int
      TopDonator: TopDonatorDto
      Recent: RecentDonationDto list }

type SuperChatMessageDto =
    { Id: string
      DisplayName: string
      Text: string
      AmountStars: int
      Color: string
      SubmittedAtUtc: string
      Status: string }

type SuperChatStateDto =
    { Messages: SuperChatMessageDto list }

type SocialLinkDto =
    { Id: string
      Kind: string
      Name: string
      Handle: string
      Url: string
      Glyph: string
      Color: string
      QrImageUrl: string
      IsFeatured: bool }

type OverlaySettingsDto =
    { Style: string
      Layout: string }

type BannerDto =
    { Id: string
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

type PlayerStateDto =
    { ServerTimeUtc: string
      Stream: StreamStateDto
      NowPlaying: NowPlayingDto
      Queue: QueueStateDto
      DonationGoal: DonationGoalDto
      SuperChat: SuperChatStateDto
      Socials: SocialLinkDto list
      Overlay: OverlaySettingsDto
      Banners: BannerDto list
      PlaybackState: string }

type StreamHealthDto =
    { Status: string
      LastHeartbeatUtc: string
      OfflineReason: string
      TraceId: string }

type CurrentSongDto =
    { Title: string
      Artist: string
      ExternalUrl: string
      FallbackText: string }


type StreamFileDto =
    { CachePath: string
      ContentType: string }


type AdminAuthSessionDto =
    { Username: string
      CsrfToken: string
      DevelopmentFixturesEnabled: bool }

type LibraryScanAcceptedDto =
    { ScanJobId: string }

type LibraryScanStatusDto =
    { ScanJobId: string
      Status: string
      DiscoveredCount: int
      RequestedAtUtc: string
      StartedAtUtc: string
      FinishedAtUtc: string
      FailureReason: string }

type AdminTrackDto =
    { Id: string
      Title: string
      Artist: string
      Album: string
      DurationMs: int
      HasCachedFile: bool
      CoverImageUrl: string
      MetadataSource: string
      StorageBackendId: string }

type AdminTrackPageDto =
    { Items: AdminTrackDto list
      NextCursor: string }

type QueueReorderRequestDto =
    { QueueItemIds: string list }

type PlaybackQueueAcceptedDto =
    { QueueItemId: string }

type StreamNodePlaybackCommandDto =
    { Generation: int64
      Action: string
      QueueItemId: string
      ClaimOwner: string
      ClaimAttempt: int }

type StreamNodeControlDto =
    { DesiredState: string
      RestartGeneration: int
      PlaybackCommands: StreamNodePlaybackCommandDto list
      NextPlaybackGeneration: int64 }
type StreamNodeStatusDto =
    { Status: string
      DesiredState: string
      LastHeartbeatUtc: string
      FailureReason: string
      BitrateKbps: int
      RestartGeneration: int }

type CurrentPlaybackAssignmentDto =
    { QueueItemId: string
      ClaimOwner: string
      ClaimAttempt: int
      TrackId: string
      CachePath: string
      ContentType: string
      Title: string
      Artist: string
      DurationMs: int }

type PlaylistScheduleDto =
    { Id: string
      DaysOfWeek: int list
      StartTime: string
      EndTime: string
      StartDate: string
      EndDate: string
      TimeZoneId: string }

type PlaylistSummaryDto =
    { Id: string
      Name: string
      Description: string
      IsActive: bool
      Type: string
      Source: string
      Order: string
      Weight: int
      IsJingle: bool
      Interrupt: bool
      AvoidDuplicates: bool
      PlayEverySongs: int option
      PlayEveryMinutes: int option
      PlayAtMinute: int option
      IsSystem: bool
      ItemCount: int
      Schedules: PlaylistScheduleDto list }

type PlaylistItemDto =
    { Id: string
      TrackId: string
      Title: string
      Artist: string
      Position: int }

type DefaultStorageBackendDto =
    { Type: string
      LocalRoot: string
      S3Bucket: string
      S3Region: string
      S3ServiceUrl: string
      S3ForcePathStyle: bool }

type AdditionalStorageBackendDto =
    { Id: string
      Name: string
      Type: string
      LocalRoot: string
      S3Bucket: string
      IsEnabled: bool }

type AdminStorageDto =
    { DefaultBackend: DefaultStorageBackendDto
      AdditionalBackends: AdditionalStorageBackendDto list }

type StorageEntryDto =
    { Path: string
      Name: string
      Kind: string
      SizeBytes: int64 option
      LastModifiedUtc: string option
      ContentType: string option }

type StorageEntryPageDto =
    { Path: string
      Items: StorageEntryDto list
      NextCursor: string option }

type StorageDeleteEntryDto =
    { Path: string
      Kind: string }

type StorageDeleteRequestDto =
    { StorageBackendId: string option
      Entries: StorageDeleteEntryDto list }

type StorageDeleteConfirmRequestDto =
    { StorageBackendId: string option
      Entries: StorageDeleteEntryDto list
      ImpactToken: string }

type StoragePlaylistMembershipDto =
    { PlaylistId: string
      PlaylistName: string
      TrackCount: int }

type StorageSampleTrackDto =
    { TrackId: string
      Title: string
      Artist: string
      PlaylistNames: string list }

type StorageCurrentTrackDto =
    { TrackId: string
      Title: string
      Artist: string }

type StorageDeleteImpactDto =
    { FileCount: int
      FolderCount: int
      TotalBytes: int64
      TrackedFileCount: int
      TracksToDeleteCount: int
      PlaylistMemberships: StoragePlaylistMembershipDto list
      SampleTracks: StorageSampleTrackDto list
      SampleTracksTruncated: bool
      CurrentTrack: StorageCurrentTrackDto option
      ImpactToken: string }

type StorageDeleteResultDto =
    { DeletedFileCount: int
      DeletedFolderCount: int
      DetachedPlaylistItemCount: int
      DeletedTrackCount: int
      PlaybackAdvanced: bool }

type PaidVerticalSliceFixtureResponseDto =
    { DonationPaymentId: string
      SayPaymentId: string
      SayMessageId: string }
