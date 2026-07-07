namespace Web10.Radio.Tests

open System
open System.Collections.Generic
open Microsoft.Extensions.Configuration
open NUnit.Framework
open Web10.Radio.API

module ConfigurationTests =
    let private requiredEnvironmentVariables () =
        [ "WEB10_POSTGRES__CONNECTION_STRING"
          "WEB10_TELEGRAM__BOT_TOKEN"
          "WEB10_TELEGRAM__WEBHOOK_SECRET"
          "WEB10_TELEGRAM__CHANNEL_ID_OR_USERNAME"
          "WEB10_STREAM__RTMP_URL"
          "WEB10_STREAM__RTMP_KEY"
          "WEB10_STREAM__STAGE_URL"
          "WEB10_STORAGE__TYPE"
          "WEB10_STORAGE__LOCAL_ROOT"
          "WEB10_STORAGE__S3_BUCKET"
          "WEB10_OTEL__EXPORTER_OTLP_ENDPOINT"
          "WEB10_DATA_PROTECTION__KEY_RING_PATH" ]

    let private sensitiveValues () =
        [ "123456:TESTTOKEN"
          "test-webhook-secret"
          "test-rtmp-key" ]

    let private validConfigurationPairs () =
        [ "POSTGRES:CONNECTION_STRING", "Host=127.0.0.1;Port=5432;Database=web10;Username=web10;Password=web10"
          "TELEGRAM:BOT_TOKEN", "123456:TESTTOKEN"
          "TELEGRAM:WEBHOOK_SECRET", "test-webhook-secret"
          "TELEGRAM:CHANNEL_ID_OR_USERNAME", "@netscapedidnothingwrong"
          "STREAM:RTMP_URL", "rtmps://dc4-1.rtmp.t.me/s/"
          "STREAM:RTMP_KEY", "test-rtmp-key"
          "STREAM:STAGE_URL", "http://localhost:5173/"
          "STORAGE:TYPE", "Local"
          "STORAGE:LOCAL_ROOT", "/tmp/web10-radio-library"
          "STORAGE:S3_BUCKET", "web10-radio-test-bucket"
          "OTEL:EXPORTER_OTLP_ENDPOINT", "http://localhost:4317"
          "DATA_PROTECTION:KEY_RING_PATH", "/tmp/web10-radio-keys" ]

    let private buildConfiguration pairs =
        let configurationPairs =
            pairs
            |> List.map (fun (key, value) -> KeyValuePair<string, string>(key, value))

        ConfigurationBuilder().AddInMemoryCollection(configurationPairs).Build()

    let private joinedErrors errors = String.concat Environment.NewLine errors

    let private assertErrorsContainEnvironmentVariables expectedEnvironmentVariables errors =
        let message = joinedErrors errors

        expectedEnvironmentVariables
        |> List.iter (fun environmentVariable ->
            Assert.That(message, Does.Contain(environmentVariable), sprintf "Expected configuration errors to name %s." environmentVariable))

    let private assertErrorsDoNotContainSensitiveValues errors =
        let message = joinedErrors errors

        sensitiveValues ()
        |> List.iter (fun sensitiveValue ->
            Assert.That(message.Contains sensitiveValue, Is.False, "Configuration errors must not include supplied secret values."))

    [<Test>]
    let ``load reports all missing required env keys`` () =
        let configuration = ConfigurationBuilder().Build()

        match Configuration.load configuration with
        | Ok _ -> Assert.Fail("Expected missing configuration to return Error.")
        | Error errors ->
            assertErrorsContainEnvironmentVariables (requiredEnvironmentVariables ()) errors
            assertErrorsDoNotContainSensitiveValues errors

    [<Test>]
    let ``load accepts valid stripped WEB10 configuration keys`` () =
        let configuration = buildConfiguration (validConfigurationPairs ())

        match Configuration.load configuration with
        | Error errors -> Assert.Fail("Expected valid configuration to return Ok, but got errors: " + joinedErrors errors)
        | Ok options ->
            Assert.That(options.Storage.Type, Is.EqualTo(Local))
            Assert.That(options.Stream.StageUrl.AbsoluteUri, Is.EqualTo("http://localhost:5173/"))

    [<Test>]
    let ``load rejects invalid storage type and URI values`` () =
        let invalidPairs =
            validConfigurationPairs ()
            |> List.map (fun (key, value) ->
                match key with
                | "STORAGE:TYPE" -> key, "Filesystem"
                | "STREAM:STAGE_URL" -> key, "not-a-uri"
                | _ -> key, value)

        let configuration = buildConfiguration invalidPairs

        match Configuration.load configuration with
        | Ok _ -> Assert.Fail("Expected invalid configuration to return Error.")
        | Error errors ->
            assertErrorsContainEnvironmentVariables [ "WEB10_STORAGE__TYPE"; "WEB10_STREAM__STAGE_URL" ] errors
            assertErrorsDoNotContainSensitiveValues errors
