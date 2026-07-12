namespace Web10.Radio.StreamNode.Tools

open System
open System.Threading
open System.Threading.Tasks

[<CLIMutable>]
type FakeOptions =
    { BaseUrl: string
      CallbackToken: string
      HeartbeatSeconds: float
      MaxTrackSeconds: float
      BitrateKbps: int
      Status: string
      Advance: bool
      RunSeconds: float option }

module FakeMode =
    let private environment name fallback =
        match Environment.GetEnvironmentVariable name with
        | null -> fallback
        | value when String.IsNullOrWhiteSpace value -> fallback
        | value -> value

    let options (arguments: Map<string, string>) =
        let value name envName fallback = Parsing.option name (environment envName fallback) arguments
        let baseUrl = value "base-url" "WEB10_API__BASE_URL" "http://localhost:8080"
        let token = value "callback-token" "WEB10_STREAM__CALLBACK_TOKEN" ""
        if String.IsNullOrWhiteSpace token then raise (ToolError("WEB10_STREAM__CALLBACK_TOKEN", "required"))
        let heartbeat = Parsing.positiveFloat "heartbeat-seconds" (value "heartbeat-seconds" "WEB10_FAKE_HEARTBEAT_SECONDS" "10")
        let maxTrack = Parsing.positiveFloat "max-track-seconds" (value "max-track-seconds" "WEB10_FAKE_MAX_TRACK_SECONDS" "30")
        let bitrate = Parsing.positiveInt "bitrate-kbps" (value "bitrate-kbps" "WEB10_FAKE_BITRATE_KBPS" "192")
        let status = value "status" "WEB10_FAKE_STATUS" "live"
        let status = Parsing.requireChoice "status" (set [ "live"; "starting" ]) status
        let advance = Parsing.boolValue "advance" (value "advance" "WEB10_FAKE_ADVANCE" "true")
        let runSeconds =
            match Map.tryFind "run-seconds" arguments with
            | None -> None
            | Some value -> Some(Parsing.positiveFloat "run-seconds" value)
        { BaseUrl = baseUrl.TrimEnd('/')
          CallbackToken = token
          HeartbeatSeconds = heartbeat
          MaxTrackSeconds = maxTrack
          BitrateKbps = bitrate
          Status = status
          Advance = advance
          RunSeconds = runSeconds }

    let private runDeadline provider options =
        let timeout = options.RunSeconds |> Option.defaultValue (365.0 * 24.0 * 60.0 * 60.0) |> TimeSpan.FromSeconds
        Time.create provider timeout

    let run (provider: TimeProvider) (settings: FakeOptions) (cancellationToken: CancellationToken) =
        task {
            let client = new BackendConnection(settings.BaseUrl, settings.CallbackToken, bitrateKbps = settings.BitrateKbps)
            let deadline = runDeadline provider settings
            let mutable current: Assignment option = None
            let mutable startedAt = provider.GetTimestamp()
            let mutable running = true
            try
                while running && not cancellationToken.IsCancellationRequested && Time.remaining deadline > TimeSpan.Zero do
                    let! assignment = client.GetAssignment(deadline, cancellationToken)
                    match assignment with
                    | Some value ->
                        if current |> Option.map (fun item -> item.QueueItemId) <> Some value.QueueItemId then
                            current <- Some value
                            startedAt <- provider.GetTimestamp()
                            printfn "fake now-playing track=%s" value.QueueItemId
                        do!
                            client.Heartbeat(
                                settings.Status,
                                None,
                                Some 0,
                                Some value.QueueItemId,
                                deadline,
                                cancellationToken
                            )
                        do! client.RenewLease(value, deadline, cancellationToken)
                        let duration =
                            let fromMetadata = float value.DurationMs / 1000.0
                            max 1.0 (min settings.MaxTrackSeconds (if fromMetadata > 0.0 then fromMetadata else 1.0))
                        if settings.Advance && provider.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds duration then
                            do! client.Complete(value, "played", deadline, cancellationToken)
                            current <- None
                    | None ->
                        current <- None
                        do! client.Heartbeat(settings.Status, None, Some 0, None, deadline, cancellationToken)
                    do! Time.delay provider (TimeSpan.FromSeconds settings.HeartbeatSeconds) cancellationToken
                running <- false
            with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                running <- false
            let offlineDeadline = Time.create provider (TimeSpan.FromSeconds 5.0)
            try
                do!
                    client.Heartbeat(
                        "offline",
                        None,
                        Some 0,
                        None,
                        offlineDeadline,
                        CancellationToken.None
                    )
            with _ -> ()
        }
