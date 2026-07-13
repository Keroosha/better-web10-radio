namespace Web10.Radio.StreamNode

open System
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type DesiredState =
    | Running
    | Stopped

[<RequireQualifiedAccess>]
type CallbackResult =
    | Accepted
    | Stale
    | Unauthorized
    | TransientError

[<RequireQualifiedAccess>]
type PlaybackAction =
    | Skip
    | Restart

[<RequireQualifiedAccess>]
type PlaybackCompletion =
    | Played
    | Failed of reason: string

[<RequireQualifiedAccess>]
type CallbackName =
    | Started
    | Ended
    | OutputFailed
    | ProtocolError
    | StartTimeout

[<CLIMutable>]
type RuntimeConfig =
    { ApiBaseUrl: string
      CallbackToken: string
      StageUrl: string
      RtmpUrl: string
      RtmpKey: string
      Display: string
      Width: int
      Height: int
      Framerate: int
      BitrateKbps: int
      StorageRoot: string
      CacheRoot: string
      CallbackPort: int
      LiquidsoapSocket: string }

type Config = RuntimeConfig

[<CLIMutable>]
type ControlState =
    { DesiredState: string
      RestartGeneration: int64 }

[<CLIMutable>]
type PlaybackCommand =
    { Generation: int64
      Action: string
      QueueItemId: Guid
      ClaimOwner: Guid
      ClaimAttempt: int }

[<CLIMutable>]
type ControlPage =
    { DesiredState: string
      RestartGeneration: int64
      PlaybackCommands: PlaybackCommand array
      NextPlaybackGeneration: int64 }

[<CLIMutable>]
type Assignment =
    { QueueItemId: Guid
      ClaimOwner: Guid
      ClaimAttempt: int
      TrackId: Guid
      ContentType: string
      Title: string
      Artist: string
      DurationMs: int
      CueStartMs: int option
      CueDurationMs: int option }

module Assignment =
    let identity (assignment: Assignment) =
        assignment.QueueItemId, assignment.ClaimOwner, assignment.ClaimAttempt

[<CLIMutable>]
type HeartbeatMetadata =
    { BitrateKbps: int option
      RestartAttempt: int option
      ActiveQueueItemId: Guid option }

[<CLIMutable>]
type Heartbeat =
    { Status: string
      FailureReason: string option
      Metadata: HeartbeatMetadata }

type BackendError =
    | Transport of operation: string
    | Http of operation: string * status: int
    | InvalidResponse of operation: string
    | UnauthorizedResponse of operation: string

exception ConfigurationException of key: string

module Monotonic =
    let timestamp (timeProvider: TimeProvider) = timeProvider.GetTimestamp()

    let elapsed (timeProvider: TimeProvider) (startTimestamp: int64) =
        timeProvider.GetElapsedTime(startTimestamp)

    let deadline (timeProvider: TimeProvider) (duration: TimeSpan) =
        timeProvider.GetTimestamp(), duration

    let remaining (timeProvider: TimeProvider) (startTimestamp: int64) (duration: TimeSpan) =
        let left = duration - elapsed timeProvider startTimestamp
        if left <= TimeSpan.Zero then TimeSpan.Zero else left

    let delay (timeProvider: TimeProvider) (duration: TimeSpan) (cancellationToken: CancellationToken) =
        if duration <= TimeSpan.Zero then Task.CompletedTask
        else Task.Delay(duration, timeProvider, cancellationToken)

module SafeText =
    let boundedReason (reason: string) =
        let value = if isNull reason then "Stream playback failed" else reason.Trim()
        if String.IsNullOrWhiteSpace value then "Stream playback failed"
        elif value.Length <= 500 then value
        else value.Substring(0, 500)

    let redactException (_: exn) = "operation failed"
module ResultWorkflow =
    type ResultBuilder() =
        member _.Bind(value, continuation) = Result.bind continuation value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value: Result<'T, 'Error>) = value
        member _.Zero() = Ok ()

    let result = ResultBuilder()
