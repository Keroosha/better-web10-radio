namespace Web10.Radio.StreamNode.Tools

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks

[<CLIMutable>]
type HeartbeatMetadata =
    { BitrateKbps: int
      RestartAttempt: int option
      ActiveQueueItemId: string option }

[<CLIMutable>]
type HeartbeatPayload =
    { Status: string
      FailureReason: string option
      Metadata: HeartbeatMetadata }

[<CLIMutable>]
type LeasePayload =
    { ClaimOwner: string
      ClaimAttempt: int }

[<CLIMutable>]
type CompletionPayload =
    { ClaimOwner: string
      ClaimAttempt: int
      Status: string }

type Assignment =
    { QueueItemId: string
      ClaimOwner: string
      ClaimAttempt: int
      DurationMs: int
      Title: string
      Artist: string }

[<CLIMutable>]
type TrackCandidate =
    { Id: string
      Title: string
      HasCachedFile: bool }

type ToolError(operation: string, status: string) =
    inherit Exception(sprintf "%s status=%s" operation status)
    member _.Operation = operation
    member _.Status = status

module Json =
    let options =
        let value = JsonSerializerOptions(JsonSerializerDefaults.Web)
        value.PropertyNameCaseInsensitive <- true
        value

    let serialize value = JsonSerializer.Serialize(value, options)
    let tryDocument (text: string) =
        try
            use document = JsonDocument.Parse(text)
            Some(document.RootElement.Clone())
        with _ -> None

module Time =
    let system = TimeProvider.System

    type Deadline =
        { Provider: TimeProvider
          StartTimestamp: int64
          Timeout: TimeSpan }

    let create (provider: TimeProvider) timeout =
        if timeout <= TimeSpan.Zero then
            raise (ToolError("arguments", "invalid"))
        { Provider = provider
          StartTimestamp = provider.GetTimestamp()
          Timeout = timeout }

    let remaining deadline =
        let elapsed = deadline.Provider.GetElapsedTime(deadline.StartTimestamp)
        let left = deadline.Timeout - elapsed
        if left <= TimeSpan.Zero then TimeSpan.Zero else left

    let ensureRemaining deadline operation =
        let left = remaining deadline
        if left <= TimeSpan.Zero then raise (ToolError(operation, "timeout"))
        left

    let requestTimeout deadline operation =
        let left = ensureRemaining deadline operation
        if left > TimeSpan.FromSeconds 5.0 then TimeSpan.FromSeconds 5.0 else left

    let delay (provider: TimeProvider) delayTime cancellationToken =
        if delayTime <= TimeSpan.Zero then Task.CompletedTask
        else Task.Delay(delayTime, provider, cancellationToken)

module Parsing =
    let option (name: string) (fallback: string) (args: Map<string, string>) =
        args |> Map.tryFind name |> Option.defaultValue fallback

    let required (name: string) (args: Map<string, string>) =
        match Map.tryFind name args with
        | Some value when not (String.IsNullOrWhiteSpace value) -> value
        | _ -> raise (ToolError(name, "required"))

    let positiveInt (name: string) (value: string) =
        match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, parsed when parsed > 0 -> parsed
        | _ -> raise (ToolError(name, "invalid"))

    let positiveFloat (name: string) (value: string) =
        match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, parsed when Double.IsFinite parsed && parsed > 0.0 -> parsed
        | _ -> raise (ToolError(name, "invalid"))

    let nonNegativeInt (name: string) (value: string) =
        match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, parsed when parsed >= 0 -> parsed
        | _ -> raise (ToolError(name, "invalid"))

    let boolValue (name: string) (value: string) =
        match Boolean.TryParse(value) with
        | true, parsed -> parsed
        | _ -> raise (ToolError(name, "invalid"))

    let requireChoice (name: string) (choices: Set<string>) (value: string) =
        if Set.contains value choices then value else raise (ToolError(name, "invalid"))

    let parseOptions (tokens: string list) =
        let rec loop (remaining: string list) (result: Map<string, string>) =
            match remaining with
            | [] -> result
            | (name: string) :: (value: string) :: tail when name.StartsWith("--", StringComparison.Ordinal) ->
                loop tail (result |> Map.add (name.Substring 2) value)
            | (name: string) :: _ when name.StartsWith("--", StringComparison.Ordinal) ->
                raise (ToolError(name, "missing-value"))
            | (token: string) :: _ -> raise (ToolError(token, "invalid"))
        loop tokens Map.empty

module Document =
    let private property (name: string) (document: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if document.TryGetProperty(name, &value) then Some value else None

    let string (name: string) (document: JsonElement) : string option =
        match property name document with
        | Some value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
        | _ -> None

    let int name document =
        match property name document with
        | Some value ->
            let mutable number = 0
            if value.TryGetInt32(&number) then Some number else None
        | None -> None

    let bool name document =
        match property name document with
        | Some value when value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False ->
            Some(value.GetBoolean())
        | _ -> None

    let nullableString (name: string) (document: JsonElement) =
        match property name document with
        | Some value when value.ValueKind = JsonValueKind.Null -> Some None
        | Some value when value.ValueKind = JsonValueKind.String -> Some(Some(value.GetString()))
        | _ -> None

    let objectProperty name document =
        match property name document with
        | Some value when value.ValueKind = JsonValueKind.Object -> Some value
        | _ -> None

    let arrayElements name document =
        match property name document with
        | Some value when value.ValueKind = JsonValueKind.Array -> Some(value.EnumerateArray() |> Seq.toArray)
        | _ -> None

    let assignment (document: JsonElement) =
        match string "queueItemId" document, string "claimOwner" document, int "claimAttempt" document,
              int "durationMs" document, string "title" document, string "artist" document with
        | Some queueItemId, Some claimOwner, Some claimAttempt, Some durationMs, Some title, Some artist
            when not (String.IsNullOrWhiteSpace queueItemId)
                 && not (String.IsNullOrWhiteSpace claimOwner)
                 && claimAttempt > 0
                 && durationMs >= 0 ->
            Some
                { QueueItemId = queueItemId
                  ClaimOwner = claimOwner
                  ClaimAttempt = claimAttempt
                  DurationMs = durationMs
                  Title = title
                  Artist = artist }
        | _ -> None

    let trackCandidates (document: JsonElement) =
        let items =
            if document.ValueKind = JsonValueKind.Array then Some(document.EnumerateArray() |> Seq.toArray)
            else arrayElements "items" document
        items
        |> Option.map (Array.choose (fun item ->
            match string "id" item, string "title" item with
            | Some id, Some title when not (String.IsNullOrWhiteSpace id) ->
                Some
                    { Id = id
                      Title = title
                      HasCachedFile = bool "hasCachedFile" item |> Option.defaultValue true }
            | _ -> None))

    let intField name document =
        match int name document with
        | Some value -> value
        | None -> raise (ToolError("response", "invalid"))

    let stringField name document =
        match string name document with
        | Some value when not (String.IsNullOrWhiteSpace value) -> value
        | _ -> raise (ToolError("response", "invalid"))
