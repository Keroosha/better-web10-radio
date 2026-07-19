namespace Web10.Radio.StreamNode

open Web10.Radio.StreamNode.ResultWorkflow
open System
open System.Collections.Generic
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type ConfigurationError =
    | Missing of key: string
    | Invalid of key: string

module Configuration =
    let private positiveDecimal = Regex("^[1-9][0-9]*$", RegexOptions.CultureInvariant)
    let private bearerToken = Regex("^[A-Za-z0-9._~+/-]+={0,2}$", RegexOptions.CultureInvariant)
    let private display = Regex("^:([0-9]+)$", RegexOptions.CultureInvariant)

    let private getValue (environment: IReadOnlyDictionary<string, string>) key =
        match environment.TryGetValue key with
        | true, value when not (isNull value) && value.Length > 0 && value = value.Trim() -> Some value
        | _ -> None

    let private required environment key =
        match getValue environment key with
        | Some value -> Ok value
        | None -> Error(ConfigurationError.Missing key)

    let private optional (environment: IReadOnlyDictionary<string, string>) key defaultValue =
        match environment.TryGetValue key with
        | false, _ -> Ok defaultValue
        | true, value when not (isNull value) && value.Length > 0 && value = value.Trim() -> Ok value
        | _ -> Error(ConfigurationError.Invalid key)

    let private positiveInt (environment: IReadOnlyDictionary<string, string>) key defaultValue =
        match environment.TryGetValue key with
        | false, _ -> Ok defaultValue
        | true, value when not (isNull value) && positiveDecimal.IsMatch value ->
            match Int32.TryParse value with
            | true, parsed when parsed > 0 -> Ok parsed
            | _ -> Error(ConfigurationError.Invalid key)
        | _ -> Error(ConfigurationError.Invalid key)

    let private graphicsBackend environment =
        match optional environment "WEB10_STREAM__GRAPHICS_BACKEND" "swiftshader" with
        | Ok "swiftshader" -> Ok GraphicsBackend.SwiftShader
        | Ok "vulkan" -> Ok GraphicsBackend.Vulkan
        | Ok _
        | Error _ -> Error(ConfigurationError.Invalid "WEB10_STREAM__GRAPHICS_BACKEND")

    let private videoPreset environment =
        match optional environment "WEB10_STREAM__VIDEO_PRESET" "veryfast" with
        | Ok "ultrafast" -> Ok VideoPreset.UltraFast
        | Ok "superfast" -> Ok VideoPreset.SuperFast
        | Ok "veryfast" -> Ok VideoPreset.VeryFast
        | Ok "faster" -> Ok VideoPreset.Faster
        | Ok "fast" -> Ok VideoPreset.Fast
        | Ok "medium" -> Ok VideoPreset.Medium
        | Ok "slow" -> Ok VideoPreset.Slow
        | Ok "slower" -> Ok VideoPreset.Slower
        | Ok "veryslow" -> Ok VideoPreset.VerySlow
        | Ok "placebo" -> Ok VideoPreset.Placebo
        | Ok _
        | Error _ -> Error(ConfigurationError.Invalid "WEB10_STREAM__VIDEO_PRESET")

    let private validateHttp key (allowQuery: bool) value =
        try
            let parsed = Uri(value, UriKind.Absolute)
            if parsed.Scheme <> Uri.UriSchemeHttp && parsed.Scheme <> Uri.UriSchemeHttps then Error(ConfigurationError.Invalid key)
            elif String.IsNullOrWhiteSpace parsed.Host then Error(ConfigurationError.Invalid key)
            elif not (isNull parsed.UserInfo) && parsed.UserInfo.Length > 0 then Error(ConfigurationError.Invalid key)
            elif (not allowQuery) && (not (String.IsNullOrEmpty parsed.Query) || not (String.IsNullOrEmpty parsed.Fragment)) then Error(ConfigurationError.Invalid key)
            elif value |> Seq.exists Char.IsWhiteSpace then Error(ConfigurationError.Invalid key)
            else Ok value
        with :? UriFormatException -> Error(ConfigurationError.Invalid key)

    let private validateRtmp key value =
        try
            let parsed = Uri(value, UriKind.Absolute)
            if parsed.Scheme <> "rtmp" && parsed.Scheme <> "rtmps" then Error(ConfigurationError.Invalid key)
            elif String.IsNullOrWhiteSpace parsed.Host then Error(ConfigurationError.Invalid key)
            elif not (String.IsNullOrEmpty parsed.UserInfo) then Error(ConfigurationError.Invalid key)
            elif value |> Seq.exists Char.IsWhiteSpace then Error(ConfigurationError.Invalid key)
            else Ok value
        with :? UriFormatException -> Error(ConfigurationError.Invalid key)

    let private bind2 f a b = Result.bind (fun x -> Result.map (f x) b) a
    let private bind3 f a b c = Result.bind (fun x -> Result.bind (fun y -> Result.map (f x y) c) b) a

    let validate (environment: IReadOnlyDictionary<string, string>) : Result<RuntimeConfig, ConfigurationError> =
        result {
            let! api = required environment "WEB10_API__BASE_URL"
            let! callbackToken = required environment "WEB10_STREAM__CALLBACK_TOKEN"
            let! stage = required environment "WEB10_STREAM__STAGE_URL"
            let! rtmp = required environment "WEB10_STREAM__RTMP_URL"
            let! rtmpKey = required environment "WEB10_STREAM__RTMP_KEY"
            let! api = validateHttp "WEB10_API__BASE_URL" false api
            let! stage = validateHttp "WEB10_STREAM__STAGE_URL" true stage
            let! rtmp = validateRtmp "WEB10_STREAM__RTMP_URL" rtmp
            do!
                if callbackToken.Length >= 24 && bearerToken.IsMatch callbackToken then Ok()
                else Error(ConfigurationError.Invalid "WEB10_STREAM__CALLBACK_TOKEN")
            do!
                if rtmpKey.Length >= 16 && not (rtmpKey |> Seq.exists Char.IsWhiteSpace) then Ok()
                else Error(ConfigurationError.Invalid "WEB10_STREAM__RTMP_KEY")
            let! displayValue = optional environment "WEB10_STREAM__DISPLAY" ":99"
            do!
                if display.IsMatch displayValue then Ok()
                else Error(ConfigurationError.Invalid "WEB10_STREAM__DISPLAY")
            let! graphicsBackend = graphicsBackend environment
            let! width = positiveInt environment "WEB10_STREAM__WIDTH" 1280
            let! height = positiveInt environment "WEB10_STREAM__HEIGHT" 720
            let! framerate = positiveInt environment "WEB10_STREAM__FRAMERATE" 30
            let! bitrate = positiveInt environment "WEB10_STREAM__BITRATE_KBPS" 192
            let! videoBitrate = positiveInt environment "WEB10_STREAM__VIDEO_BITRATE_KBPS" 2500
            let! videoPreset = videoPreset environment
            let! storageRoot = optional environment "WEB10_STORAGE__ROOT" "/var/lib/web10/storage"
            let! cacheRoot = optional environment "WEB10_STORAGE__CACHE_ROOT" "/var/lib/web10/cache"
            let! callbackPort = positiveInt environment "WEB10_STREAM__CALLBACK_PORT" 18080
            do!
                if callbackPort <= 65535 then Ok()
                else Error(ConfigurationError.Invalid "WEB10_STREAM__CALLBACK_PORT")
            let! socket = optional environment "WEB10_STREAM__LIQUIDSOAP_SOCKET" "/run/web10/liquidsoap.sock"
            return
                { ApiBaseUrl = api.TrimEnd('/'); CallbackToken = callbackToken; StageUrl = stage
                  RtmpUrl = rtmp; RtmpKey = rtmpKey; Display = displayValue
                  GraphicsBackend = graphicsBackend; Width = width; Height = height
                  Framerate = framerate; BitrateKbps = bitrate
                  VideoBitrateKbps = videoBitrate; VideoPreset = videoPreset; StorageRoot = storageRoot
                  CacheRoot = cacheRoot; CallbackPort = callbackPort; LiquidsoapSocket = socket }
        }

    let fromEnvironment () =
        let values = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        for item in Environment.GetEnvironmentVariables() do
            let pair = item :?> System.Collections.DictionaryEntry
            values[string pair.Key] <- string pair.Value
        validate values

    let errorKey = function
        | ConfigurationError.Missing key
        | ConfigurationError.Invalid key -> key

    let describe = function
        | ConfigurationError.Missing key -> sprintf "missing configuration: %s" key
        | ConfigurationError.Invalid key -> sprintf "invalid configuration: %s" key

module ConfigLoader =
    let load environment = Configuration.validate environment
    let loadEnvironment () = Configuration.fromEnvironment ()
