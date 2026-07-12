namespace Web10.Radio.API

open System
open System.Net.Http
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open System.Text.Json
open Web10.Radio.Database
open Web10.Radio.Application

type ApiProgramMarker = class end

module Program =
    let private invalidConfigurationMessage (errors: string list) =
        let renderedErrors =
            errors
            |> List.map (fun error -> sprintf "- %s" error)
            |> String.concat Environment.NewLine

        sprintf "Invalid Web10 configuration:%s%s" Environment.NewLine renderedErrors

    let private runHealthCheck (uriText: string) =
        try
            let uri = Uri(uriText, UriKind.Absolute)

            if uri.Scheme <> Uri.UriSchemeHttp && uri.Scheme <> Uri.UriSchemeHttps then
                2
            else
                use client = new HttpClient()
                client.Timeout <- TimeSpan.FromSeconds(4.0)
                use response = client.GetAsync(uri).GetAwaiter().GetResult()
                if response.IsSuccessStatusCode then 0 else 1
        with _ ->
            1

    [<EntryPoint>]
    let main args =
        match args with
        | [| "--health-check"; uri |] -> runHealthCheck uri
        | _ ->
            let builder = WebApplication.CreateBuilder(args)
            builder.Services.ConfigureHttpJsonOptions(fun options ->
                options.SerializerOptions.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
                options.SerializerOptions.DictionaryKeyPolicy <- JsonNamingPolicy.CamelCase)
            |> ignore

            builder.Configuration.AddEnvironmentVariables(prefix = "WEB10_") |> ignore

            let options =
                match Configuration.load builder.Configuration with
                | Ok options -> options
                | Error errors -> raise (InvalidOperationException(invalidConfigurationMessage errors))

            builder.Services
            |> DatabaseComposition.addDatabase options.Postgres
            |> ApplicationComposition.addApplicationServices
            |> BackgroundWorkerComposition.addBackgroundWorkers options
            |> ApiEndpoints.addApiServices options.Admin options.DevelopmentFixturesEnabled options.Stream
            |> HealthComposition.addHealthChecks options
            |> ObservabilityComposition.addObservability options.Otel builder.Environment
            |> ignore

            let app = builder.Build()
            app.UseAuthentication() |> ignore
            app.UseAuthorization() |> ignore
            HealthEndpoints.mapHealthEndpoints app
            ApiEndpoints.mapApiV0Endpoints app
            app.Run()
            0
