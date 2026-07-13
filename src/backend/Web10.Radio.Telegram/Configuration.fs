namespace Web10.Radio.Telegram

open System
open System.Globalization
open System.Net
open System.Text.RegularExpressions
open Microsoft.Extensions.Configuration
open Npgsql
open Web10.Radio.Database
type OtelOptions =
    { ExporterOtlpEndpoint: Uri }

[<RequireQualifiedAccess>]
type TelegramServiceOptions =
    { Postgres: PostgresOptions
      Telegram: TelegramOptions
      Otel: OtelOptions option }

[<RequireQualifiedAccess>]
module TelegramConfiguration =
    let private telegramTokenPattern = Regex("^[1-9][0-9]{5,19}:[A-Za-z0-9_-]{20,}$", RegexOptions.CultureInvariant)
    let private telegramUsernamePattern = Regex("^@[A-Za-z][A-Za-z0-9_]{4,31}$", RegexOptions.CultureInvariant)
    let private telegramChannelIdPattern = Regex("^-100[1-9][0-9]{5,15}$", RegexOptions.CultureInvariant)
    let private webhookSecretPattern = Regex("^[A-Za-z0-9_-]{16,256}$", RegexOptions.CultureInvariant)

    let private requiredKeys =
        [ "POSTGRES:CONNECTION_STRING", "WEB10_POSTGRES__CONNECTION_STRING"
          "TELEGRAM:BOT_TOKEN", "WEB10_TELEGRAM__BOT_TOKEN"
          "TELEGRAM:WEBHOOK_SECRET", "WEB10_TELEGRAM__WEBHOOK_SECRET"
          "TELEGRAM:CHANNEL_ID_OR_USERNAME", "WEB10_TELEGRAM__CHANNEL_ID_OR_USERNAME"
          "TELEGRAM:REQUEST_PRICE_STARS", "WEB10_TELEGRAM__REQUEST_PRICE_STARS"
          "TELEGRAM:SAY_PRICE_STARS", "WEB10_TELEGRAM__SAY_PRICE_STARS"
          ]

    let private readOptionalExact (configuration: IConfiguration) key =
        let value = configuration[key]
        if isNull value then None else Some value

    let private readRequired (configuration: IConfiguration) (errors: ResizeArray<string>) (key, envVar) =
        match configuration[key] with
        | value when String.IsNullOrWhiteSpace value ->
            errors.Add(sprintf "%s is required and must be non-empty." envVar)
            None
        | value -> Some(value.Trim())

    let private parsePositiveInt32 (errors: ResizeArray<string>) (envVar: string) (value: string option) =
        match value with
        | Some raw ->
            match Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, parsed when parsed > 0 -> Some parsed
            | _ ->
                errors.Add(sprintf "%s must be a positive 32-bit integer." envVar)
                None
        | None ->
            errors.Add(sprintf "%s must be a positive 32-bit integer." envVar)
            None

    let private parseAbsoluteHttpUri (errors: ResizeArray<string>) (envVar: string) (value: string option) =
        match value with
        | None -> None
        | Some raw ->
            match Uri.TryCreate(raw, UriKind.Absolute) with
            | true, uri when uri.Scheme = "http" || uri.Scheme = "https" -> Some uri
            | true, _ ->
                errors.Add(sprintf "%s must use one of these URI schemes: http, https." envVar)
                None
            | false, _ ->
                errors.Add(sprintf "%s must be an absolute URI." envVar)
                None

    let private hasEnoughVariation (value: string) =
        value |> Seq.distinct |> Seq.truncate 4 |> Seq.length = 4

    let private validateConnectionString (errors: ResizeArray<string>) (value: string option) =
        match value with
        | Some connectionString ->
            try
                let builder = NpgsqlConnectionStringBuilder(connectionString: string)
                if String.IsNullOrWhiteSpace builder.Host
                   || String.IsNullOrWhiteSpace builder.Database
                   || String.IsNullOrWhiteSpace builder.Username then
                    errors.Add("WEB10_POSTGRES__CONNECTION_STRING must specify non-empty Host, Database, and Username values.")
            with
            | :? ArgumentException
            | :? FormatException ->
                errors.Add("WEB10_POSTGRES__CONNECTION_STRING must be a valid Npgsql connection string.")
        | None -> ()

    let private validateTelegram (errors: ResizeArray<string>) (token: string option) (secret: string option) (channel: string option) =
        match token with
        | Some value when not (telegramTokenPattern.IsMatch(value)) ->
            errors.Add("WEB10_TELEGRAM__BOT_TOKEN must have Telegram bot token syntax (numeric bot id, colon, and token).")
        | _ -> ()
        match secret with
        | Some value when not (webhookSecretPattern.IsMatch(value)) || not (hasEnoughVariation value) ->
            errors.Add("WEB10_TELEGRAM__WEBHOOK_SECRET must be a nontrivial 16-256 character Telegram secret token using letters, digits, underscore, or hyphen.")
        | _ -> ()
        match channel with
        | Some value when not (telegramUsernamePattern.IsMatch(value) || telegramChannelIdPattern.IsMatch(value)) ->
            errors.Add("WEB10_TELEGRAM__CHANNEL_ID_OR_USERNAME must be an @channel_username or a -100-prefixed Telegram channel id.")
        | _ -> ()

    let private parseUpdateMode (errors: ResizeArray<string>) (value: string option) =
        match value with
        | None -> Webhook
        | Some "Webhook" -> Webhook
        | Some "LongPolling" -> LongPolling
        | Some _ ->
            errors.Add("WEB10_TELEGRAM__UPDATE_MODE must be exactly Webhook or LongPolling.")
            Webhook

    let load (configuration: IConfiguration) : Result<TelegramServiceOptions, string list> =
        let errors = ResizeArray<string>()
        let values =
            requiredKeys
            |> List.map (fun ((key, _) as requiredKey) -> key, readRequired configuration errors requiredKey)
            |> Map.ofList
        let required key = values[key]
        let optional key = readOptionalExact configuration key
        let connectionString = required "POSTGRES:CONNECTION_STRING"
        let botToken = required "TELEGRAM:BOT_TOKEN"
        let webhookSecret = required "TELEGRAM:WEBHOOK_SECRET"
        let channel = required "TELEGRAM:CHANNEL_ID_OR_USERNAME"
        let requestPrice = parsePositiveInt32 errors "WEB10_TELEGRAM__REQUEST_PRICE_STARS" (required "TELEGRAM:REQUEST_PRICE_STARS")
        let sayPrice = parsePositiveInt32 errors "WEB10_TELEGRAM__SAY_PRICE_STARS" (required "TELEGRAM:SAY_PRICE_STARS")
        let updateMode = parseUpdateMode errors (optional "TELEGRAM:UPDATE_MODE")
        let otelEnabled =
            match optional "OTEL:ENABLED" with
            | None -> true
            | value ->
                match value with
                | Some raw when String.IsNullOrWhiteSpace raw ->
                    errors.Add("WEB10_OTEL__ENABLED must be exactly true or false.")
                    false
                | _ ->
                    match value with
                    | Some "true" -> true
                    | Some "false" -> false
                    | _ ->
                        errors.Add("WEB10_OTEL__ENABLED must be exactly true or false.")
                        false
        let endpoint =
            if otelEnabled then
                match optional "OTEL:EXPORTER_OTLP_ENDPOINT" with
                | Some raw when not (String.IsNullOrWhiteSpace raw) ->
                    parseAbsoluteHttpUri errors "WEB10_OTEL__EXPORTER_OTLP_ENDPOINT" (Some(raw.Trim()))
                | _ ->
                    errors.Add("WEB10_OTEL__EXPORTER_OTLP_ENDPOINT is required when WEB10_OTEL__ENABLED=true.")
                    None
            else
                None
        validateConnectionString errors connectionString
        validateTelegram errors botToken webhookSecret channel

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            let get key = required key |> Option.get
            Ok
                { Postgres = { ConnectionString = get "POSTGRES:CONNECTION_STRING" }
                  Telegram =
                    { BotToken = get "TELEGRAM:BOT_TOKEN"
                      WebhookSecret = get "TELEGRAM:WEBHOOK_SECRET"
                      ChannelIdOrUsername = get "TELEGRAM:CHANNEL_ID_OR_USERNAME"
                      RequestPriceStars = Option.get requestPrice
                      UpdateMode = updateMode
                      SayPriceStars = Option.get sayPrice }
                  Otel = Option.map (fun endpoint -> { ExporterOtlpEndpoint = endpoint }) endpoint }

module Configuration =
    let load (configuration: IConfiguration) = TelegramConfiguration.load configuration
