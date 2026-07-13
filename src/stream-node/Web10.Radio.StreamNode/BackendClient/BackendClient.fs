namespace Web10.Radio.StreamNode

open Web10.Radio.StreamNode.ResultWorkflow
open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

module private Json =
    let options =
        let value = JsonSerializerOptions(JsonSerializerDefaults.Web)
        value.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        value.DictionaryKeyPolicy <- JsonNamingPolicy.CamelCase
        value

    let objectRoot (bytes: byte array) operation =
        try
            use document = JsonDocument.Parse(bytes)
            if document.RootElement.ValueKind = JsonValueKind.Object then Ok(document.RootElement.Clone())
            else Error(BackendError.InvalidResponse operation)
        with :? JsonException -> Error(BackendError.InvalidResponse operation)

    let tryProperty (name: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty(name, &value) then Some value else None

    let requiredString name operation root =
        match tryProperty name root with
        | Some value when value.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(value.GetString())) -> Ok(value.GetString())
        | _ -> Error(BackendError.InvalidResponse operation)

    let optionalString name operation root =
        match tryProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Null -> Ok None
        | Some value when value.ValueKind = JsonValueKind.String -> Ok(Some(value.GetString()))
        | None -> Ok None
        | _ -> Error(BackendError.InvalidResponse operation)

    let int64 name operation root =
        match tryProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Number ->
            let mutable parsed = 0L
            if value.TryGetInt64(&parsed) && parsed >= 0L then Ok parsed else Error(BackendError.InvalidResponse operation)
        | _ -> Error(BackendError.InvalidResponse operation)

    let int32Positive name operation root =
        match tryProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Number ->
            let mutable parsed = 0
            if value.TryGetInt32(&parsed) && parsed > 0 then Ok parsed else Error(BackendError.InvalidResponse operation)
        | _ -> Error(BackendError.InvalidResponse operation)

    let cueTiming operation root =
        match tryProperty "cueStartMs" root, tryProperty "cueDurationMs" root with
        | Option.None, Option.None -> Ok(None, None)
        | Some start, Some duration when start.ValueKind = JsonValueKind.Null && duration.ValueKind = JsonValueKind.Null ->
            Ok(None, None)
        | Some start, Some duration when start.ValueKind = JsonValueKind.Number && duration.ValueKind = JsonValueKind.Number ->
            let mutable parsedStart = 0
            let mutable parsedDuration = 0
            if start.TryGetInt32(&parsedStart) && parsedStart >= 0
               && duration.TryGetInt32(&parsedDuration) && parsedDuration > 0 then
                Ok(Some parsedStart, Some parsedDuration)
            else
                Error(BackendError.InvalidResponse operation)
        | _ -> Error(BackendError.InvalidResponse operation)

    let guid name operation root =
        match requiredString name operation root with
        | Error error -> Error error
        | Ok value ->
            let mutable parsed = Guid.Empty
            if Guid.TryParse(value, &parsed) && parsed <> Guid.Empty then Ok parsed
            else Error(BackendError.InvalidResponse operation)

    let optionalGuid name operation root =
        match tryProperty name root with
        | None -> Ok None
        | Some value when value.ValueKind = JsonValueKind.Null -> Ok None
        | Some value when value.ValueKind = JsonValueKind.String ->
            let mutable parsed = Guid.Empty
            if Guid.TryParse(value.GetString(), &parsed) && parsed <> Guid.Empty then Ok(Some parsed)
            else Error(BackendError.InvalidResponse operation)
        | _ -> Error(BackendError.InvalidResponse operation)

    let serialize value = JsonSerializer.Serialize(value, options) |> Encoding.UTF8.GetBytes

[<RequireQualifiedAccess>]
type BackendClientResult =
    | Control of ControlState * PlaybackCommand list * int64
    | Assignment of Assignment option

type IBackendClient =
    abstract GetControlPageAsync: afterGeneration: int64 * limit: int * cancellationToken: CancellationToken -> Task<Result<ControlPage, BackendError>>
    abstract PollControlAsync: afterGeneration: int64 * cancellationToken: CancellationToken -> Task<Result<ControlState * PlaybackCommand list * int64, BackendError>>
    abstract GetAssignmentAsync: cancellationToken: CancellationToken -> Task<Result<Assignment option, BackendError>>
    abstract GetUpcomingAsync: cancellationToken: CancellationToken -> Task<Result<Assignment option * Assignment option, BackendError>>
    abstract PostHeartbeatAsync: Heartbeat * cancellationToken: CancellationToken -> Task<Result<unit, BackendError>>
    abstract RenewLeaseAsync: Assignment * cancellationToken: CancellationToken -> Task<CallbackResult>
    abstract CompleteAsync: Assignment * PlaybackCompletion * cancellationToken: CancellationToken -> Task<CallbackResult>

type BackendClient(config: RuntimeConfig, ?httpClient: HttpClient) =
    let ownsClient = httpClient.IsNone
    let client = defaultArg httpClient (new HttpClient())
    let baseUrl = config.ApiBaseUrl.TrimEnd('/')
    let timeout = TimeSpan.FromSeconds 5.0

    do
        if httpClient.IsNone then client.Timeout <- timeout

    member private _.RequestAsync(method: HttpMethod, path: string, payload: byte array option, token: CancellationToken) =
        task {
            use request = new HttpRequestMessage(method, baseUrl + path)
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", config.CallbackToken)
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
            match payload with
            | Some bytes ->
                request.Content <- new ByteArrayContent(bytes)
                request.Content.Headers.ContentType <- MediaTypeHeaderValue("application/json")
                request.Content.Headers.ContentType.CharSet <- "utf-8"
            | None -> ()
            try
                use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                let! bytes = response.Content.ReadAsByteArrayAsync(token)
                return Ok(response.StatusCode, bytes)
            with
            | :? OperationCanceledException when token.IsCancellationRequested -> return raise (OperationCanceledException(token))
            | :? HttpRequestException -> return Error(BackendError.Transport path)
            | :? TaskCanceledException -> return Error(BackendError.Transport path)
            | :? InvalidOperationException -> return Error(BackendError.Transport path)
        }

    member private this.GetAsync path token = this.RequestAsync(HttpMethod.Get, path, None, token)

    member private this.PostAsync path payload token = this.RequestAsync(HttpMethod.Post, path, Some(Json.serialize payload), token)

    member private this.ParseControlPage(bytes: byte array) =
        let operation = "stream-node.control"
        result {
            let! root = Json.objectRoot bytes operation
            let! desired = Json.requiredString "desiredState" operation root
            do! if desired = "running" || desired = "paused" || desired = "stopped" then Ok() else Error(BackendError.InvalidResponse operation)
            let! restartGeneration = Json.int64 "restartGeneration" operation root
            let! commands =
                match Json.tryProperty "playbackCommands" root with
                | Some value when value.ValueKind = JsonValueKind.Array ->
                    value.EnumerateArray()
                    |> Seq.toList
                    |> List.map (fun commandRoot ->
                        result {
                            let! generation = Json.int64 "generation" operation commandRoot
                            do! if generation > 0L then Ok() else Error(BackendError.InvalidResponse operation)
                            let! action = Json.requiredString "action" operation commandRoot
                            do! if action = "skip" || action = "restart" then Ok() else Error(BackendError.InvalidResponse operation)
                            let! queueItemId = Json.guid "queueItemId" operation commandRoot
                            let! claimOwner = Json.guid "claimOwner" operation commandRoot
                            let! claimAttempt = Json.int32Positive "claimAttempt" operation commandRoot
                            return { Generation = generation; Action = action; QueueItemId = queueItemId; ClaimOwner = claimOwner; ClaimAttempt = claimAttempt }
                        })
                    |> List.fold (fun state item ->
                        match state, item with
                        | Ok values, Ok value -> Ok(values @ [ value ])
                        | Error error, _ -> Error error
                        | _, Error error -> Error error) (Ok [])
                | None -> Ok []
                | _ -> Error(BackendError.InvalidResponse operation)
            let! next =
                match Json.tryProperty "nextPlaybackGeneration" root with
                | Some _ -> Json.int64 "nextPlaybackGeneration" operation root
                | None -> Ok 0L
            do! if next >= 0L then Ok() else Error(BackendError.InvalidResponse operation)
            return { DesiredState = desired; RestartGeneration = restartGeneration; PlaybackCommands = commands |> List.toArray; NextPlaybackGeneration = next }
        }

    member private _.ParseAssignmentElement(operation: string, root: JsonElement) =
        result {
            let! queueItemId = Json.guid "queueItemId" operation root
            let! claimOwner = Json.guid "claimOwner" operation root
            let! claimAttempt = Json.int32Positive "claimAttempt" operation root
            let! trackId = Json.guid "trackId" operation root
            let! contentType = Json.requiredString "contentType" operation root
            let! title =
                match Json.tryProperty "title" root with
                | Some value when value.ValueKind = JsonValueKind.String -> Ok(value.GetString())
                | _ -> Ok ""
            let! artist =
                match Json.tryProperty "artist" root with
                | Some value when value.ValueKind = JsonValueKind.String -> Ok(value.GetString())
                | _ -> Ok ""
            let! duration =
                match Json.tryProperty "durationMs" root with
                | Some value when value.ValueKind = JsonValueKind.Number ->
                    let mutable parsed = 0
                    if value.TryGetInt32(&parsed) && parsed >= 0 then Ok parsed else Error(BackendError.InvalidResponse operation)
                | _ -> Ok 0
            let! cueStartMs, cueDurationMs = Json.cueTiming operation root
            return
                { QueueItemId = queueItemId
                  ClaimOwner = claimOwner
                  ClaimAttempt = claimAttempt
                  TrackId = trackId
                  ContentType = contentType
                  Title = title
                  Artist = artist
                  DurationMs = duration
                  CueStartMs = cueStartMs
                  CueDurationMs = cueDurationMs }
        }

    member private this.ParseAssignment(bytes: byte array) =
        let operation = "stream-node.playback.current"
        result {
            let! root = Json.objectRoot bytes operation
            return! this.ParseAssignmentElement(operation, root)
        }

    member private this.ParseUpcoming(bytes: byte array) =
        let operation = "stream-node.playback.upcoming"
        result {
            let! root = Json.objectRoot bytes operation
            let parseSlot name =
                match Json.tryProperty name root with
                | Some value when value.ValueKind = JsonValueKind.Object -> this.ParseAssignmentElement(operation, value) |> Result.map Some
                | _ -> Ok None
            let! current = parseSlot "current"
            let! next = parseSlot "next"
            return (current, next)
        }

    member private this.MapResponse operation status =
        if status = HttpStatusCode.Unauthorized || status = HttpStatusCode.Forbidden then Error(BackendError.UnauthorizedResponse operation)
        else Error(BackendError.Http(operation, int status))

    interface IBackendClient with
        member this.GetControlPageAsync(afterGeneration, limit, token) =
            task {
                let query = sprintf "/api/v0/stream-node/control?afterPlaybackGeneration=%d&limit=%d" afterGeneration limit
                let! response = this.GetAsync query token
                match response with
                | Error error -> return Error error
                | Ok(status, bytes) when status = HttpStatusCode.OK -> return this.ParseControlPage bytes
                | Ok(status, _) -> return this.MapResponse "stream-node.control" status
            }

        member this.PollControlAsync(afterGeneration, token) =
            task {
                let mutable cursor = afterGeneration
                let mutable pages: PlaybackCommand list = []
                let mutable state: ControlState option = None
                let mutable donePaging = false
                let mutable failure: BackendError option = None
                while not donePaging && failure.IsNone do
                    let! page = (this :> IBackendClient).GetControlPageAsync(cursor, 100, token)
                    match page with
                    | Error error -> failure <- Some error
                    | Ok value ->
                        state <- Some { DesiredState = value.DesiredState; RestartGeneration = value.RestartGeneration }
                        pages <- pages @ (value.PlaybackCommands |> Array.toList)
                        if value.PlaybackCommands.Length = 0 then donePaging <- true
                        elif value.NextPlaybackGeneration <= cursor then failure <- Some(BackendError.InvalidResponse "stream-node.control")
                        else cursor <- value.NextPlaybackGeneration
                match failure, state with
                | Some error, _ -> return Error error
                | None, Some value -> return Ok(value, pages, cursor)
                | None, None -> return Error(BackendError.InvalidResponse "stream-node.control")
            }

        member this.GetAssignmentAsync token =
            task {
                let! response = this.GetAsync "/api/v0/stream-node/playback/current" token
                match response with
                | Error error -> return Error error
                | Ok(status, _) when status = HttpStatusCode.NoContent -> return Ok None
                | Ok(status, bytes) when status = HttpStatusCode.OK -> return this.ParseAssignment bytes |> Result.map Some
                | Ok(status, _) -> return this.MapResponse "stream-node.playback.current" status
            }

        member this.GetUpcomingAsync token =
            task {
                let! response = this.GetAsync "/api/v0/stream-node/playback/upcoming" token
                match response with
                | Error error -> return Error error
                | Ok(status, bytes) when status = HttpStatusCode.OK -> return this.ParseUpcoming bytes
                | Ok(status, _) -> return this.MapResponse "stream-node.playback.upcoming" status
            }

        member this.PostHeartbeatAsync(heartbeat, token) =
            task {
                let toObject value = value |> Option.map box |> Option.defaultValue null
                let payload = Dictionary<string, obj>()
                payload["status"] <- box heartbeat.Status
                payload["failureReason"] <- toObject heartbeat.FailureReason
                let metadata = Dictionary<string, obj>()
                metadata["bitrateKbps"] <- toObject heartbeat.Metadata.BitrateKbps
                metadata["restartAttempt"] <- toObject heartbeat.Metadata.RestartAttempt
                metadata["activeQueueItemId"] <- heartbeat.Metadata.ActiveQueueItemId |> Option.map (fun x -> box (x.ToString("D"))) |> Option.defaultValue null
                payload["metadata"] <- box metadata
                let! response = this.PostAsync "/api/v0/stream-node/heartbeat" payload token
                match response with
                | Error error -> return Error error
                | Ok(status, _) when status = HttpStatusCode.NoContent -> return Ok()
                | Ok(status, _) -> return this.MapResponse "stream-node.heartbeat" status
            }

        member this.RenewLeaseAsync(assignment, token) =
            task {
                let payload = {| claimOwner = assignment.ClaimOwner.ToString("D"); claimAttempt = assignment.ClaimAttempt |}
                let path = sprintf "/api/v0/stream-node/playback/%O/lease" assignment.QueueItemId
                let! response = this.PostAsync path payload token
                return
                    match response with
                    | Error(BackendError.UnauthorizedResponse _) -> CallbackResult.Unauthorized
                    | Error _ -> CallbackResult.TransientError
                    | Ok(status, _) when status = HttpStatusCode.NoContent -> CallbackResult.Accepted
                    | Ok(status, _) when status = HttpStatusCode.Conflict -> CallbackResult.Stale
                    | Ok(status, _) when status = HttpStatusCode.Unauthorized || status = HttpStatusCode.Forbidden -> CallbackResult.Unauthorized
                    | Ok _ -> CallbackResult.TransientError
            }

        member this.CompleteAsync(assignment, completion, token) =
            task {
                let payload = Dictionary<string, obj>()
                payload["claimOwner"] <- box (assignment.ClaimOwner.ToString("D"))
                payload["claimAttempt"] <- box assignment.ClaimAttempt
                match completion with
                | PlaybackCompletion.Played -> payload["status"] <- box "played"
                | PlaybackCompletion.Failed reason -> payload["status"] <- box "failed"; payload["failureReason"] <- box (SafeText.boundedReason reason)
                let path = sprintf "/api/v0/stream-node/playback/%O/completion" assignment.QueueItemId
                let! response = this.PostAsync path payload token
                return
                    match response with
                    | Error(BackendError.UnauthorizedResponse _) -> CallbackResult.Unauthorized
                    | Error _ -> CallbackResult.TransientError
                    | Ok(status, _) when status = HttpStatusCode.NoContent -> CallbackResult.Accepted
                    | Ok(status, _) when status = HttpStatusCode.Conflict -> CallbackResult.Stale
                    | Ok(status, _) when status = HttpStatusCode.Unauthorized || status = HttpStatusCode.Forbidden -> CallbackResult.Unauthorized
                    | Ok _ -> CallbackResult.TransientError
            }

    member this.GetControlPageAsync(afterGeneration: int64, limit: int, token: CancellationToken) =
        (this :> IBackendClient).GetControlPageAsync(afterGeneration, limit, token)

    member this.PollControlAsync(afterGeneration: int64, token: CancellationToken) =
        (this :> IBackendClient).PollControlAsync(afterGeneration, token)

    member this.GetAssignmentAsync(token: CancellationToken) =
        (this :> IBackendClient).GetAssignmentAsync(token)

    member this.GetUpcomingAsync(token: CancellationToken) =
        (this :> IBackendClient).GetUpcomingAsync(token)

    member this.PostHeartbeatAsync(heartbeat: Heartbeat, token: CancellationToken) =
        (this :> IBackendClient).PostHeartbeatAsync(heartbeat, token)

    member this.RenewLeaseAsync(assignment: Assignment, token: CancellationToken) =
        (this :> IBackendClient).RenewLeaseAsync(assignment, token)

    member this.CompleteAsync(assignment: Assignment, completion: PlaybackCompletion, token: CancellationToken) =
        (this :> IBackendClient).CompleteAsync(assignment, completion, token)

    interface IDisposable with
        member _.Dispose() = if ownsClient then client.Dispose()
