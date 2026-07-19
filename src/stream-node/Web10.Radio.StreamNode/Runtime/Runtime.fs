namespace Web10.Radio.StreamNode

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type RuntimeFailure =
    | BackendAuthorization
    | RtmpOutput
    | RestartBudget
    | Pipeline

[<CLIMutable>]
type RuntimeSnapshot =
    { DesiredState: string
      Status: string
      RestartGeneration: int64
      PlaybackGeneration: int64
      TerminalFailure: bool
      ActiveQueueItemId: Guid option
      RestartAttempt: int option
      FailureReason: string option
      LastHeartbeatUtc: DateTimeOffset }

module Runtime =
    // Custom media protocols materialize the whole source before Liquidsoap emits on_track.
    // Allow large Local/S3 objects to finish resolving without falsely failing live playback.
    let callbackStartDeadline = TimeSpan.FromSeconds 60.0
    let pollCadence = TimeSpan.FromSeconds 2.0
    let heartbeatCadence = TimeSpan.FromSeconds 10.0
    let leaseCadence = TimeSpan.FromSeconds 10.0
    let stablePeriod = TimeSpan.FromSeconds 300.0
    let processGrace = TimeSpan.FromSeconds 10.0

    let captureStageUrl (stageUrl: string) =
        try
            let uri = Uri(stageUrl, UriKind.Absolute)
            let builder = UriBuilder(uri)
            let query =
                if String.IsNullOrEmpty uri.Query then "capture=1"
                else
                    let existing = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries) |> Array.filter (fun pair -> not (pair.StartsWith("capture=", StringComparison.OrdinalIgnoreCase)))
                    String.concat "&" (Array.append existing [| "capture=1" |])
            builder.Query <- query
            builder.Uri.AbsoluteUri
        with _ -> stageUrl

    let environment config =
        let values = Dictionary<string, string>(StringComparer.Ordinal)
        for item in Environment.GetEnvironmentVariables() do
            let pair = item :?> System.Collections.DictionaryEntry
            values[string pair.Key] <- string pair.Value
        values["WEB10_API__BASE_URL"] <- config.ApiBaseUrl
        values["WEB10_STREAM__CALLBACK_TOKEN"] <- config.CallbackToken
        values["WEB10_STREAM__STAGE_URL"] <- config.StageUrl
        values["WEB10_STREAM__RTMP_URL"] <- config.RtmpUrl
        values["WEB10_STREAM__RTMP_KEY"] <- config.RtmpKey
        values["WEB10_STREAM__DISPLAY"] <- config.Display
        values["WEB10_STREAM__GRAPHICS_BACKEND"] <- GraphicsBackend.value config.GraphicsBackend
        values["WEB10_STREAM__WIDTH"] <- string config.Width
        values["WEB10_STREAM__HEIGHT"] <- string config.Height
        values["WEB10_STREAM__FRAMERATE"] <- string config.Framerate
        values["WEB10_STREAM__BITRATE_KBPS"] <- string config.BitrateKbps
        values["WEB10_STREAM__VIDEO_BITRATE_KBPS"] <- string config.VideoBitrateKbps
        values["WEB10_STREAM__VIDEO_PRESET"] <- VideoPreset.value config.VideoPreset
        values["WEB10_STREAM__CALLBACK_PORT"] <- string config.CallbackPort
        values["WEB10_STORAGE__ROOT"] <- config.StorageRoot
        values["WEB10_STORAGE__CACHE_ROOT"] <- config.CacheRoot
        values :> IReadOnlyDictionary<string, string>
[<Sealed>]
type RuntimeSupervisor(
    config: RuntimeConfig,
    ?backendClient: IBackendClient,
    ?processFactory: IProcessFactory,
    ?timeProvider: TimeProvider,
    ?liquidsoapClient: LiquidsoapClient) as this =
    let timeProvider = defaultArg timeProvider TimeProvider.System
    let backend = defaultArg backendClient (BackendClient(config) :> IBackendClient)
    let processFactory = defaultArg processFactory (SystemProcessFactory() :> IProcessFactory)
    let liquidsoap = defaultArg liquidsoapClient (LiquidsoapClient(config, timeProvider = timeProvider))
    let cancellation = new CancellationTokenSource()
    let callbackEvents = ConcurrentQueue<CallbackName * Assignment>()
    let callbackServer =
        CallbackServer(
            config.CallbackPort,
            { new ICallbackSink with
                member _.IsAlive = not cancellation.IsCancellationRequested
                member _.Accept payload = this.AcceptCallback payload })
    let restartBudget = RestartBudget()
    let syncRoot = obj ()
    let mutable xvfb: IManagedProcess option = None
    let mutable chromium: IManagedProcess option = None
    let mutable unclutter: IManagedProcess option = None
    let mutable media: IManagedProcess option = None
    let mutable desiredState = "running"
    let mutable restartGeneration = -1L
    let mutable playbackGeneration = 0L
    let mutable assignment: Assignment option = None
    let mutable assignmentPath: string option = None
    let mutable callbackIdentity: (Guid * Guid * int) option = None
    let mutable callbackStarted = false
    let mutable callbackEnded = false
    let mutable outputFailed = false
    let mutable pushedAt: int64 option = None
    let mutable onDeck: Assignment option = None
    let mutable onDeckPath: string option = None
    let mutable onDeckPushed = false
    let mutable onDeckStarted = false
    let mutable nextOnDeckLeaseAt = 0L
    let mutable completionPending: (Assignment * PlaybackCompletion) option = None
    let mutable nextCompletionAt = 0L
    let mutable restartAt: (int64 * bool) option = None
    let mutable restartAttempt: int option = None
    let mutable terminalFailure = false
    let mutable failureReason: string option = None
    let mutable degradedUntil = 0L
    let mutable stableSince: int64 option = None
    let mutable nextControlAt = 0L
    let mutable nextAssignmentAt = 0L
    let mutable nextHeartbeatAt = 0L
    let mutable nextLeaseAt = 0L
    let mutable controlAlive = false
    let mutable lastHeartbeatUtc = DateTimeOffset.MinValue

    member private _.NowTimestamp = timeProvider.GetTimestamp()

    member private _.ElapsedSince(timestamp) = timeProvider.GetElapsedTime(timestamp)

    member private _.SetDegraded(reason: string) =
        let now = timeProvider.GetTimestamp()
        failureReason <- Some(SafeText.boundedReason reason)
        let until = now + int64 (float Runtime.stablePeriod.TotalSeconds * float timeProvider.TimestampFrequency)
        degradedUntil <- max degradedUntil until

    member private _.SetTerminal(reason: string) =
        terminalFailure <- true
        restartAt <- None
        failureReason <- Some(SafeText.boundedReason reason)

    member private _.ActiveQueueItemId = assignment |> Option.map (fun value -> value.QueueItemId)

    member private _.StopMediaAsync(token) =
        task {
            let old = media
            media <- None
            do! ProcessSupervisor.stopAsync timeProvider Runtime.processGrace old token
        }

    member private _.StopVisualAsync(token) =
        task {
            let oldChromium, oldUnclutter, oldXvfb = chromium, unclutter, xvfb
            chromium <- None; unclutter <- None; xvfb <- None
            do! ProcessSupervisor.stopManyAsync timeProvider Runtime.processGrace [ oldChromium; oldUnclutter; oldXvfb ] token
        }

    member private this.StopAllAsync(token) =
        task {
            do! this.StopMediaAsync(token)
            do! this.StopVisualAsync(token)
        }

    member private _.ClearOnDeck() =
        onDeck <- None
        onDeckPath <- None
        onDeckPushed <- false
        onDeckStarted <- false

    member private this.ClearAssignment() =
        assignment <- None
        assignmentPath <- None
        pushedAt <- None
        callbackIdentity <- None
        callbackStarted <- false
        callbackEnded <- false
        outputFailed <- false
        this.ClearOnDeck()

    member private _.PromoteOnDeck() : bool =
        match onDeck, onDeckPath with
        | Some value, Some path ->
            let now = timeProvider.GetTimestamp()
            assignment <- Some value
            assignmentPath <- Some path
            pushedAt <- Some now
            callbackIdentity <- Some(Assignment.identity value)
            callbackStarted <- onDeckStarted
            callbackEnded <- false
            outputFailed <- false
            nextLeaseAt <- now
            onDeck <- None
            onDeckPath <- None
            onDeckPushed <- false
            onDeckStarted <- false
            true
        | _ -> false

    member private this.StartVisualAsync(token) =
        task {
            let env = Runtime.environment config
            if not (ProcessSupervisor.isAlive xvfb) then
                let value = processFactory.Start(ProcessKind.Xvfb, [ "Xvfb"; config.Display; "-screen"; "0"; sprintf "%dx%dx24" config.Width config.Height; "-nolisten"; "tcp" ], env, Directory.GetCurrentDirectory(), false)
                xvfb <- Some value
                let displayNumber = config.Display.TrimStart ':'
                let path = Path.Combine("/tmp/.X11-unix", "X" + displayNumber)
                let started = timeProvider.GetTimestamp()
                while not (File.Exists path) && timeProvider.GetElapsedTime(started) < Runtime.processGrace do
                    if not value.IsAlive then raise (InvalidOperationException("Xvfb exited before readiness"))
                    do! Monotonic.delay timeProvider (TimeSpan.FromMilliseconds 50.0) token
            if not (ProcessSupervisor.isAlive unclutter) then
                let childEnv = Dictionary<string, string>(env)
                childEnv["DISPLAY"] <- config.Display
                unclutter <- Some(processFactory.Start(ProcessKind.Unclutter, [ "unclutter"; "--timeout"; "0"; "--start-hidden" ], childEnv, Directory.GetCurrentDirectory(), false))
            if not (ProcessSupervisor.isAlive chromium) then
                let profile = "/tmp/web10-chromium"
                try Directory.Delete(profile, true) with _ -> ()
                let childEnv = Dictionary<string, string>(env)
                childEnv["DISPLAY"] <- config.Display
                chromium <- Some(processFactory.Start(ProcessKind.Chromium, [ "./scripts/start-chromium.sh"; profile; Runtime.captureStageUrl config.StageUrl ], childEnv, Directory.GetCurrentDirectory(), false))
        }

    member private _.StartMedia() =
        if not (ProcessSupervisor.isAlive media) then
            media <- Some(processFactory.Start(ProcessKind.Liquidsoap, [ "liquidsoap"; "./liquidsoap/web10.liq" ], Runtime.environment config, Directory.GetCurrentDirectory(), true))
            stableSince <- Some(timeProvider.GetTimestamp())

    member private this.ScheduleRestart(reason: string, visual: bool) =
        this.SetDegraded reason
        let decision = restartBudget.Record timeProvider
        restartAttempt <- Some decision.Attempt
        let now = timeProvider.GetTimestamp()
        if not decision.Allowed then
            terminalFailure <- true
            restartAt <- None
            this.SetFailureHeartbeatBestEffort("failed", reason, Some decision.Attempt)
        else
            restartAt <- Some(now + int64 (float decision.Delay.TotalSeconds * float timeProvider.TimestampFrequency), visual)
            this.SetFailureHeartbeatBestEffort("restarting", reason, Some decision.Attempt)

    member private this.SetFailureHeartbeatBestEffort(status, reason, attempt) =
        task {
            try
                let! _ = backend.PostHeartbeatAsync({ Status = status; FailureReason = Some(SafeText.boundedReason reason); Metadata = { BitrateKbps = Some config.BitrateKbps; RestartAttempt = attempt; ActiveQueueItemId = this.ActiveQueueItemId } }, cancellation.Token)
                return ()
            with _ -> ()
        } |> ignore

    member private this.ControlledRestart() =
        task {
            restartBudget.Reset()
            terminalFailure <- false
            restartAt <- None
            restartAttempt <- None
            failureReason <- None
            degradedUntil <- 0L
            completionPending <- None
            do! this.StopMediaAsync(cancellation.Token)
            this.ClearAssignment()
            nextAssignmentAt <- timeProvider.GetTimestamp()
            nextLeaseAt <- timeProvider.GetTimestamp()
        }

    member private this.PollControlAsync() =
        task {
            try
                let! result = backend.PollControlAsync(playbackGeneration, cancellation.Token)
                match result with
                | Error(BackendError.UnauthorizedResponse _) -> this.SetTerminal("Backend authorization failed")
                | Error _ -> this.SetDegraded("Backend control request failed")
                | Ok(state, commands, nextGeneration) ->
                    let previous = restartGeneration
                    desiredState <- state.DesiredState
                    restartGeneration <- state.RestartGeneration
                    if previous >= 0L && restartGeneration > previous then do! this.ControlledRestart()
                    for command in commands |> List.sortBy (fun value -> value.Generation) do
                        if command.Generation > playbackGeneration then
                            match assignment with
                            | Some current when current.QueueItemId = command.QueueItemId && current.ClaimOwner = command.ClaimOwner && current.ClaimAttempt = command.ClaimAttempt ->
                                if command.Action = "skip" then
                                    do! this.StopMediaAsync(cancellation.Token)
                                    this.ClearAssignment()
                                    nextAssignmentAt <- timeProvider.GetTimestamp()
                                elif command.Action = "restart" && completionPending.IsNone then
                                    do! this.StopMediaAsync(cancellation.Token)
                                    this.ClearAssignment()
                                    nextAssignmentAt <- timeProvider.GetTimestamp()
                            | _ -> ()
                    playbackGeneration <- max playbackGeneration nextGeneration
            with
            | :? OperationCanceledException -> ()
            | _ -> this.SetDegraded("Backend control request failed")
        }

    member private this.PollUpcomingAsync() =
        task {
            if desiredState <> "running" || terminalFailure then ()
            else
                try
                    let! result = backend.GetUpcomingAsync cancellation.Token
                    match result with
                    | Error(BackendError.UnauthorizedResponse _) -> this.SetTerminal("Backend authorization failed")
                    | Error _ -> this.SetDegraded("Backend playback request failed")
                    | Ok(currentOpt, nextOpt) ->
                        match currentOpt, assignment with
                        | Some value, None ->
                            assignment <- Some value
                            assignmentPath <- Some(Liquidsoap.mediaProtocolUri value)
                        | _ -> ()
                        match nextOpt with
                        | Some value ->
                            let alreadyCurrent = assignment |> Option.exists (fun c -> Assignment.identity c = Assignment.identity value)
                            if not alreadyCurrent && not onDeckPushed then
                                match onDeck with
                                | Some existing when Assignment.identity existing = Assignment.identity value -> ()
                                | _ ->
                                    onDeck <- Some value
                                    onDeckPath <- Some(Liquidsoap.mediaProtocolUri value)
                                    onDeckStarted <- false
                        | None -> ()
                with
                | :? OperationCanceledException -> ()
                | _ -> this.SetDegraded("Backend playback request failed")
        }

    member private this.PushAssignmentAsync() =
        task {
            match assignment, assignmentPath, pushedAt, completionPending, media with
            | Some value, Some path, None, None, Some process when process.IsAlive ->
                let! ready = liquidsoap.IsReadyAsync(cancellation.Token)
                if ready then
                    try
                        let! response = liquidsoap.PushAsync(value, path, cancellation.Token)
                        if response.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 then raise (InvalidOperationException("Liquidsoap rejected media"))
                        callbackIdentity <- Some(Assignment.identity value)
                        callbackStarted <- false; callbackEnded <- false; outputFailed <- false
                        pushedAt <- Some(timeProvider.GetTimestamp())
                        nextLeaseAt <- timeProvider.GetTimestamp()
                    with _ -> do! this.BeginCompletionAsync(value, PlaybackCompletion.Failed "Media pipeline rejected assignment")
            | _ -> ()
        }

    member private this.PushOnDeckAsync() =
        task {
            match onDeck, onDeckPath, onDeckPushed, assignment, pushedAt, completionPending, media with
            | Some value, Some path, false, Some _, Some _, None, Some process when process.IsAlive && callbackStarted && not callbackEnded ->
                let! ready = liquidsoap.IsReadyAsync(cancellation.Token)
                if ready then
                    try
                        let! response = liquidsoap.PushAsync(value, path, cancellation.Token)
                        if response.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 then raise (InvalidOperationException("Liquidsoap rejected on-deck media"))
                        onDeckPushed <- true
                        nextOnDeckLeaseAt <- timeProvider.GetTimestamp()
                    with _ -> this.ClearOnDeck()
            | _ -> ()
        }

    member private this.BeginCompletionAsync(value: Assignment, completion: PlaybackCompletion) =
        task {
            if completionPending.IsNone then
                completionPending <- Some(value, completion)
                nextCompletionAt <- timeProvider.GetTimestamp()
                match completion with
                | PlaybackCompletion.Failed reason ->
                    this.SetDegraded reason
                    do! this.StopMediaAsync(cancellation.Token)
                    this.ClearOnDeck()
                | PlaybackCompletion.Played -> ()
                do! this.ServiceCompletionAsync()
        }

    member private this.ServiceCompletionAsync() =
        task {
            match completionPending with
            | None -> ()
            | Some(value, completion) ->
                if timeProvider.GetTimestamp() >= nextCompletionAt then
                    let! result = backend.CompleteAsync(value, completion, cancellation.Token)
                    match result with
                    | CallbackResult.Accepted
                    | CallbackResult.Stale ->
                        completionPending <- None
                        match assignment with
                        | Some current when Assignment.identity current = Assignment.identity value ->
                            this.ClearAssignment(); nextAssignmentAt <- timeProvider.GetTimestamp()
                        | _ -> ()
                    | CallbackResult.Unauthorized -> completionPending <- None; this.SetTerminal("Backend authorization failed")
                    | CallbackResult.TransientError -> nextCompletionAt <- timeProvider.GetTimestamp() + int64 (float Runtime.heartbeatCadence.TotalSeconds * float timeProvider.TimestampFrequency)
        }

    member private this.RenewLeaseAsync() =
        task {
            match assignment, pushedAt, completionPending with
            | Some value, Some _, None when desiredState = "running" && not terminalFailure && timeProvider.GetTimestamp() >= nextLeaseAt ->
                nextLeaseAt <- timeProvider.GetTimestamp() + int64 (float Runtime.leaseCadence.TotalSeconds * float timeProvider.TimestampFrequency)
                match! backend.RenewLeaseAsync(value, cancellation.Token) with
                | CallbackResult.Accepted -> ()
                | CallbackResult.Stale ->
                    do! this.StopMediaAsync(cancellation.Token)
                    this.ClearAssignment()
                    nextAssignmentAt <- timeProvider.GetTimestamp()
                | CallbackResult.Unauthorized -> this.SetTerminal("Backend authorization failed")
                | CallbackResult.TransientError -> this.SetDegraded("Playback lease renewal failed")
            | _ -> ()

            match onDeck, onDeckPushed with
            | Some value, true when desiredState = "running" && not terminalFailure && timeProvider.GetTimestamp() >= nextOnDeckLeaseAt ->
                nextOnDeckLeaseAt <- timeProvider.GetTimestamp() + int64 (float Runtime.leaseCadence.TotalSeconds * float timeProvider.TimestampFrequency)
                match! backend.RenewLeaseAsync(value, cancellation.Token) with
                | CallbackResult.Accepted -> ()
                | CallbackResult.Stale -> this.ClearOnDeck()
                | CallbackResult.Unauthorized -> this.SetTerminal("Backend authorization failed")
                | CallbackResult.TransientError -> ()
            | _ -> ()
        }

    member private this.DrainCallbacksAsync() =
        task {
            let mutable item = Unchecked.defaultof<CallbackName * Assignment>
            while callbackEvents.TryDequeue(&item) do
                let callback, value = item
                let fenceId = Assignment.identity value
                let isCurrent = assignment |> Option.exists (fun current -> Assignment.identity current = fenceId)
                let isOnDeck = onDeck |> Option.exists (fun deck -> Assignment.identity deck = fenceId)
                if isCurrent then
                    match callback with
                    | CallbackName.Started -> callbackStarted <- true
                    | CallbackName.Ended ->
                        this.PromoteOnDeck() |> ignore
                        do! this.BeginCompletionAsync(value, PlaybackCompletion.Played)
                    | CallbackName.OutputFailed ->
                        do! this.BeginCompletionAsync(value, PlaybackCompletion.Failed "RTMP output failed")
                        this.SetTerminal("RTMP output failed")
                    | CallbackName.StartTimeout
                    | CallbackName.ProtocolError -> do! this.BeginCompletionAsync(value, PlaybackCompletion.Failed "Playback callback protocol error")
                elif isOnDeck then
                    match callback with
                    | CallbackName.Started -> onDeckStarted <- true
                    | CallbackName.Ended ->
                        do! this.BeginCompletionAsync(value, PlaybackCompletion.Played)
                        this.ClearOnDeck()
                    | _ -> ()
        }

    member private this.CheckStartDeadlineAsync() =
        task {
            match assignment, pushedAt with
            | Some value, Some started when not callbackStarted && timeProvider.GetElapsedTime(started) >= Runtime.callbackStartDeadline -> do! this.BeginCompletionAsync(value, PlaybackCompletion.Failed "Playback start callback timed out")
            | _ -> ()
        }

    member private this.ObserveChildrenAsync() =
        task {
            if desiredState = "running" && not terminalFailure && restartAt.IsNone then
                if xvfb |> Option.exists (fun p -> not p.IsAlive) || chromium |> Option.exists (fun p -> not p.IsAlive) || unclutter |> Option.exists (fun p -> not p.IsAlive) then
                    do! this.StopMediaAsync(cancellation.Token)
                    do! this.StopVisualAsync(cancellation.Token)
                    this.ScheduleRestart("Visual pipeline exited", true)
                elif media |> Option.exists (fun p -> not p.IsAlive) then
                    media <- None
                    let reason = if assignment.IsSome then "RTMP output failed" else "Media pipeline exited"
                    if reason = "RTMP output failed" then
                        match assignment with
                        | Some value -> do! this.BeginCompletionAsync(value, PlaybackCompletion.Failed reason)
                        | None -> ()
                        this.SetTerminal reason
                    else this.ScheduleRestart(reason, false)
        }

    member private this.ApplyRestartAsync() =
        task {
            match restartAt with
            | None -> ()
            | Some(deadline, visual) when timeProvider.GetTimestamp() >= deadline ->
                restartAt <- None
                try
                    if visual then do! this.StartVisualAsync(cancellation.Token)
                    this.StartMedia()
                with _ -> this.ScheduleRestart("Pipeline restart failed", visual)
            | _ -> ()
        }

    member private this.HeartbeatAsync() =
        task {
            let status, reason =
                if desiredState = "stopped" then "offline", None
                elif terminalFailure then "failed", failureReason
                elif restartAt.IsSome then "restarting", failureReason
                elif timeProvider.GetTimestamp() < degradedUntil then "degraded", failureReason
                elif assignment.IsSome && callbackStarted && ProcessSupervisor.isAlive media && ProcessSupervisor.isAlive xvfb && ProcessSupervisor.isAlive chromium then "live", None
                else "starting", None
            if timeProvider.GetTimestamp() >= nextHeartbeatAt then
                nextHeartbeatAt <- timeProvider.GetTimestamp() + int64 (float Runtime.heartbeatCadence.TotalSeconds * float timeProvider.TimestampFrequency)
                lastHeartbeatUtc <- timeProvider.GetUtcNow()
                try
                    let metadata: HeartbeatMetadata =
                        { BitrateKbps = if status = "offline" then None else Some config.BitrateKbps
                          RestartAttempt = restartAttempt
                          ActiveQueueItemId = this.ActiveQueueItemId }
                    let heartbeat: Heartbeat = { Status = status; FailureReason = reason; Metadata = metadata }
                    let! result = backend.PostHeartbeatAsync(heartbeat, cancellation.Token)
                    match result with
                    | Ok () -> ()
                    | Error(BackendError.UnauthorizedResponse _) -> this.SetTerminal("Backend authorization failed")
                    | Error _ -> if status <> "offline" then this.SetDegraded("Backend heartbeat failed")
                with _ -> if status <> "offline" then this.SetDegraded("Backend heartbeat failed")
        }

    member private this.ResetStableBudget() =
        if restartAt.IsNone && not terminalFailure && ProcessSupervisor.isAlive media && ProcessSupervisor.isAlive xvfb && ProcessSupervisor.isAlive chromium then
            match stableSince with
            | Some value when timeProvider.GetElapsedTime(value) >= Runtime.stablePeriod -> restartBudget.Reset(); restartAttempt <- None; stableSince <- Some(timeProvider.GetTimestamp())
            | None -> stableSince <- Some(timeProvider.GetTimestamp())
            | _ -> ()
        else stableSince <- None

    member private this.LoopAsync(token: CancellationToken) =
        task {
            nextControlAt <- 0L; nextAssignmentAt <- 0L; nextHeartbeatAt <- 0L; nextLeaseAt <- 0L
            while not token.IsCancellationRequested do
                let now = timeProvider.GetTimestamp()
                if now >= nextControlAt then
                    nextControlAt <- now + int64 (float Runtime.pollCadence.TotalSeconds * float timeProvider.TimestampFrequency)
                    do! this.PollControlAsync()
                do! this.ApplyRestartAsync()
                if desiredState = "stopped" then
                    do! this.StopMediaAsync(token)
                    this.ClearAssignment()
                elif desiredState = "paused" then
                    do! this.StopMediaAsync(token)
                elif not terminalFailure then
                    if not (ProcessSupervisor.isAlive xvfb && ProcessSupervisor.isAlive chromium && ProcessSupervisor.isAlive unclutter) then
                        try do! this.StartVisualAsync(token) with _ -> this.ScheduleRestart("Pipeline startup failed", true)
                    if restartAt.IsNone && not (ProcessSupervisor.isAlive media) then this.StartMedia()
                    do! this.ObserveChildrenAsync()
                    if now >= nextAssignmentAt then
                        nextAssignmentAt <- now + int64 (float Runtime.pollCadence.TotalSeconds * float timeProvider.TimestampFrequency)
                        do! this.PollUpcomingAsync()
                    do! this.PushAssignmentAsync()
                    do! this.PushOnDeckAsync()
                do! this.DrainCallbacksAsync()
                do! this.ServiceCompletionAsync()
                do! this.RenewLeaseAsync()
                do! this.CheckStartDeadlineAsync()
                this.ResetStableBudget()
                do! this.HeartbeatAsync()
                try do! Task.Delay(TimeSpan.FromMilliseconds 100.0, timeProvider, token) with :? OperationCanceledException -> ()
        }

    member private this.AcceptCallback(payload: CallbackPayload) =
        lock syncRoot (fun () ->
            match payload with
            | CallbackPayload.OutputFailed ->
                match assignment, callbackIdentity with
                | Some value, Some identity when Assignment.identity value = identity && not outputFailed -> outputFailed <- true; callbackEvents.Enqueue(CallbackName.OutputFailed, value); true
                | _ -> false
            | CallbackPayload.Started fence
            | CallbackPayload.Ended fence ->
                let fenceId = (fence.QueueItemId, fence.ClaimOwner, fence.ClaimAttempt)
                let callback = match payload with | CallbackPayload.Started _ -> CallbackName.Started | _ -> CallbackName.Ended
                let matchesCurrent =
                    match callbackIdentity, assignment with
                    | Some identity, Some value -> identity = fenceId && Assignment.identity value = fenceId
                    | _ -> false
                if matchesCurrent then
                    match assignment with
                    | Some value ->
                        if callback = CallbackName.Started && callbackStarted then false
                        elif callback = CallbackName.Ended && callbackEnded then false
                        else
                            callbackEvents.Enqueue(callback, value)
                            (if callback = CallbackName.Started then callbackStarted <- true else callbackEnded <- true)
                            true
                    | None -> false
                else
                    match onDeck with
                    | Some value when Assignment.identity value = fenceId ->
                        if callback = CallbackName.Started && onDeckStarted then false
                        else
                            callbackEvents.Enqueue(callback, value)
                            (if callback = CallbackName.Started then onDeckStarted <- true)
                            true
                    | _ -> false)

    member this.Snapshot() =
        let status, reason =
            if desiredState = "stopped" then "offline", None
            elif desiredState = "paused" then "offline", None
            elif terminalFailure then "failed", failureReason
            elif restartAt.IsSome then "restarting", failureReason
            elif timeProvider.GetTimestamp() < degradedUntil then "degraded", failureReason
            elif assignment.IsSome && callbackStarted then "live", None
            else "starting", None
        { DesiredState = desiredState; Status = status; RestartGeneration = max 0L restartGeneration; PlaybackGeneration = playbackGeneration; TerminalFailure = terminalFailure; ActiveQueueItemId = this.ActiveQueueItemId; RestartAttempt = restartAttempt; FailureReason = reason; LastHeartbeatUtc = lastHeartbeatUtc }

    member _.RequestShutdown() = cancellation.Cancel()

    member this.RunAsync(token: CancellationToken) =
        task {
            use linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token)
            callbackServer.Start()
            controlAlive <- true
            try
                try do! this.StartVisualAsync(linked.Token) with _ -> this.ScheduleRestart("Visual pipeline failed", true)
                try this.StartMedia() with _ -> this.ScheduleRestart("Media pipeline failed", false)
                try do! this.LoopAsync(linked.Token) with :? OperationCanceledException -> ()
            with _ -> ()
            controlAlive <- false
            try
                let! _ = backend.PostHeartbeatAsync({ Status = "offline"; FailureReason = None; Metadata = { BitrateKbps = None; RestartAttempt = None; ActiveQueueItemId = None } }, CancellationToken.None)
                ()
            with _ -> ()
            do! this.StopAllAsync(CancellationToken.None)
            do! callbackServer.StopAsync()
        }

    member _.CallbackServer = callbackServer
    member _.Backend = backend
    member _.TimeProvider = timeProvider

type Supervisor = RuntimeSupervisor
