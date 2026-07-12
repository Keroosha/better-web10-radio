namespace Web10.Radio.StreamNode.Tools

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

[<CLIMutable>]
type ToolResponse =
    { Status: int
      Body: string option }

module private Http =
    let uri (baseUrl: string) (path: string) =
        match Uri.TryCreate(baseUrl.TrimEnd('/') + path, UriKind.Absolute) with
        | true, value when value.Scheme = Uri.UriSchemeHttp || value.Scheme = Uri.UriSchemeHttps -> value
        | _ -> raise (ToolError("arguments", "invalid"))

    let readResponse (response: HttpResponseMessage) (cancellationToken: CancellationToken) =
        task {
            let! body = response.Content.ReadAsStringAsync(cancellationToken)
            return
                { Status = int response.StatusCode
                  Body = if String.IsNullOrEmpty body then None else Some body }
        }

    let send (client: HttpClient) (baseUrl: string) (token: string) (method: HttpMethod) (path: string) (payload: string option) (includeCsrf: bool) (deadline: Time.Deadline) (cancellationToken: CancellationToken) =
        task {
            let timeout = Time.requestTimeout deadline path
            use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            timeoutCts.CancelAfter timeout
            use request = new HttpRequestMessage(method, uri baseUrl path)
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
            request.Headers.UserAgent.ParseAdd("Web10.Radio.StreamNode.Tools/1.0")
            if not (String.IsNullOrWhiteSpace token) then
                request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
            if includeCsrf then
                match payload with
                | Some _ -> ()
                | None -> ()
            match payload with
            | Some body ->
                request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
            | None -> ()
            try
                use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                return! readResponse response timeoutCts.Token
            with
            | :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                return { Status = 0; Body = None }
            | :? HttpRequestException ->
                return { Status = 0; Body = None }
        }

module private Decode =
    let document operation response expectedStatus =
        if response.Status <> expectedStatus then raise (ToolError(operation, if response.Status = 0 then "unavailable" else string response.Status))
        match response.Body |> Option.bind Json.tryDocument with
        | Some value -> value
        | None -> raise (ToolError(operation, "invalid-response"))

    let accepted operation response expected =
        if response.Status <> expected then
            raise (ToolError(operation, if response.Status = 0 then "unavailable" else string response.Status))

    let noContent operation response expected = accepted operation response expected
    let jsonBody value = Some(Json.serialize value)

type BackendConnection(baseUrl: string, callbackToken: string, ?bitrateKbps: int, ?httpClient: HttpClient) =
    let bitrate = defaultArg bitrateKbps 192
    let client = defaultArg httpClient (new HttpClient(new HttpClientHandler(AllowAutoRedirect = false)))

    member _.GetAssignment(deadline: Time.Deadline, cancellationToken: CancellationToken) =
        task {
            let! response = Http.send client baseUrl callbackToken HttpMethod.Get "/api/v0/stream-node/playback/current" None false deadline cancellationToken
            if response.Status = 204 then
                return None
            else
                let document = Decode.document "playback-current" response 200
                match Document.assignment document with
                | Some assignment -> return Some assignment
                | None -> return raise (ToolError("playback-current", "invalid-response"))
        }

    member _.Heartbeat(status: string, failureReason: string option, restartAttempt: int option, activeQueueItemId: string option, deadline: Time.Deadline, cancellationToken: CancellationToken) =
        task {
            let payload =
                { Status = status
                  FailureReason = failureReason
                  Metadata =
                    { BitrateKbps = bitrate
                      RestartAttempt = restartAttempt
                      ActiveQueueItemId = activeQueueItemId } }
            let! response = Http.send client baseUrl callbackToken HttpMethod.Post "/api/v0/stream-node/heartbeat" (Decode.jsonBody payload) false deadline cancellationToken
            Decode.noContent "heartbeat" response 204
        }

    member _.RenewLease(assignment: Assignment, deadline: Time.Deadline, cancellationToken: CancellationToken) =
        task {
            let payload =
                { ClaimOwner = assignment.ClaimOwner
                  ClaimAttempt = assignment.ClaimAttempt }
            let path = sprintf "/api/v0/stream-node/playback/%s/lease" (Uri.EscapeDataString assignment.QueueItemId)
            let! response = Http.send client baseUrl callbackToken HttpMethod.Post path (Decode.jsonBody payload) false deadline cancellationToken
            Decode.noContent "playback-lease" response 204
        }

    member _.Complete(assignment: Assignment, status: string, deadline: Time.Deadline, cancellationToken: CancellationToken) =
        task {
            let payload =
                { ClaimOwner = assignment.ClaimOwner
                  ClaimAttempt = assignment.ClaimAttempt
                  Status = status }
            let path = sprintf "/api/v0/stream-node/playback/%s/completion" (Uri.EscapeDataString assignment.QueueItemId)
            let! response = Http.send client baseUrl callbackToken HttpMethod.Post path (Decode.jsonBody payload) false deadline cancellationToken
            Decode.noContent "playback-completion" response 204
        }

    member _.GetControl(afterGeneration: int64, limit: int, deadline: Time.Deadline, cancellationToken: CancellationToken) =
        task {
            let query = sprintf "?afterPlaybackGeneration=%d&limit=%d" afterGeneration limit
            let! response = Http.send client baseUrl callbackToken HttpMethod.Get ("/api/v0/stream-node/control" + query) None false deadline cancellationToken
            let document = Decode.document "stream-control" response 200
            let desiredState = Document.stringField "desiredState" document
            let generation =
                match Document.int "restartGeneration" document with
                | Some value when value >= 0 -> int64 value
                | _ -> raise (ToolError("stream-control", "invalid-response"))
            return desiredState, generation
        }


type AdminConnection(baseUrl: string, username: string, password: string, ?httpClient: HttpClient) as this =
    let cookies = CookieContainer()
    let handler = HttpClientHandler(AllowAutoRedirect = false, CookieContainer = cookies, UseCookies = true)
    let client = defaultArg httpClient (new HttpClient(handler))
    let mutable csrfToken: string option = None

    member private _.Request(method: HttpMethod, operation: string, path: string, payload: obj option, deadline: Time.Deadline, cancellationToken: CancellationToken) =
        task {
            let body = payload |> Option.map Json.serialize
            let! response = Http.send client baseUrl "" method path body false deadline cancellationToken
            return response
        }

    member _.Login(deadline: Time.Deadline, cancellationToken: CancellationToken) =
        task {
            let payload = {| Username = username; Password = password |}
            let! response = (this :> AdminConnection).Request(HttpMethod.Post, "login", "/api/v0/admin/auth/login", Some(box payload), deadline, cancellationToken)
            let document = Decode.document "login" response 200
            let csrf = Document.stringField "csrfToken" document
            csrfToken <- Some csrf
        }

    member _.Get(operation, path, deadline: Time.Deadline, cancellationToken: CancellationToken) =
        (this :> AdminConnection).Request(HttpMethod.Get, operation, path, None, deadline, cancellationToken)

    member _.Post(operation: string, path: string, payload: obj, expectedStatus: int, deadline: Time.Deadline, cancellationToken: CancellationToken) =
        task {
            let body = Some(Json.serialize payload)
            let! response = Http.send client baseUrl "" HttpMethod.Post path body false deadline cancellationToken
            Decode.accepted operation response expectedStatus
            return response
        }

    member _.Put(operation: string, path: string, payload: obj, expectedStatus: int, deadline: Time.Deadline, cancellationToken: CancellationToken) =
        task {
            let! response = Http.send client baseUrl "" HttpMethod.Put path (Some(Json.serialize payload)) false deadline cancellationToken
            Decode.accepted operation response expectedStatus
            return response
        }

    member _.GetJson(operation, path, deadline: Time.Deadline, cancellationToken: CancellationToken) =
        task {
            let! response = (this :> AdminConnection).Request(HttpMethod.Get, operation, path, None, deadline, cancellationToken)
            return Decode.document operation response 200
        }

    member _.PostWithCsrf(operation: string, path: string, payload: obj, expectedStatus: int, deadline: Time.Deadline, cancellationToken: CancellationToken) =
        task {
            let csrf = csrfToken |> Option.defaultWith (fun () -> raise (ToolError(operation, "not-authenticated")))
            let body = Some(Json.serialize payload)
            // CSRF is sent through a separate request to avoid broadening the bearer client.
            use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            timeoutCts.CancelAfter(Time.requestTimeout deadline operation)
            use request = new HttpRequestMessage(HttpMethod.Post, Http.uri baseUrl path)
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
            request.Headers.Add("X-CSRF-Token", csrf)
            request.Content <- new StringContent(body.Value, Encoding.UTF8, "application/json")
            try
                use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                let! result = Http.readResponse response timeoutCts.Token
                Decode.accepted operation result expectedStatus
                return result
            with
            | :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                return raise (ToolError(operation, "timeout"))
            | :? HttpRequestException ->
                return raise (ToolError(operation, "unavailable"))
        }
    member _.PutWithCsrf(operation: string, path: string, payload: obj, expectedStatus: int, deadline: Time.Deadline, cancellationToken: CancellationToken) =
        task {
            let csrf = csrfToken |> Option.defaultWith (fun () -> raise (ToolError(operation, "not-authenticated")))
            use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            timeoutCts.CancelAfter(Time.requestTimeout deadline operation)
            use request = new HttpRequestMessage(HttpMethod.Put, Http.uri baseUrl path)
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
            request.Headers.Add("X-CSRF-Token", csrf)
            request.Content <- new StringContent(Json.serialize payload, Encoding.UTF8, "application/json")
            try
                use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                let! result = Http.readResponse response timeoutCts.Token
                Decode.accepted operation result expectedStatus
                return result
            with
            | :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                return raise (ToolError(operation, "timeout"))
            | :? HttpRequestException ->
                return raise (ToolError(operation, "unavailable"))
        }

    member _.GetRtmpStat(url: string, deadline: Time.Deadline, cancellationToken: CancellationToken) =
        task {
            let! response = Http.send client url "" HttpMethod.Get "" None false deadline cancellationToken
            return response
        }

    member private _.Client = client
    member private _.BaseUrl = baseUrl
    member private _.Csrf = csrfToken
