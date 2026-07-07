namespace Web10.Radio.API

open System
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Web10.Radio.Database
open Web10.Radio.Telegram

module Program =
    let private invalidConfigurationMessage (errors: string list) =
        let renderedErrors =
            errors
            |> List.map (fun error -> sprintf "- %s" error)
            |> String.concat Environment.NewLine

        sprintf "Invalid Web10 configuration:%s%s" Environment.NewLine renderedErrors

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)
        builder.Configuration.AddEnvironmentVariables(prefix = "WEB10_") |> ignore

        let options =
            match Configuration.load builder.Configuration with
            | Ok options -> options
            | Error errors -> raise (InvalidOperationException(invalidConfigurationMessage errors))

        builder.Services
        |> DatabaseComposition.addDatabase options.Postgres
        |> ApplicationComposition.addApplicationServices
        |> TelegramComposition.addTelegram options.Telegram
        // B2 adds real MailboxProcessor-backed workers here.
        |> HealthComposition.addHealthChecks options
        |> ObservabilityComposition.addObservability options.Otel builder.Environment
        |> ignore

        let app = builder.Build()
        HealthEndpoints.mapHealthEndpoints app
        app.Run()
        0
