namespace Web10.Radio.StreamNode

open System
open System.IO
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks

module Liquidsoap =
    let private escapeAnnotation (value: string) =
        if isNull value then ""
        else value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n")

    let private extensionForContentType (contentType: string) =
        match (if isNull contentType then "" else contentType.Trim().ToLowerInvariant()) with
        | "audio/mpeg" | "audio/mp3" -> "mp3"
        | "audio/ogg" | "application/ogg" | "audio/vorbis" | "audio/x-vorbis+ogg" -> "ogg"
        | "audio/flac" | "audio/x-flac" -> "flac"
        | "audio/wav" | "audio/x-wav" | "audio/wave" | "audio/vnd.wave" -> "wav"
        | "audio/mp4" | "audio/aac" | "audio/x-m4a" | "audio/m4a" -> "m4a"
        | "audio/opus" -> "opus"
        | _ -> "mp3"

    let mediaProtocolUri (assignment: Assignment) =
        match assignment.CueStartMs, assignment.CueDurationMs with
        | Some startMs, Some durationMs ->
            sprintf "web10cue:%s:%d:%d.flac" (assignment.QueueItemId.ToString("D")) startMs durationMs
        | _ ->
            sprintf "web10media:%s.%s" (assignment.QueueItemId.ToString("D")) (extensionForContentType assignment.ContentType)

    let annotatedFileUri assignment path =
        let metadata =
            [ sprintf "web10_queue_item_id=\"%s\"" (escapeAnnotation (assignment.QueueItemId.ToString("D")))
              sprintf "web10_claim_owner=\"%s\"" (escapeAnnotation (assignment.ClaimOwner.ToString("D")))
              sprintf "web10_claim_attempt=\"%d\"" assignment.ClaimAttempt
              sprintf "title=\"%s\"" (escapeAnnotation assignment.Title)
              sprintf "artist=\"%s\"" (escapeAnnotation assignment.Artist) ]
            |> String.concat ","
        sprintf "annotate:%s:%s" metadata path

type LiquidsoapClient(config: RuntimeConfig, ?socketPath: string, ?timeProvider: TimeProvider) =
    let socket = defaultArg socketPath config.LiquidsoapSocket
    let timeProvider = defaultArg timeProvider TimeProvider.System
    let timeout = TimeSpan.FromSeconds 2.0

    member private _.SendAsync(command: string, token: CancellationToken) =
        task {
            use linked = CancellationTokenSource.CreateLinkedTokenSource(token)
            linked.CancelAfter(timeout)
            let endpoint = UnixDomainSocketEndPoint(socket)
            let connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            try
                do! connection.ConnectAsync(endpoint, linked.Token)
                use stream = new NetworkStream(connection, ownsSocket = true)
                let request = Encoding.UTF8.GetBytes(command + "\n")
                do! stream.WriteAsync(request, linked.Token)
                do! stream.FlushAsync(linked.Token)
                let buffer = Array.zeroCreate<byte> 4096
                use output = new MemoryStream()
                let mutable complete = false
                while not complete && output.Length < 65536L do
                    let! count = stream.ReadAsync(buffer, linked.Token)
                    if count = 0 then complete <- true
                    else
                        output.Write(buffer, 0, count)
                        let text = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n")
                        complete <- text.EndsWith("\nEND\n", StringComparison.Ordinal)
                let response = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n")
                let lines = response.Split([| '\n' |], StringSplitOptions.None)
                let normalized =
                    if lines.Length > 0 && lines[lines.Length - 1] = "" then
                        let trimmed = lines |> Array.take (lines.Length - 1)
                        if trimmed.Length > 0 && trimmed[trimmed.Length - 1] = "END" then
                            String.Join("\n", trimmed |> Array.take (trimmed.Length - 1))
                        else response
                    else response
                return normalized
            finally
                connection.Dispose()
        }

    member this.CommandAsync(command: string, token: CancellationToken) =
        this.SendAsync(command, token)

    member this.PushAsync(assignment: Assignment, path: string, token: CancellationToken) =
        this.SendAsync(sprintf "web10.push %s" (Liquidsoap.annotatedFileUri assignment path), token)

    member this.IsReadyAsync(token: CancellationToken) =
        task {
            try
                let! response = this.SendAsync("web10-video.is_ready", token)
                return response.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
            with _ -> return false
        }

    member this.IsOutputStartedAsync(token: CancellationToken) =
        task {
            try
                let! response = this.SendAsync("web10-rtmp.is_started", token)
                return response.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
            with _ -> return false
        }
