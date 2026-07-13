namespace Web10.Radio.API

open System
open System.Collections.Generic
open System.Globalization
open System.Text

type CueSheetTrack =
    { FileName: string
      TrackNumber: int
      Title: string option
      Artist: string option
      Album: string option
      StartMs: int }

type CueSheetError =
    { SheetPath: string
      LineNumber: int
      Message: string }

module CueSheet =
    type private ParsedTrack =
        { FileName: string
          TrackNumber: int
          Title: string option
          Artist: string option
          Index01Ms: int option
          Index01LineNumber: int option
          LineNumber: int }

    let private error sheetPath lineNumber message =
        Error
            { SheetPath = sheetPath
              LineNumber = lineNumber
              Message = message }

    let private decode sheetPath (bytes: byte array) =
        try
            let utf8 = UTF8Encoding(false, true)
            Ok(utf8.GetString(bytes))
        with :? DecoderFallbackException ->
            try
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)
                Ok(Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes))
            with
            | :? DecoderFallbackException -> error sheetPath 1 "The CUE sheet is neither strict UTF-8 nor Windows-1251."
            | ex -> error sheetPath 1 ("The CUE sheet could not be decoded: " + ex.Message)

    let private unquote (value: string) =
        let trimmed = value.Trim()
        if trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"') then
            trimmed.Substring(1, trimmed.Length - 2)
        else
            trimmed

    let private directive (line: string) =
        let trimmed = line.Trim()
        if String.IsNullOrWhiteSpace(trimmed) then None
        else
            let separator = trimmed.IndexOfAny([| ' '; '\t' |])
            if separator < 0 then Some(trimmed.ToUpperInvariant(), "")
            else Some(trimmed.Substring(0, separator).ToUpperInvariant(), trimmed.Substring(separator).Trim())

    let private fileName (value: string) =
        let trimmed = value.Trim()
        if trimmed.StartsWith('"') then
            let closingQuote = trimmed.IndexOf('"', 1)
            if closingQuote > 0 then trimmed.Substring(1, closingQuote - 1)
            else ""
        else
            trimmed.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead
            |> Option.defaultValue ""

    let private parseTrackNumber (value: string) =
        let parts = value.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
        if parts.Length <> 2 || not (parts.[1].Equals("AUDIO", StringComparison.OrdinalIgnoreCase)) then
            Ok None
        else
            match Int32.TryParse(parts.[0], NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, trackNumber when trackNumber > 0 -> Ok(Some trackNumber)
            | _ -> Error "TRACK must contain a positive track number."

    let private parseIndex01 sheetPath lineNumber (value: string) =
        let parts = value.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
        if parts.Length <> 2 || not (parts.[0].Equals("01", StringComparison.Ordinal)) then
            Ok None
        else
            let fields = parts.[1].Split(':')
            if fields.Length <> 3 then
                error sheetPath lineNumber "INDEX 01 must have mm:ss:ff timecode."
            else
                match Int64.TryParse(fields.[0], NumberStyles.None, CultureInfo.InvariantCulture),
                      Int64.TryParse(fields.[1], NumberStyles.None, CultureInfo.InvariantCulture),
                      Int64.TryParse(fields.[2], NumberStyles.None, CultureInfo.InvariantCulture) with
                | (true, minutes), (true, seconds), (true, frames)
                    when minutes >= 0L && seconds >= 0L && seconds < 60L && frames >= 0L && frames < 75L ->
                    let totalFrames = minutes * 60L * 75L + seconds * 75L + frames
                    let milliseconds = Math.Round((decimal totalFrames * 1000M) / 75M, 0, MidpointRounding.AwayFromZero)
                    if milliseconds > decimal Int32.MaxValue then
                        error sheetPath lineNumber "INDEX 01 timecode exceeds the supported duration range."
                    else
                        Ok(Some(int milliseconds))
                | _ -> error sheetPath lineNumber "INDEX 01 contains an invalid mm:ss:ff timecode."

    let parseBytes (sheetPath: string) (bytes: byte array) : Result<CueSheetTrack list, CueSheetError> =
        match decode sheetPath bytes with
        | Error value -> Error value
        | Ok decoded when String.IsNullOrWhiteSpace(decoded.TrimStart('\uFEFF')) ->
            error sheetPath 1 "The CUE sheet is empty."
        | Ok decoded ->
            let text = decoded.TrimStart('\uFEFF')
            let lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            let completed = ResizeArray<ParsedTrack>()
            let seenTrackNumbers = HashSet<int>()
            let lastStartByFile = Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            let mutable currentFile: string option = None
            let mutable globalTitle: string option = None
            let mutable globalArtist: string option = None
            let mutable activeTrack: ParsedTrack option = None
            let mutable ignoredTrack = false
            let mutable failure: CueSheetError option = None

            let fail lineNumber message =
                if Option.isNone failure then
                    failure <- Some { SheetPath = sheetPath; LineNumber = lineNumber; Message = message }

            let flushActive () =
                match activeTrack with
                | None -> ()
                | Some track ->
                    match track.Index01Ms with
                    | None -> fail track.LineNumber (sprintf "TRACK %d has no INDEX 01." track.TrackNumber)
                    | Some startMs ->
                        match lastStartByFile.TryGetValue(track.FileName) with
                        | true, previous when startMs <= previous ->
                            let indexLineNumber = track.Index01LineNumber |> Option.defaultValue track.LineNumber
                            fail indexLineNumber (sprintf "TRACK %d INDEX 01 must increase within FILE %s." track.TrackNumber track.FileName)
                        | _ ->
                            lastStartByFile.[track.FileName] <- startMs
                            completed.Add(track)
                activeTrack <- None
                ignoredTrack <- false

            for index in 0 .. lines.Length - 1 do
                if Option.isNone failure then
                    let lineNumber = index + 1
                    match directive lines.[index] with
                    | None -> ()
                    | Some(keyword, value) ->
                        match keyword with
                        | "REM"
                        | "ISRC" -> ()
                        | "FILE" ->
                            flushActive ()
                            let parsedFileName = fileName value
                            if String.IsNullOrWhiteSpace(parsedFileName) then
                                fail lineNumber "FILE must contain a nonempty file name."
                            else
                                currentFile <- Some parsedFileName
                        | "TRACK" ->
                            flushActive ()
                            match parseTrackNumber value with
                            | Error message -> fail lineNumber message
                            | Ok None -> ignoredTrack <- true
                            | Ok(Some trackNumber) ->
                                match currentFile with
                                | None -> fail lineNumber "A TRACK AUDIO directive appeared before FILE."
                                | Some sourceFile when not (seenTrackNumbers.Add(trackNumber)) ->
                                    fail lineNumber (sprintf "TRACK %d is duplicated." trackNumber)
                                | Some sourceFile ->
                                    activeTrack <-
                                        Some
                                            { FileName = sourceFile
                                              TrackNumber = trackNumber
                                              Title = None
                                              Artist = None
                                              Index01Ms = None
                                              Index01LineNumber = None
                                              LineNumber = lineNumber }
                        | "TITLE" when not ignoredTrack ->
                            match activeTrack with
                            | Some track -> activeTrack <- Some { track with Title = Some(unquote value) }
                            | None -> globalTitle <- Some(unquote value)
                        | "PERFORMER" when not ignoredTrack ->
                            match activeTrack with
                            | Some track -> activeTrack <- Some { track with Artist = Some(unquote value) }
                            | None -> globalArtist <- Some(unquote value)
                        | "INDEX" when not ignoredTrack ->
                            match activeTrack with
                            | None -> ()
                            | Some track ->
                                match parseIndex01 sheetPath lineNumber value with
                                | Error parseError -> failure <- Some parseError
                                | Ok None -> ()
                                | Ok(Some startMs) ->
                                    match track.Index01Ms with
                                    | Some _ -> fail lineNumber (sprintf "TRACK %d contains more than one INDEX 01." track.TrackNumber)
                                    | None -> activeTrack <- Some { track with Index01Ms = Some startMs; Index01LineNumber = Some lineNumber }
                        | _ -> ()

            if Option.isNone failure then flushActive ()

            match failure with
            | Some value -> Error value
            | None ->
                completed
                |> Seq.map (fun track ->
                    { FileName = track.FileName
                      TrackNumber = track.TrackNumber
                      Title = track.Title |> Option.orElse globalTitle
                      Artist = track.Artist |> Option.orElse globalArtist
                      Album = globalTitle
                      StartMs = track.Index01Ms |> Option.defaultWith (fun () -> invalidOp "INDEX 01 was validated before emission.") })
                |> Seq.toList
                |> Ok
