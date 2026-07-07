namespace Web10.Radio.API

open System
open Microsoft.Extensions.Configuration
open Web10.Radio.Database
open Web10.Radio.Telegram

type StreamOptions =
    { RtmpUrl: Uri
      RtmpKey: string
      StageUrl: Uri }

type StorageType =
    | Local
    | S3

type StorageOptions =
    { Type: StorageType
      LocalRoot: string
      S3Bucket: string }

type OtelOptions =
    { ExporterOtlpEndpoint: Uri }

type DataProtectionOptions =
    { KeyRingPath: string }

type Web10Options =
    { Postgres: PostgresOptions
      Telegram: TelegramOptions
      Stream: StreamOptions
      Storage: StorageOptions
      Otel: OtelOptions
      DataProtection: DataProtectionOptions }

module Configuration =
    let private requiredKeys =
        [ "POSTGRES:CONNECTION_STRING", "WEB10_POSTGRES__CONNECTION_STRING"
          "TELEGRAM:BOT_TOKEN", "WEB10_TELEGRAM__BOT_TOKEN"
          "TELEGRAM:WEBHOOK_SECRET", "WEB10_TELEGRAM__WEBHOOK_SECRET"
          "TELEGRAM:CHANNEL_ID_OR_USERNAME", "WEB10_TELEGRAM__CHANNEL_ID_OR_USERNAME"
          "STREAM:RTMP_URL", "WEB10_STREAM__RTMP_URL"
          "STREAM:RTMP_KEY", "WEB10_STREAM__RTMP_KEY"
          "STREAM:STAGE_URL", "WEB10_STREAM__STAGE_URL"
          "STORAGE:TYPE", "WEB10_STORAGE__TYPE"
          "STORAGE:LOCAL_ROOT", "WEB10_STORAGE__LOCAL_ROOT"
          "STORAGE:S3_BUCKET", "WEB10_STORAGE__S3_BUCKET"
          "OTEL:EXPORTER_OTLP_ENDPOINT", "WEB10_OTEL__EXPORTER_OTLP_ENDPOINT"
          "DATA_PROTECTION:KEY_RING_PATH", "WEB10_DATA_PROTECTION__KEY_RING_PATH" ]

    let private readRequired (configuration: IConfiguration) (errors: ResizeArray<string>) (key: string, envVar: string) =
        let value = configuration[key]

        if String.IsNullOrWhiteSpace value then
            errors.Add(sprintf "%s is required and must be non-empty." envVar)
            None
        else
            Some value

    let private parseAbsoluteUri (errors: ResizeArray<string>) (envVar: string) (value: string option) =
        match value with
        | None -> None
        | Some raw ->
            match Uri.TryCreate(raw, UriKind.Absolute) with
            | true, uri -> Some uri
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

    let load (configuration: IConfiguration) : Result<Web10Options, string list> =
        let errors = ResizeArray<string>()
        let values =
            requiredKeys
            |> List.map (fun ((key, envVar) as requiredKey) -> key, readRequired configuration errors requiredKey)
            |> Map.ofList

        let value key = values[key] |> Option.get
        let optionalValue key = values[key]

        let rtmpUrl = parseAbsoluteUri errors "WEB10_STREAM__RTMP_URL" (optionalValue "STREAM:RTMP_URL")
        let stageUrl = parseAbsoluteUri errors "WEB10_STREAM__STAGE_URL" (optionalValue "STREAM:STAGE_URL")
        let otlpEndpoint = parseAbsoluteUri errors "WEB10_OTEL__EXPORTER_OTLP_ENDPOINT" (optionalValue "OTEL:EXPORTER_OTLP_ENDPOINT")
        let storageType = parseStorageType errors (optionalValue "STORAGE:TYPE")

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            Ok
                { Postgres = { ConnectionString = value "POSTGRES:CONNECTION_STRING" }
                  Telegram =
                    { BotToken = value "TELEGRAM:BOT_TOKEN"
                      WebhookSecret = value "TELEGRAM:WEBHOOK_SECRET"
                      ChannelIdOrUsername = value "TELEGRAM:CHANNEL_ID_OR_USERNAME" }
                  Stream =
                    { RtmpUrl = Option.get rtmpUrl
                      RtmpKey = value "STREAM:RTMP_KEY"
                      StageUrl = Option.get stageUrl }
                  Storage =
                    { Type = Option.get storageType
                      LocalRoot = value "STORAGE:LOCAL_ROOT"
                      S3Bucket = value "STORAGE:S3_BUCKET" }
                  Otel = { ExporterOtlpEndpoint = Option.get otlpEndpoint }
                  DataProtection = { KeyRingPath = value "DATA_PROTECTION:KEY_RING_PATH" } }
