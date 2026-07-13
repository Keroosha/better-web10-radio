namespace Web10.Radio.StreamNode.Tools

open System
open System.Globalization
open System.Linq
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq

[<CLIMutable>]
type SmokeOptions =
    { Mode: string
      BaseUrl: string
      RtmpStatUrl: string
      Username: string
      Password: string
      TimeoutSeconds: float }

module SmokeBackend =
    let private supportedModes =
        Set.ofList [ "restart-live"; "flac-cue"; "reorder"; "skip"; "restart-current"; "play-now"; "expect-output-failure"; "recover" ]

    let options (arguments: Map<string, string>) =
        let mode = Parsing.option "mode" "" arguments
        if not (Set.contains mode supportedModes) then
            raise (ToolError("mode", "invalid"))
        let baseUrl = Parsing.required "base-url" arguments
        let rtmpStatUrl = Parsing.required "rtmp-stat-url" arguments
        let username = Parsing.required "username" arguments
        let password = Parsing.required "password" arguments
        let timeoutSeconds = Parsing.option "timeout-seconds" "60" arguments |> Parsing.positiveFloat "timeout-seconds"
        { Mode = mode
          BaseUrl = baseUrl.TrimEnd('/')
          RtmpStatUrl = rtmpStatUrl
          Username = username
          Password = password
          TimeoutSeconds = timeoutSeconds }

    let private property (name: string) (document: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if document.TryGetProperty(name, &value) then Some value else None

    let private stringValue (name: string) (document: JsonElement) = Document.string name document

    let private int64Value (name: string) (document: JsonElement) =
        match property name document with
        | Some value ->
            let mutable number = 0L
            if value.TryGetInt64(&number) then Some number else None
        | None -> None

    let private statusSnapshot (admin: AdminConnection) (deadline: Time.Deadline) cancellationToken =
        task {
            let! response = admin.Get("stream-status", "/api/v0/admin/stream-node/status", deadline, cancellationToken)
            if response.Status <> 200 then return None
            else
                match response.Body |> Option.bind Json.tryDocument with
                | Some document -> return Some document
                | None -> return None
        }

    let private restart (admin: AdminConnection) (deadline: Time.Deadline) cancellationToken =
        task {
            let! response = admin.PostWithCsrf("stream-restart", "/api/v0/admin/stream-node/restart", Map.empty<string, obj>, 202, deadline, cancellationToken)
            match response.Body |> Option.bind Json.tryDocument with
            | Some document ->
                match int64Value "restartGeneration" document with
                | Some generation when generation >= 0L -> return generation
                | _ -> return raise (ToolError("stream-restart", "invalid-response"))
            | None -> return raise (ToolError("stream-restart", "invalid-response"))
        }

    let private stop (admin: AdminConnection) (deadline: Time.Deadline) cancellationToken =
        task {
            let! response = admin.PostWithCsrf("stream-stop", "/api/v0/admin/stream-node/stop", Map.empty<string, obj>, 202, deadline, cancellationToken)
            match response.Body |> Option.bind Json.tryDocument with
            | Some document ->
                match int64Value "restartGeneration" document with
                | Some generation when generation >= 0L -> return generation
                | _ -> return raise (ToolError("stream-stop", "invalid-response"))
            | None -> return raise (ToolError("stream-stop", "invalid-response"))
        }

    let private findTrack (expectedTitle: string) (admin: AdminConnection) (deadline: Time.Deadline) cancellationToken =
        task {
            let query = Uri.EscapeDataString expectedTitle
            let! response = admin.Get("track-lookup", "/api/v0/admin/tracks?query=" + query + "&limit=100", deadline, cancellationToken)
            if response.Status <> 200 then
                return raise (ToolError("track-lookup", string response.Status))
            else
                let document = response.Body |> Option.bind Json.tryDocument |> Option.defaultWith (fun () -> raise (ToolError("track-lookup", "invalid-response")))
                let candidates = Document.trackCandidates document |> Option.defaultValue [||]
                match candidates |> Array.filter (fun track -> track.Title = expectedTitle && track.HasCachedFile) with
                | [| candidate |] -> return candidate.Id
                | [||] -> return raise (ToolError("track-lookup", "not-found"))
                | _ -> return raise (ToolError("track-lookup", "ambiguous"))
        }

    let private enqueue (admin: AdminConnection) trackId (deadline: Time.Deadline) cancellationToken =
        task {
            let! response = admin.PostWithCsrf("playback-queue", "/api/v0/admin/playback/queue", {| TrackId = trackId |}, 202, deadline, cancellationToken)
            let document = response.Body |> Option.bind Json.tryDocument |> Option.defaultWith (fun () -> raise (ToolError("playback-queue", "invalid-response")))
            let _ = Document.stringField "queueItemId" document
            return ()
        }

    let private getStringField name operation document =
        match stringValue name document with
        | Some value -> value
        | None -> raise (ToolError(operation, "invalid-response"))

    let private waitFor operation (deadline: Time.Deadline) cancellationToken (probe: unit -> Task<bool * string option>) =
        task {
            let mutable lastStatus: string option = None
            let mutable complete = false
            while not complete && Time.remaining deadline > TimeSpan.Zero do
                let! success, observed = probe ()
                if success then complete <- true
                else
                    match observed with
                    | Some value -> lastStatus <- Some value
                    | None -> ()
                    do! Time.delay deadline.Provider (TimeSpan.FromSeconds 1.0) cancellationToken
            if not complete then raise (ToolError(operation, lastStatus |> Option.defaultValue "timeout"))
        }

    let private waitForLive admin rtmpStatUrl (deadline: Time.Deadline) cancellationToken =
        waitFor "live" deadline cancellationToken (fun () ->
            task {
                let! status = statusSnapshot admin deadline cancellationToken
                match status with
                | None -> return false, Some "unavailable"
                | Some document ->
                    let isLive = stringValue "status" document = Some "live" && (stringValue "failureReason" document).IsNone
                    if not isLive then
                        return false, None
                    else
                        let! response = admin.GetRtmpStat(rtmpStatUrl, deadline, cancellationToken)
                        if response.Status <> 200 then
                            return false, Some(string response.Status)
                        else
                            let active =
                                match response.Body with
                                | None -> false
                                | Some body ->
                                    try
                                        let xml = XDocument.Parse body
                                        let values =
                                            xml.Descendants()
                                            |> Seq.filter (fun node -> node.Name.LocalName = "naccepted" || node.Name.LocalName = "bytes_in")
                                            |> Seq.map (fun node -> node.Name.LocalName, node.Value.Trim())
                                            |> Map.ofSeq
                                        match Map.tryFind "naccepted" values, Map.tryFind "bytes_in" values with
                                        | Some accepted, Some bytesIn -> Int64.Parse(accepted, CultureInfo.InvariantCulture) > 0L && Int64.Parse(bytesIn, CultureInfo.InvariantCulture) > 0L
                                        | _ -> false
                                    with _ -> false
                            return active, None
            })

    let private waitForNowPlaying (admin: AdminConnection) (expectedTrackId: string) (deadline: Time.Deadline) cancellationToken =
        waitFor "cue-now-playing" deadline cancellationToken (fun () ->
            task {
                let! state = admin.GetJson("player-state", "/api/v0/player/state", deadline, cancellationToken)
                match property "nowPlaying" state with
                | Some nowPlaying when nowPlaying.ValueKind = JsonValueKind.Object ->
                    return stringValue "trackId" nowPlaying = Some expectedTrackId, stringValue "trackId" nowPlaying
                | _ -> return false, Some "missing-now-playing"
            })

    let private waitForOffline admin requiredGeneration requireStopped (deadline: Time.Deadline) cancellationToken =
        waitFor "offline" deadline cancellationToken (fun () ->
            task {
                let! status = statusSnapshot admin deadline cancellationToken
                match status with
                | None -> return false, Some "unavailable"
                | Some document ->
                    let generationMatches = requiredGeneration |> Option.forall (fun expected -> int64Value "restartGeneration" document = Some expected)
                    let stopped = not requireStopped || stringValue "desiredState" document = Some "stopped"
                    return generationMatches && stopped && stringValue "status" document = Some "offline", None
            })

    let private waitForResumed admin requiredGeneration (deadline: Time.Deadline) cancellationToken =
        waitFor "stream-restart" deadline cancellationToken (fun () ->
            task {
                let! status = statusSnapshot admin deadline cancellationToken
                match status with
                | None -> return false, Some "unavailable"
                | Some document ->
                    let generation = int64Value "restartGeneration" document |> Option.defaultValue -1L
                    return generation >= requiredGeneration && stringValue "desiredState" document = Some "running" && (stringValue "status" document = Some "starting" || stringValue "status" document = Some "live"), None
            })

    let private queueIds (document: JsonElement) =
        match property "queue" document with
        | Some queue ->
            match property "items" queue with
            | Some items when items.ValueKind = JsonValueKind.Array ->
                items.EnumerateArray()
                |> Seq.choose (fun item ->
                    match stringValue "status" item with
                    | Some "queued" -> stringValue "id" item |> Option.orElse (stringValue "queueItemId" item)
                    | _ -> None)
                |> Seq.toArray
            | _ -> [||]
        | _ -> [||]

    let private reorder (admin: AdminConnection) (deadline: Time.Deadline) cancellationToken =
        task {
            let! state = admin.GetJson("queue-state", "/api/v0/player/state", deadline, cancellationToken)
            let ids = queueIds state
            if ids.Length > 0 then
                let reversed = ids |> Array.rev
                let! _ = admin.PutWithCsrf("playback-reorder", "/api/v0/admin/playback/queue/order", {| QueueItemIds = reversed |}, 200, deadline, cancellationToken)
                return ()
            else
                return ()
        }

    let private control (admin: AdminConnection) (route: string) (operation: string) (deadline: Time.Deadline) cancellationToken =
        task {
            let! _ = admin.PostWithCsrf(operation, route, Map.empty<string, obj>, 202, deadline, cancellationToken)
            return ()
        }

    let run (provider: TimeProvider) (settings: SmokeOptions) cancellationToken =
        task {
            let deadline = Time.create provider (TimeSpan.FromSeconds settings.TimeoutSeconds)
            let admin = new AdminConnection(settings.BaseUrl, settings.Username, settings.Password)
            do! admin.Login(deadline, cancellationToken)
            match settings.Mode with
            | "restart-live" ->
                let! _ = restart admin deadline cancellationToken
                let! track = findTrack "web10-compose-smoke" admin deadline cancellationToken
                do! enqueue admin track deadline cancellationToken
                do! waitForLive admin settings.RtmpStatUrl deadline cancellationToken
            | "flac-cue" ->
                let! _ = restart admin deadline cancellationToken
                let! track = findTrack "web10-compose-cue-track-02" admin deadline cancellationToken
                let! _ = admin.PostWithCsrf("cue-play-now", "/api/v0/admin/playback/play-now", {| TrackId = track |}, 202, deadline, cancellationToken)
                do! waitForNowPlaying admin track deadline cancellationToken
                do! waitForLive admin settings.RtmpStatUrl deadline cancellationToken
            | "reorder" -> do! reorder admin deadline cancellationToken
            | "skip" -> do! control admin "/api/v0/admin/playback/skip" "playback-skip" deadline cancellationToken
            | "restart-current" -> do! control admin "/api/v0/admin/playback/restart-current" "playback-restart-current" deadline cancellationToken
            | "play-now" ->
                let! track = findTrack "web10-compose-smoke" admin deadline cancellationToken
                let! _ = admin.PostWithCsrf("play-now", "/api/v0/admin/playback/play-now", {| TrackId = track |}, 202, deadline, cancellationToken)
                return ()
            | "expect-output-failure" ->
                do!
                    waitFor "output-failure" deadline cancellationToken (fun () ->
                        task {
                            let! status = statusSnapshot admin deadline cancellationToken
                            match status with
                            | Some document ->
                                let state = stringValue "status" document
                                let reason = stringValue "failureReason" document
                                return (state = Some "degraded" || state = Some "restarting" || state = Some "failed") && reason = Some "RTMP output failed", None
                            | None -> return false, Some "unavailable"
                        })
            | "recover" ->
                let! firstGeneration = restart admin deadline cancellationToken
                let! track = findTrack "web10-compose-smoke" admin deadline cancellationToken
                do! enqueue admin track deadline cancellationToken
                do! waitForLive admin settings.RtmpStatUrl deadline cancellationToken
                let! stoppedGeneration = stop admin deadline cancellationToken
                do! waitForOffline admin (Some stoppedGeneration) true deadline cancellationToken
                let! secondGeneration = restart admin deadline cancellationToken
                if secondGeneration <= firstGeneration then raise (ToolError("stream-restart", "invalid-response"))
                do! waitForResumed admin secondGeneration deadline cancellationToken
            | _ -> raise (ToolError("mode", "invalid"))
        }
