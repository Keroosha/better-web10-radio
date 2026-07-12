namespace Web10.Radio.API

open System
open System.IO
open System.Security.Cryptography
open ATL

type ExtractedCover =
    { Bytes: byte array
      ContentType: string
      Extension: string
      Sha256: string }

type ExtractedTrackMetadata =
    { Title: string option
      Artist: string option
      Album: string option
      DurationMs: int option
      Cover: ExtractedCover option }

[<RequireQualifiedAccess>]
type TrackMetadataError =
    | FileNotFound of string
    | InvalidPath of string
    | ParseFailed of string
    | DurationInvalid of string

[<RequireQualifiedAccess>]
module TrackMetadata =
    [<Literal>]
    let private MaxCoverBytes = 10 * 1024 * 1024

    let private normalizeText (value: string) =
        if String.IsNullOrWhiteSpace value then None else Some(value.Trim())

    let private sha256Hex (bytes: byte array) =
        SHA256.HashData(bytes)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private sniffCover (bytes: byte array) =
        if isNull bytes || bytes.Length < 4 then
            None
        elif bytes.Length >= 3 && bytes[0] = 0xFFuy && bytes[1] = 0xD8uy && bytes[2] = 0xFFuy then
            Some("image/jpeg", ".jpg")
        elif bytes.Length >= 8
             && bytes[0] = 0x89uy
             && bytes[1] = 0x50uy
             && bytes[2] = 0x4Euy
             && bytes[3] = 0x47uy
             && bytes[4] = 0x0Duy
             && bytes[5] = 0x0Auy
             && bytes[6] = 0x1Auy
             && bytes[7] = 0x0Auy then
            Some("image/png", ".png")
        elif bytes.Length >= 12
             && bytes[0] = 0x52uy
             && bytes[1] = 0x49uy
             && bytes[2] = 0x46uy
             && bytes[3] = 0x46uy
             && bytes[8] = 0x57uy
             && bytes[9] = 0x45uy
             && bytes[10] = 0x42uy
             && bytes[11] = 0x50uy then
            Some("image/webp", ".webp")
        else
            None

    let private readCover (track: Track) =
        try
            let picture =
                track.EmbeddedPictures
                |> Seq.tryFind (fun picture -> picture.PicType = PictureInfo.PIC_TYPE.Front)
                |> Option.orElseWith (fun () -> track.EmbeddedPictures |> Seq.tryHead)

            match picture with
            | None -> None
            | Some picture ->
                let bytes = picture.PictureData

                if isNull bytes || bytes.Length = 0 || bytes.Length > MaxCoverBytes then
                    None
                else
                    match sniffCover bytes with
                    | None -> None
                    | Some(contentType, extension) ->
                        Some
                            { Bytes = bytes
                              ContentType = contentType
                              Extension = extension
                              Sha256 = sha256Hex bytes }
        with _ ->
            None

    let private durationMilliseconds (track: Track) =
        let duration = float track.Duration

        if Double.IsNaN duration || Double.IsInfinity duration || duration < 0.0 then
            None
        else
            let milliseconds = duration * 1000.0

            if Double.IsNaN milliseconds || Double.IsInfinity milliseconds || milliseconds > float Int32.MaxValue then
                None
            else
                try
                    Some(Convert.ToInt32(Math.Round(milliseconds, MidpointRounding.AwayFromZero)))
                with :? OverflowException ->
                    None

    let read (path: string) : Result<ExtractedTrackMetadata, TrackMetadataError> =
        if String.IsNullOrWhiteSpace path then
            Error(TrackMetadataError.InvalidPath "The media path is empty.")
        else
            try
                if not (File.Exists path) then
                    Error(TrackMetadataError.FileNotFound path)
                else
                    try
                        let track = Track(path)

                        Ok
                            { Title = normalizeText track.Title
                              Artist = normalizeText track.Artist
                              Album = normalizeText track.Album
                              DurationMs = durationMilliseconds track
                              Cover = readCover track }
                    with ex ->
                        Error(TrackMetadataError.ParseFailed ex.Message)
            with
            | :? ArgumentException as ex -> Error(TrackMetadataError.InvalidPath ex.Message)
            | :? IOException as ex -> Error(TrackMetadataError.ParseFailed ex.Message)
            | ex -> Error(TrackMetadataError.ParseFailed ex.Message)
