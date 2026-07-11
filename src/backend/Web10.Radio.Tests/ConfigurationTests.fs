namespace Web10.Radio.Tests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open Microsoft.Extensions.Configuration
open NUnit.Framework
open Web10.Radio.API

module ConfigurationTests =
    type private InvalidConfigurationCase =
        { Name: string
          Overrides: (string * string option) list
          ExpectedErrorFragments: string list }

    let private requiredEnvironmentVariables =
        [ "WEB10_POSTGRES__CONNECTION_STRING"
          "WEB10_TELEGRAM__BOT_TOKEN"
          "WEB10_TELEGRAM__WEBHOOK_SECRET"
          "WEB10_TELEGRAM__CHANNEL_ID_OR_USERNAME"
          "WEB10_TELEGRAM__REQUEST_PRICE_STARS"
          "WEB10_TELEGRAM__SAY_PRICE_STARS"
          "WEB10_STREAM__RTMP_URL"
          "WEB10_STREAM__RTMP_KEY"
          "WEB10_STREAM__STAGE_URL"
          "WEB10_STREAM__CALLBACK_TOKEN"
          "WEB10_STORAGE__TYPE"
          "WEB10_ADMIN__USERNAME"
          "WEB10_ADMIN__PASSWORD"
          "WEB10_OTEL__EXPORTER_OTLP_ENDPOINT"
          "WEB10_DATA_PROTECTION__KEY_RING_PATH" ]

    let private configurationPairs (root: string) =
        Map.ofList
            [ "POSTGRES:CONNECTION_STRING", "Host=127.0.0.1;Port=5432;Database=web10;Username=web10;Password=postgres-secret-42"
              "TELEGRAM:BOT_TOKEN", "123456:AbcdefghijklmnopQRSTuvwx"
              "TELEGRAM:WEBHOOK_SECRET", "webhook-Secret_12345"
              "TELEGRAM:CHANNEL_ID_OR_USERNAME", "@netscapedidnothingwrong"
              "TELEGRAM:REQUEST_PRICE_STARS", "100"
              "TELEGRAM:SAY_PRICE_STARS", "50"
              "STREAM:RTMP_URL", "rtmps://dc4-1.rtmp.t.me/s/"
              "STREAM:RTMP_KEY", "rtmp-key-Secret_12345"
              "STREAM:STAGE_URL", "https://stage.web10.radio/"
              "STREAM:CALLBACK_TOKEN", "stream-callback-token-Secret_123456"
              "STORAGE:TYPE", "Local"
              "STORAGE:LOCAL_ROOT", Path.Combine(root, "library")
              "ADMIN:USERNAME", "test-admin"
              "ADMIN:PASSWORD", "test-admin-password-1234567890"
              "OTEL:EXPORTER_OTLP_ENDPOINT", "https://otel.web10.radio/v1/traces"
              "DATA_PROTECTION:KEY_RING_PATH", Path.Combine(root, "keys") ]

    let private applyOverrides (pairs: Map<string, string>) overrides =
        overrides
        |> List.fold (fun configured (key, value) ->
            match value with
            | Some text -> configured |> Map.add key text
            | None -> configured |> Map.remove key) pairs

    let private buildConfiguration (pairs: Map<string, string>) =
        let configurationPairs =
            pairs
            |> Map.toList
            |> List.map (fun (key, value) -> KeyValuePair<string, string>(key, value))

        ConfigurationBuilder().AddInMemoryCollection(configurationPairs).Build()

    let private joinedErrors errors = String.concat Environment.NewLine errors

    let private assertNoSecretsWereLeaked (pairs: Map<string, string>) (message: string) =
        [ "POSTGRES:CONNECTION_STRING"
          "TELEGRAM:BOT_TOKEN"
          "TELEGRAM:WEBHOOK_SECRET"
          "STREAM:RTMP_KEY"
          "STREAM:CALLBACK_TOKEN"
          "ADMIN:PASSWORD" ]
        |> List.choose (fun key -> pairs |> Map.tryFind key)
        |> List.iter (fun secret -> Assert.That(message, Does.Not.Contain(secret), "Configuration diagnostics must not echo supplied secrets."))

    let private assertLoadRejects (case: InvalidConfigurationCase) (pairs: Map<string, string>) =
        match Configuration.load (buildConfiguration pairs) with
        | Ok _ -> Assert.Fail(sprintf "%s: expected semantic configuration rejection." case.Name)
        | Error errors ->
            let message = joinedErrors errors

            case.ExpectedErrorFragments
            |> List.iter (fun expected ->
                Assert.That(message, Does.Contain(expected), sprintf "%s: expected configuration error fragment %s." case.Name expected))

            assertNoSecretsWereLeaked pairs message

    let private invalidConfigurationCases _ =
        let uniqueSuffix = Guid.NewGuid().ToString("N")
        let localRootFile = Path.Combine("/proc", "web10-radio-local-root-" + uniqueSuffix)
        let keyRingFile = Path.Combine("/proc", "web10-radio-key-ring-" + uniqueSuffix)

        [ { Name = "malformed Npgsql connection string"
            Overrides = [ "POSTGRES:CONNECTION_STRING", Some "Host=localhost;Password=invalid-connection-secret;UnsupportedKeyword=true" ]
            ExpectedErrorFragments = [ "WEB10_POSTGRES__CONNECTION_STRING" ] }
          { Name = "Npgsql connection string requires host database and username"
            Overrides = [ "POSTGRES:CONNECTION_STRING", Some "Host=localhost;Database=;Username=web10;Password=invalid-connection-secret" ]
            ExpectedErrorFragments = [ "WEB10_POSTGRES__CONNECTION_STRING must specify non-empty Host, Database, and Username values." ] }
          { Name = "stream callback token must be nontrivial and at least 24 characters"
            Overrides = [ "STREAM:CALLBACK_TOKEN", Some "weak" ]
            ExpectedErrorFragments = [ "WEB10_STREAM__CALLBACK_TOKEN" ] }
          { Name = "S3 force path style is lowercase boolean only"
            Overrides =
                [ "STORAGE:TYPE", Some "S3"
                  "STORAGE:LOCAL_ROOT", None
                  "STORAGE:S3_BUCKET", Some "web10-radio-test-bucket"
                  "STORAGE:S3_REGION", Some "us-east-1"
                  "STORAGE:S3_FORCE_PATH_STYLE", Some "True" ]
            ExpectedErrorFragments = [ "WEB10_STORAGE__S3_FORCE_PATH_STYLE must be exactly true or false." ] }
          { Name = "Local storage rejects enabled S3 force path style"
            Overrides = [ "STORAGE:S3_FORCE_PATH_STYLE", Some "true" ]
            ExpectedErrorFragments = [ "S3 bucket, region, service URL, and true force-path-style settings must be unset when WEB10_STORAGE__TYPE=Local." ] }
          { Name = "admin username must remain nonblank after trimming"
            Overrides = [ "ADMIN:USERNAME", Some "   " ]
            ExpectedErrorFragments = [ "WEB10_ADMIN__USERNAME" ] }
          { Name = "admin username is capped at 64 characters after trimming"
            Overrides = [ "ADMIN:USERNAME", Some(String.replicate 65 "u") ]
            ExpectedErrorFragments = [ "WEB10_ADMIN__USERNAME" ] }
          { Name = "admin password must contain at least 12 characters"
            Overrides = [ "ADMIN:PASSWORD", Some "short-pass" ]
            ExpectedErrorFragments = [ "WEB10_ADMIN__PASSWORD" ] }
          { Name = "admin password is capped at 256 characters"
            Overrides = [ "ADMIN:PASSWORD", Some(String.replicate 257 "p") ]
            ExpectedErrorFragments = [ "WEB10_ADMIN__PASSWORD" ] }
          { Name = "stream callback token rejects comma-delimited values"
            Overrides = [ "STREAM:CALLBACK_TOKEN", Some "stream-callback-token-Secret_123456,second" ]
            ExpectedErrorFragments = [ "WEB10_STREAM__CALLBACK_TOKEN" ] }
          { Name = "stage URI uses a disallowed scheme"
            Overrides = [ "STREAM:STAGE_URL", Some "ftp://stage.web10.radio/" ]
            ExpectedErrorFragments = [ "WEB10_STREAM__STAGE_URL" ] }
          { Name = "RTMP URI uses a disallowed scheme"
            Overrides = [ "STREAM:RTMP_URL", Some "https://stream.web10.radio/" ]
            ExpectedErrorFragments = [ "WEB10_STREAM__RTMP_URL" ] }
          { Name = "OTLP URI uses a disallowed scheme"
            Overrides = [ "OTEL:EXPORTER_OTLP_ENDPOINT", Some "ftp://otel.web10.radio/v1/traces" ]
            ExpectedErrorFragments = [ "WEB10_OTEL__EXPORTER_OTLP_ENDPOINT" ] }
          { Name = "Telegram token has invalid syntax"
            Overrides = [ "TELEGRAM:BOT_TOKEN", Some "telegram-token-secret" ]
            ExpectedErrorFragments = [ "WEB10_TELEGRAM__BOT_TOKEN" ] }
          { Name = "Telegram channel has invalid syntax"
            Overrides = [ "TELEGRAM:CHANNEL_ID_OR_USERNAME", Some "@bad!" ]
            ExpectedErrorFragments = [ "WEB10_TELEGRAM__CHANNEL_ID_OR_USERNAME" ] }
          { Name = "Telegram request price is required"
            Overrides = [ "TELEGRAM:REQUEST_PRICE_STARS", None ]
            ExpectedErrorFragments = [ "WEB10_TELEGRAM__REQUEST_PRICE_STARS must be a positive 32-bit integer." ] }
          { Name = "Telegram say price is required"
            Overrides = [ "TELEGRAM:SAY_PRICE_STARS", None ]
            ExpectedErrorFragments = [ "WEB10_TELEGRAM__SAY_PRICE_STARS must be a positive 32-bit integer." ] }
          { Name = "Telegram request price must be an integer"
            Overrides = [ "TELEGRAM:REQUEST_PRICE_STARS", Some "100.0" ]
            ExpectedErrorFragments = [ "WEB10_TELEGRAM__REQUEST_PRICE_STARS must be a positive 32-bit integer." ] }
          { Name = "Telegram say price must be an integer"
            Overrides = [ "TELEGRAM:SAY_PRICE_STARS", Some "fifty" ]
            ExpectedErrorFragments = [ "WEB10_TELEGRAM__SAY_PRICE_STARS must be a positive 32-bit integer." ] }
          { Name = "Telegram request price must be positive"
            Overrides = [ "TELEGRAM:REQUEST_PRICE_STARS", Some "0" ]
            ExpectedErrorFragments = [ "WEB10_TELEGRAM__REQUEST_PRICE_STARS must be a positive 32-bit integer." ] }
          { Name = "Telegram say price must be positive"
            Overrides = [ "TELEGRAM:SAY_PRICE_STARS", Some "-50" ]
            ExpectedErrorFragments = [ "WEB10_TELEGRAM__SAY_PRICE_STARS must be a positive 32-bit integer." ] }
          { Name = "S3 bucket has invalid syntax"
            Overrides =
                [ "STORAGE:TYPE", Some "S3"
                  "STORAGE:LOCAL_ROOT", None
                  "STORAGE:S3_BUCKET", Some "UpperCase.bucket"
                  "STORAGE:S3_REGION", Some "us-east-1" ]
            ExpectedErrorFragments = [ "WEB10_STORAGE__S3_BUCKET" ] }
          { Name = "S3 region has invalid syntax"
            Overrides =
                [ "STORAGE:TYPE", Some "S3"
                  "STORAGE:LOCAL_ROOT", None
                  "STORAGE:S3_BUCKET", Some "web10-radio-test-bucket"
                  "STORAGE:S3_REGION", Some "US-EAST-1" ]
            ExpectedErrorFragments = [ "WEB10_STORAGE__S3_REGION" ] }
          { Name = "S3 service URL uses a disallowed scheme"
            Overrides =
                [ "STORAGE:TYPE", Some "S3"
                  "STORAGE:LOCAL_ROOT", None
                  "STORAGE:S3_BUCKET", Some "web10-radio-test-bucket"
                  "STORAGE:S3_REGION", Some "us-east-1"
                  "STORAGE:S3_SERVICE_URL", Some "ftp://object-store.web10.radio/" ]
            ExpectedErrorFragments = [ "WEB10_STORAGE__S3_SERVICE_URL" ] }
          { Name = "Local storage rejects S3-only fields"
            Overrides = [ "STORAGE:S3_BUCKET", Some "web10-radio-test-bucket" ]
            ExpectedErrorFragments = [ "S3 bucket, region, service URL, and true force-path-style settings must be unset when WEB10_STORAGE__TYPE=Local." ] }
          { Name = "S3 storage rejects a Local root"
            Overrides =
                [ "STORAGE:TYPE", Some "S3"
                  "STORAGE:S3_BUCKET", Some "web10-radio-test-bucket"
                  "STORAGE:S3_REGION", Some "us-east-1" ]
            ExpectedErrorFragments = [ "WEB10_STORAGE__LOCAL_ROOT must be unset when WEB10_STORAGE__TYPE=S3." ] }
          { Name = "Local root must identify a writable directory"
            Overrides = [ "STORAGE:LOCAL_ROOT", Some localRootFile ]
            ExpectedErrorFragments = [ "WEB10_STORAGE__LOCAL_ROOT" ] }
          { Name = "data-protection key ring must identify a writable directory"
            Overrides = [ "DATA_PROTECTION:KEY_RING_PATH", Some keyRingFile ]
            ExpectedErrorFragments = [ "WEB10_DATA_PROTECTION__KEY_RING_PATH" ] } ]

    let private withTemporaryDirectory work =
        let root = Path.Combine(Path.GetTempPath(), "web10-radio-configuration-tests-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore

        try
            work root
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)

    let private assertProcessRejects (case: InvalidConfigurationCase) (pairs: Map<string, string>) =
        task {
            let startInfo = ProcessStartInfo("dotnet")
            startInfo.ArgumentList.Add(typeof<ApiProgramMarker>.Assembly.Location)
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true

            startInfo.Environment.Keys
            |> Seq.cast<string>
            |> Seq.filter (fun key -> key.StartsWith("WEB10_", StringComparison.OrdinalIgnoreCase))
            |> Seq.toList
            |> List.iter (fun key -> startInfo.Environment.Remove(key) |> ignore)

            pairs
            |> Map.iter (fun key value ->
                let environmentKey = "WEB10_" + key.Replace(":", "__")
                startInfo.Environment[environmentKey] <- value)

            use childProcess = new Process()
            childProcess.StartInfo <- startInfo
            Assert.That(childProcess.Start(), Is.True, sprintf "%s: failed to launch the API process." case.Name)

            let standardOutput = childProcess.StandardOutput.ReadToEndAsync()
            let standardError = childProcess.StandardError.ReadToEndAsync()
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0))

            try
                do! childProcess.WaitForExitAsync(timeout.Token)
            with :? OperationCanceledException ->
                childProcess.Kill(true)
                Assert.Fail(sprintf "%s: invalid configuration did not terminate the API process." case.Name)

            let! output = standardOutput
            let! error = standardError
            let diagnostics = output + Environment.NewLine + error

            Assert.That(childProcess.ExitCode, Is.Not.EqualTo(0), sprintf "%s: invalid configuration must prevent host startup." case.Name)

            case.ExpectedErrorFragments
            |> List.iter (fun expected ->
                Assert.That(diagnostics, Does.Contain(expected), sprintf "%s: process diagnostics omitted %s." case.Name expected))

            assertNoSecretsWereLeaked pairs diagnostics
        }

    [<Test>]
    let ``load reports all missing required environment keys without secret values`` () =
        match Configuration.load (ConfigurationBuilder().Build()) with
        | Ok _ -> Assert.Fail("Expected missing configuration to return Error.")
        | Error errors ->
            let message = joinedErrors errors

            requiredEnvironmentVariables
            |> List.iter (fun environmentVariable -> Assert.That(message, Does.Contain(environmentVariable)))

    [<Test>]
    let ``load reads configured Telegram Stars prices`` () =
        withTemporaryDirectory (fun root ->
            match Configuration.load (configurationPairs root |> buildConfiguration) with
            | Ok options ->
                Assert.That(options.Telegram.RequestPriceStars, Is.EqualTo(100))
                Assert.That(options.Telegram.SayPriceStars, Is.EqualTo(50))
            | Error errors -> Assert.Fail(sprintf "Expected configured Telegram Stars prices to be accepted, but got %s." (joinedErrors errors)))

    [<Test>]
    let ``load defaults an omitted Telegram update mode to Webhook`` () =
        withTemporaryDirectory (fun root ->
            match Configuration.load (configurationPairs root |> buildConfiguration) with
            | Ok options -> Assert.That(options.Telegram.UpdateMode, Is.EqualTo(Web10.Radio.Telegram.TelegramUpdateMode.Webhook))
            | Error errors -> Assert.Fail(sprintf "Expected omitted Telegram update mode to default to Webhook, but got %s." (joinedErrors errors)))

    [<Test>]
    let ``load accepts only the exact LongPolling Telegram update mode`` () =
        withTemporaryDirectory (fun root ->
            let pairs = configurationPairs root |> Map.add "TELEGRAM:UPDATE_MODE" "LongPolling"

            match Configuration.load (buildConfiguration pairs) with
            | Ok options -> Assert.That(options.Telegram.UpdateMode, Is.EqualTo(Web10.Radio.Telegram.TelegramUpdateMode.LongPolling))
            | Error errors -> Assert.Fail(sprintf "Expected exact LongPolling Telegram update mode to be accepted, but got %s." (joinedErrors errors)))

    [<Test>]
    let ``load rejects an invalid Telegram update mode without leaking its value or configured secrets`` () =
        withTemporaryDirectory (fun root ->
            let invalidMode = "invalid-update-mode-secret-987654"
            let pairs = configurationPairs root |> Map.add "TELEGRAM:UPDATE_MODE" invalidMode

            match Configuration.load (buildConfiguration pairs) with
            | Ok _ -> Assert.Fail("Expected an invalid Telegram update mode to be rejected.")
            | Error errors ->
                let message = joinedErrors errors
                Assert.That(errors, Is.EqualTo(box [ "WEB10_TELEGRAM__UPDATE_MODE must be exactly Webhook or LongPolling." ]))
                Assert.That(message, Does.Not.Contain(invalidMode), "Configuration diagnostics must not echo the invalid update-mode value.")
                assertNoSecretsWereLeaked pairs message)

    [<Test>]
    let ``load aggregates exact Telegram Stars price diagnostics`` () =
        withTemporaryDirectory (fun root ->
            let pairs =
                configurationPairs root
                |> Map.remove "TELEGRAM:REQUEST_PRICE_STARS"
                |> Map.add "TELEGRAM:SAY_PRICE_STARS" "0"

            match Configuration.load (buildConfiguration pairs) with
            | Ok _ -> Assert.Fail("Expected missing and non-positive Telegram Stars prices to be rejected.")
            | Error errors ->
                let expected =
                    Set.ofList
                        [ "WEB10_TELEGRAM__REQUEST_PRICE_STARS must be a positive 32-bit integer."
                          "WEB10_TELEGRAM__SAY_PRICE_STARS must be a positive 32-bit integer." ]

                let hasExactErrors = (errors |> Set.ofList) = expected
                Assert.That(hasExactErrors, Is.True))

    [<Test>]
    let ``load rejects every Telegram Stars price validation failure with exact diagnostics`` () =
        withTemporaryDirectory (fun root ->
            let baseline = configurationPairs root

            invalidConfigurationCases root
            |> List.filter (fun case -> case.Name.StartsWith("Telegram ", StringComparison.Ordinal) && case.Name.Contains(" price ", StringComparison.Ordinal))
            |> List.iter (fun case ->
                let pairs = baseline |> applyOverrides <| case.Overrides

                match Configuration.load (buildConfiguration pairs) with
                | Ok _ -> Assert.Fail(sprintf "%s: expected Telegram Stars price validation failure." case.Name)
                | Error errors ->
                    let hasExactErrors = errors = case.ExpectedErrorFragments
                    Assert.That(hasExactErrors, Is.True, sprintf "%s: expected only the exact Telegram Stars price diagnostic." case.Name)))

    [<Test>]
    let ``load rejects every semantic invalid configuration category without leaking secrets`` () =
        withTemporaryDirectory (fun root ->
            let baseline = configurationPairs root

            invalidConfigurationCases root
            |> List.iter (fun case -> baseline |> applyOverrides <| case.Overrides |> assertLoadRejects case))

    [<Test>]
    let ``API process rejects every semantic invalid configuration category before host startup`` () : Threading.Tasks.Task =
        withTemporaryDirectory (fun root ->
            task {
                let baseline = configurationPairs root

                for case in invalidConfigurationCases root do
                    do! baseline |> applyOverrides <| case.Overrides |> assertProcessRejects case
            })

    [<Test>]
    let ``Local storage accepts explicit disabled S3 force path style`` () =
        withTemporaryDirectory (fun root ->
            let pairs = configurationPairs root |> Map.add "STORAGE:S3_FORCE_PATH_STYLE" "false"

            match Configuration.load (buildConfiguration pairs) with
            | Ok options -> Assert.That(options.Storage.S3ForcePathStyle, Is.False)
            | Error errors -> Assert.Fail(sprintf "Expected explicit Local false S3 force-path-style to be accepted, but got %s." (joinedErrors errors)))
