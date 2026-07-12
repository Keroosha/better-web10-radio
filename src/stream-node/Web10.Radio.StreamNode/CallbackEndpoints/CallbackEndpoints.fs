namespace Web10.Radio.StreamNode

open System
open System.IO
open System.Net
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

[<CLIMutable>]
type CallbackFence =
    { QueueItemId: Guid
      ClaimOwner: Guid
      ClaimAttempt: int }

[<RequireQualifiedAccess>]
type CallbackPayload =
    | Started of CallbackFence
    | Ended of CallbackFence
    | OutputFailed

type ICallbackSink =
    abstract IsAlive: bool
    abstract Accept: CallbackPayload -> bool

type CallbackServer(port: int, sink: ICallbackSink) =
    let listener = new HttpListener()
    let cancellation = new CancellationTokenSource()
    let mutable loopTask: Task option = None
    let maxBytes = 4 * 1024

    let writeStatus (context: HttpListenerContext) status =
        context.Response.StatusCode <- status
        context.Response.ContentLength64 <- 0L
        context.Response.Close()

    let tryGuid (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.String then None
        else
            let mutable parsed = Guid.Empty
            if Guid.TryParse(value.GetString(), &parsed) && parsed <> Guid.Empty then Some parsed else None

    let tryFence (root: JsonElement) =
        let mutable queue = Unchecked.defaultof<JsonElement>
        let mutable owner = Unchecked.defaultof<JsonElement>
        let mutable attempt = Unchecked.defaultof<JsonElement>
        if not (root.TryGetProperty("queueItemId", &queue)) || not (root.TryGetProperty("claimOwner", &owner)) || not (root.TryGetProperty("claimAttempt", &attempt)) then None
        else
            match tryGuid queue, tryGuid owner with
            | Some queueItemId, Some claimOwner ->
                let mutable claimAttempt = 0
                if attempt.ValueKind = JsonValueKind.Number && attempt.TryGetInt32(&claimAttempt) && claimAttempt > 0 then Some { QueueItemId = queueItemId; ClaimOwner = claimOwner; ClaimAttempt = claimAttempt }
                else None
            | _ -> None

    let parsePayload path (body: byte array) =
        try
            use document = JsonDocument.Parse(body)
            let root = document.RootElement
            if root.ValueKind <> JsonValueKind.Object then None
            elif path = "/callbacks/output-failed" && not (root.EnumerateObject() |> Seq.isEmpty) then None
            elif path = "/callbacks/output-failed" then Some CallbackPayload.OutputFailed
            elif path = "/callbacks/started" && (root.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Set.ofSeq) = Set.ofList [ "queueItemId"; "claimOwner"; "claimAttempt" ] then tryFence root |> Option.map CallbackPayload.Started
            elif path = "/callbacks/ended" && (root.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Set.ofSeq) = Set.ofList [ "queueItemId"; "claimOwner"; "claimAttempt" ] then tryFence root |> Option.map CallbackPayload.Ended
            else None
        with :? JsonException -> None

    let readBody (request: HttpListenerRequest) =
        if request.ContentLength64 < 0L || request.ContentLength64 > int64 maxBytes then None
        else
            try
                let length = int request.ContentLength64
                let body = Array.zeroCreate<byte> length
                let mutable offset = 0
                while offset < length do
                    let count = request.InputStream.Read(body, offset, length - offset)
                    if count = 0 then raise (EndOfStreamException())
                    offset <- offset + count
                Some body
            with _ -> None

    let handle (context: HttpListenerContext) =
        task {
            let request = context.Request
            if request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && request.Url.AbsolutePath = "/healthz" && sink.IsAlive then
                writeStatus context 204
            elif not (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)) then
                writeStatus context 404
            else
                match request.Url.AbsolutePath, readBody request with
                | ("/callbacks/started" | "/callbacks/ended" | "/callbacks/output-failed"), Some body ->
                    match parsePayload request.Url.AbsolutePath body with
                    | None -> writeStatus context 400
                    | Some payload -> writeStatus context (if sink.Accept payload then 204 else 409)
                | _ -> writeStatus context 404
        }

    let rec loop () =
        task {
            while not cancellation.IsCancellationRequested do
                try
                    let! context = listener.GetContextAsync()
                    do! handle context
                with
                | :? HttpListenerException when cancellation.IsCancellationRequested -> ()
                | :? ObjectDisposedException when cancellation.IsCancellationRequested -> ()
                | :? OperationCanceledException when cancellation.IsCancellationRequested -> ()
                | _ -> ()
        }

    member _.Start() =
        if loopTask.IsNone then
            listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
            listener.Start()
            loopTask <- Some(loop ())

    member _.StopAsync() =
        task {
            cancellation.Cancel()
            if listener.IsListening then listener.Stop()
            match loopTask with
            | Some task ->
                try do! task with _ -> ()
            | None -> ()
        }

    member _.Port = port
