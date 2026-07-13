namespace Web10.Radio.API

open System
open System.IO
open System.Net
open System.Globalization
open System.Text.RegularExpressions
open Microsoft.Extensions.Configuration
open Npgsql
open Web10.Radio.Database

type StreamOptions =
    { RtmpUrl: Uri
      RtmpKey: string
      StageUrl: Uri
      CallbackToken: string }

type StorageType =
    | Local
    | S3

type StorageOptions =
    { Type: StorageType
      LocalRoot: string
      CacheRoot: string
      S3Bucket: string
      S3Region: string
      S3ServiceUrl: Uri option
      S3ForcePathStyle: bool
      MaxUploadBytes: int64 }

type AdminOptions =
    { Username: string
      Password: string }

type OtelOptions =
    { ExporterOtlpEndpoint: Uri }

type DataProtectionOptions =
    { KeyRingPath: string }

type Web10Options =
    { Postgres: PostgresOptions
      Stream: StreamOptions
      Storage: StorageOptions
      Admin: AdminOptions
      Otel: OtelOptions
      DevelopmentFixturesEnabled: bool
      DataProtection: DataProtectionOptions }

[<RequireQualifiedAccess>]
module Configuration =
    let private requiredKeys =
        [ "POSTGRES:CONNECTION_STRING", "WEB10_POSTGRES__CONNECTION_STRING"
          "STREAM:RTMP_URL", "WEB10_STREAM__RTMP_URL"
          "STREAM:RTMP_KEY", "WEB10_STREAM__RTMP_KEY"
          "STREAM:STAGE_URL", "WEB10_STREAM__STAGE_URL"
          "STREAM:CALLBACK_TOKEN", "WEB10_STREAM__CALLBACK_TOKEN"
          "STORAGE:TYPE", "WEB10_STORAGE__TYPE"
          "STORAGE:CACHE_ROOT", "WEB10_STORAGE__CACHE_ROOT"
          "ADMIN:USERNAME", "WEB10_ADMIN__USERNAME"
          "ADMIN:PASSWORD", "WEB10_ADMIN__PASSWORD"
          "OTEL:EXPORTER_OTLP_ENDPOINT", "WEB10_OTEL__EXPORTER_OTLP_ENDPOINT"
          "DATA_PROTECTION:KEY_RING_PATH", "WEB10_DATA_PROTECTION__KEY_RING_PATH" ]

    let private readRequired (configuration: IConfiguration) (errors: ResizeArray<string>) (key: string, envVar: string) =
        let value = configuration[key]

        if String.IsNullOrWhiteSpace value then
            errors.Add(sprintf "%s is required and must be non-empty." envVar)
            None
        else
            Some(value.Trim())

    let private readOptional (configuration: IConfiguration) key =
        let value = configuration[key]
        if String.IsNullOrWhiteSpace value then None else Some(value.Trim())
    let private readOptionalExact (configuration: IConfiguration) key =
        let value = configuration[key]
        if isNull value then None else Some value

    let private readRequiredUntrimmed (configuration: IConfiguration) (errors: ResizeArray<string>) (key: string, envVar: string) =
        let value = configuration[key]

        if String.IsNullOrWhiteSpace value then
            errors.Add(sprintf "%s is required and must be non-empty." envVar)
            None
        else
            Some value

    let private parseAbsoluteUri (errors: ResizeArray<string>) envVar allowedSchemes (value: string option) =
        match value with
        | None -> None
        | Some raw ->
            match Uri.TryCreate(raw, UriKind.Absolute) with
            | true, uri when Set.contains uri.Scheme allowedSchemes -> Some uri
            | true, _ ->
                errors.Add(sprintf "%s must use one of these URI schemes: %s." envVar (String.concat ", " allowedSchemes))
                None
            | false, _ ->
                errors.Add(sprintf "%s must be an absolute URI." envVar)
                None

    let private parseStorageType (errors: ResizeArray<string>) (value: string option) =
        match value with
        | None -> None
        | Some "Local" -> Some Local
        | Some "S3" -> Some S3
        | Some _ ->
            errors.Add("WEB10_STORAGE__TYPE must be exactly Local or S3.")
            None



    let private parseBoolean (errors: ResizeArray<string>) envVar (value: string option) =
        match value with
        | None -> false
        | Some "true" -> true
        | Some "false" -> false
        | Some _ ->
            errors.Add(sprintf "%s must be exactly true or false." envVar)
            false
    let private parseMaxUploadBytes (errors: ResizeArray<string>) (value: string option) =
        match value with
        | None -> 536870912L
        | Some raw ->
            match Int64.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, parsed when parsed > 0L -> parsed
            | _ ->
                errors.Add("WEB10_STORAGE__MAX_UPLOAD_BYTES must be a positive Int64.")
                536870912L



    let private validateConnectionString (errors: ResizeArray<string>) (value: string option) =
        match value with
        | None -> ()
        | Some connectionString ->
            try
                let builder = NpgsqlConnectionStringBuilder(connectionString)

                if String.IsNullOrWhiteSpace builder.Host
                   || String.IsNullOrWhiteSpace builder.Database
                   || String.IsNullOrWhiteSpace builder.Username then
                    errors.Add("WEB10_POSTGRES__CONNECTION_STRING must specify non-empty Host, Database, and Username values.")
            with
            | :? ArgumentException
            | :? FormatException -> errors.Add("WEB10_POSTGRES__CONNECTION_STRING must be a valid Npgsql connection string.")

    let private hasEnoughVariation (value: string) =
        value |> Seq.distinct |> Seq.truncate 4 |> Seq.length = 4

    let private validateSecret (errors: ResizeArray<string>) envVar minimumLength (value: string option) =
        match value with
        | None -> ()
        | Some secret when secret.Length < minimumLength || not (hasEnoughVariation secret) || Seq.exists Char.IsWhiteSpace secret ->
            errors.Add(sprintf "%s must be a nontrivial, whitespace-free secret of at least %d characters." envVar minimumLength)
        | Some _ -> ()

    let private bearerSecretPattern = Regex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)

    let private validateBearerSecret (errors: ResizeArray<string>) envVar minimumLength (value: string option) =
        match value with
        | None -> ()
        | Some secret when secret.Length < minimumLength || not (hasEnoughVariation secret) || not (bearerSecretPattern.IsMatch secret) ->
            errors.Add(
                sprintf
                    "%s must be a nontrivial bearer-safe secret of at least %d characters using letters, digits, underscore, or hyphen."
                    envVar
                    minimumLength
            )
        | Some _ -> ()
    let private validateAdminUsername (errors: ResizeArray<string>) (username: string option) =
        match username with
        | Some value when value.Length <= 64 -> ()
        | _ -> errors.Add("WEB10_ADMIN__USERNAME must be 1 to 64 characters after trimming.")

    let private validateAdminPassword (errors: ResizeArray<string>) (password: string option) =
        match password with
        | Some value when value.Length >= 12 && value.Length <= 256 -> ()
        | _ -> errors.Add("WEB10_ADMIN__PASSWORD must be 12 to 256 characters and is not trimmed.")


    let private s3BucketPattern = Regex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)
    let private s3RegionPattern = Regex("^[a-z0-9][a-z0-9-]{0,62}[a-z0-9]$", RegexOptions.CultureInvariant)

    let private isIpAddress (value: string) =
        match IPAddress.TryParse value with
        | true, _ -> true
        | false, _ -> false

    let private isValidS3Bucket (value: string) =
        s3BucketPattern.IsMatch value
        && not (value.Contains("..", StringComparison.Ordinal))
        && not (isIpAddress value)
        && not (value.StartsWith("xn--", StringComparison.Ordinal))
        && not (value.StartsWith("sthree-", StringComparison.Ordinal))
        && not (value.StartsWith("amzn-s3-demo-", StringComparison.Ordinal))
        && not (value.EndsWith("-s3alias", StringComparison.Ordinal))
        && not (value.EndsWith("--ol-s3", StringComparison.Ordinal))
        && not (value.EndsWith(".mrap", StringComparison.Ordinal))
        && not (value.EndsWith("--x-s3", StringComparison.Ordinal))
        && not (value.EndsWith("--table-s3", StringComparison.Ordinal))

    let private validateWritableDirectory (errors: ResizeArray<string>) envVar (path: string option) =
        match path with
        | None -> ()
        | Some rawPath ->
            let mutable probePath = null

            try
                try
                    let fullPath = Path.GetFullPath rawPath
                    Directory.CreateDirectory fullPath |> ignore
                    probePath <- Path.Combine(fullPath, sprintf ".web10-write-probe-%s.tmp" (Guid.NewGuid().ToString("N")))

                    use stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough)
                    stream.WriteByte 0uy
                    stream.Flush true
                with
                | :? ArgumentException
                | :? NotSupportedException
                | :? IOException
                | :? UnauthorizedAccessException ->
                    errors.Add(sprintf "%s must identify a creatable, writable directory." envVar)
            finally
                if not (isNull probePath) then
                    try
                        File.Delete probePath
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException -> ()

    let private validateStorage
        (errors: ResizeArray<string>)
        (storageType: StorageType option)
        (localRoot: string option)
        (cacheRoot: string option)
        (s3Bucket: string option)
        (s3Region: string option)
        (s3ServiceUrl: string option)
        (forcePathStyleValue: string option)
        =
        match cacheRoot with
        | None -> ()
        | Some _ -> validateWritableDirectory errors "WEB10_STORAGE__CACHE_ROOT" cacheRoot

        match storageType with
        | None -> ()
        | Some Local ->
            if Option.isNone localRoot then
                errors.Add("WEB10_STORAGE__LOCAL_ROOT is required when WEB10_STORAGE__TYPE=Local.")

            if Option.isSome s3Bucket
               || Option.isSome s3Region
               || Option.isSome s3ServiceUrl
               || forcePathStyleValue = Some "true" then
                errors.Add("S3 bucket, region, service URL, and true force-path-style settings must be unset when WEB10_STORAGE__TYPE=Local.")

            validateWritableDirectory errors "WEB10_STORAGE__LOCAL_ROOT" localRoot
        | Some S3 ->
            if Option.isSome localRoot then
                errors.Add("WEB10_STORAGE__LOCAL_ROOT must be unset when WEB10_STORAGE__TYPE=S3.")

            match s3Bucket with
            | None -> errors.Add("WEB10_STORAGE__S3_BUCKET is required when WEB10_STORAGE__TYPE=S3.")
            | Some bucket when not (isValidS3Bucket bucket) -> errors.Add("WEB10_STORAGE__S3_BUCKET must be a valid S3 general-purpose bucket name.")
            | Some _ -> ()

            match s3Region with
            | None -> errors.Add("WEB10_STORAGE__S3_REGION is required when WEB10_STORAGE__TYPE=S3.")
            | Some region when not (s3RegionPattern.IsMatch region) -> errors.Add("WEB10_STORAGE__S3_REGION must be a valid lowercase AWS signing region name.")
            | Some _ -> ()

    let load (configuration: IConfiguration) : Result<Web10Options, string list> =
        let errors = ResizeArray<string>()
        let values =
            requiredKeys
            |> List.map (fun ((key, _) as requiredKey) ->
                let value =
                    if key = "ADMIN:PASSWORD" then
                        readRequiredUntrimmed configuration errors requiredKey
                    else
                        readRequired configuration errors requiredKey

                key, value)
            |> Map.ofList

        let requiredValue key = values[key]
        let connectionString = requiredValue "POSTGRES:CONNECTION_STRING"
        let maxUploadBytes = parseMaxUploadBytes errors (readOptional configuration "STORAGE:MAX_UPLOAD_BYTES")
        let optionalValue key = readOptional configuration key
        let rtmpKey = requiredValue "STREAM:RTMP_KEY"
        let streamCallbackToken = requiredValue "STREAM:CALLBACK_TOKEN"
        let adminUsername = requiredValue "ADMIN:USERNAME"
        let adminPassword = requiredValue "ADMIN:PASSWORD"
        let localRoot = optionalValue "STORAGE:LOCAL_ROOT"
        let cacheRoot = requiredValue "STORAGE:CACHE_ROOT"
        let s3Bucket = optionalValue "STORAGE:S3_BUCKET"
        let s3Region = optionalValue "STORAGE:S3_REGION"
        let s3ServiceUrlRaw = optionalValue "STORAGE:S3_SERVICE_URL"
        let forcePathStyleRaw = optionalValue "STORAGE:S3_FORCE_PATH_STYLE"
        let forcePathStyle = parseBoolean errors "WEB10_STORAGE__S3_FORCE_PATH_STYLE" forcePathStyleRaw
        let storageType = parseStorageType errors (requiredValue "STORAGE:TYPE")
        let rtmpUrl = parseAbsoluteUri errors "WEB10_STREAM__RTMP_URL" (Set.ofList [ "rtmp"; "rtmps" ]) (requiredValue "STREAM:RTMP_URL")
        let stageUrl = parseAbsoluteUri errors "WEB10_STREAM__STAGE_URL" (Set.ofList [ "http"; "https" ]) (requiredValue "STREAM:STAGE_URL")
        let otlpEndpoint = parseAbsoluteUri errors "WEB10_OTEL__EXPORTER_OTLP_ENDPOINT" (Set.ofList [ "http"; "https" ]) (requiredValue "OTEL:EXPORTER_OTLP_ENDPOINT")
        let developmentFixturesEnabled = parseBoolean errors "WEB10_DEV__FIXTURES_ENABLED" (readOptionalExact configuration "DEV:FIXTURES_ENABLED")
        let s3ServiceUrl = parseAbsoluteUri errors "WEB10_STORAGE__S3_SERVICE_URL" (Set.ofList [ "http"; "https" ]) s3ServiceUrlRaw
        validateConnectionString errors connectionString
        validateSecret errors "WEB10_STREAM__RTMP_KEY" 16 rtmpKey
        validateBearerSecret errors "WEB10_STREAM__CALLBACK_TOKEN" 24 streamCallbackToken
        validateAdminUsername errors adminUsername
        validateAdminPassword errors adminPassword
        validateStorage errors storageType localRoot cacheRoot s3Bucket s3Region s3ServiceUrlRaw forcePathStyleRaw
        validateWritableDirectory errors "WEB10_DATA_PROTECTION__KEY_RING_PATH" (requiredValue "DATA_PROTECTION:KEY_RING_PATH")

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            let getRequired key = requiredValue key |> Option.get

            Ok
                { Postgres = { ConnectionString = getRequired "POSTGRES:CONNECTION_STRING" }
                  Stream =
                    { RtmpUrl = Option.get rtmpUrl
                      RtmpKey = getRequired "STREAM:RTMP_KEY"
                      StageUrl = Option.get stageUrl
                      CallbackToken = getRequired "STREAM:CALLBACK_TOKEN" }
                  Storage =
                    { Type = Option.get storageType
                      LocalRoot = Option.defaultValue "" localRoot
                      CacheRoot = Path.GetFullPath(getRequired "STORAGE:CACHE_ROOT")
                      S3Bucket = Option.defaultValue "" s3Bucket
                      S3Region = Option.defaultValue "" s3Region
                      S3ServiceUrl = s3ServiceUrl
                      S3ForcePathStyle = forcePathStyle
                      MaxUploadBytes = maxUploadBytes }
                  Admin =
                    { Username = getRequired "ADMIN:USERNAME"
                      Password = getRequired "ADMIN:PASSWORD" }
                  Otel = { ExporterOtlpEndpoint = Option.get otlpEndpoint }
                  DataProtection = { KeyRingPath = getRequired "DATA_PROTECTION:KEY_RING_PATH" }
                  DevelopmentFixturesEnabled = developmentFixturesEnabled }

