namespace Web10.Radio.Tests

open System
open System.Collections.Generic
open Microsoft.Extensions.Configuration
open NUnit.Framework
open Web10.Radio.Telegram

module TelegramConfigurationTests =
    let private baseline =
        Map.ofList
            [ "POSTGRES:CONNECTION_STRING", "Host=127.0.0.1;Port=5432;Database=web10;Username=web10;Password=telegram-test-db-secret"
              "TELEGRAM:BOT_TOKEN", "123456:abcdefghijklmnopqrstuv"
              "TELEGRAM:WEBHOOK_SECRET", "webhook-secret-0123456789"
              "TELEGRAM:CHANNEL_ID_OR_USERNAME", "@radiochannel"
              "TELEGRAM:REQUEST_PRICE_STARS", "100"
              "TELEGRAM:SAY_PRICE_STARS", "50"
              "OTEL:ENABLED", "true"
              "OTEL:EXPORTER_OTLP_ENDPOINT", "https://otel.web10.radio/v1/traces" ]

    let private buildConfiguration (pairs: Map<string, string>) =
        pairs
        |> Map.toList
        |> List.map (fun (key, value) -> KeyValuePair<string, string>(key, value))
        |> fun values -> ConfigurationBuilder().AddInMemoryCollection(values).Build()

    let private joinedErrors errors = String.concat Environment.NewLine errors

    let private assertNoSecretsWereLeaked (message: string) =
        [ "telegram-test-db-secret"
          "123456:abcdefghijklmnopqrstuv"
          "webhook-secret-0123456789" ]
        |> List.iter (fun secret -> Assert.That(message, Does.Not.Contain(secret)))

    [<Test>]
    let ``disabled OTEL without endpoint succeeds with no OTEL options`` () =
        let pairs =
            baseline
            |> Map.add "OTEL:ENABLED" "false"
            |> Map.remove "OTEL:EXPORTER_OTLP_ENDPOINT"

        match TelegramConfiguration.load (buildConfiguration pairs) with
        | Ok options -> Assert.That(Option.isNone options.Otel, Is.True)
        | Error errors ->
            Assert.Fail(sprintf "Disabled OTEL must not require an endpoint, but got %s." (joinedErrors errors))

    [<Test>]
    let ``enabled OTEL without endpoint fails with actionable validation`` () =
        let pairs = baseline |> Map.remove "OTEL:EXPORTER_OTLP_ENDPOINT"

        match TelegramConfiguration.load (buildConfiguration pairs) with
        | Ok _ -> Assert.Fail("Enabled OTEL without an endpoint must be rejected.")
        | Error errors ->
            let message = joinedErrors errors
            Assert.That(message, Does.Contain("WEB10_OTEL__EXPORTER_OTLP_ENDPOINT is required when WEB10_OTEL__ENABLED=true."))
            assertNoSecretsWereLeaked message

    [<Test>]
    let ``disabled OTEL ignores malformed endpoint`` () =
        let pairs =
            baseline
            |> Map.add "OTEL:ENABLED" "false"
            |> Map.add "OTEL:EXPORTER_OTLP_ENDPOINT" "not-an-absolute-uri"

        match TelegramConfiguration.load (buildConfiguration pairs) with
        | Ok options -> Assert.That(Option.isNone options.Otel, Is.True)
        | Error errors ->
            Assert.Fail(sprintf "Disabled OTEL must ignore malformed endpoints, but got %s." (joinedErrors errors))
