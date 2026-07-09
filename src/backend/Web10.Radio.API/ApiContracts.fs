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

type PlayerStateDto =
    { ServerTimeUtc: string
      Stream: StreamStateDto
      NowPlaying: NowPlayingDto
      Queue: QueueStateDto
      DonationGoal: DonationGoalDto
      SuperChat: SuperChatStateDto
      Socials: SocialLinkDto list
      Overlay: OverlaySettingsDto }

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

type TelegramHealthDto =
    { IsConfigured: bool
      ChannelIdOrUsername: string
      LastUpdateId: int64 Nullable
      LastError: string }

type StreamFileDto =
    { CachePath: string
      ContentType: string }
