namespace Web10.Radio.Telegram

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Web10.Radio.Application

[<Sealed>]
type TelegramProgramMarker = class end

module Program =
    let private healthCheck (url: string) =
        task {
            use client = new HttpClient(Timeout = TimeSpan.FromSeconds(10.0))
            try
                use! response = client.GetAsync(url)
                return if response.IsSuccessStatusCode then 0 else 1
            with _ ->
                return 1
        }

    let private loadOptions (builder: WebApplicationBuilder) =
        match TelegramConfiguration.load builder.Configuration with
        | Ok options -> Ok options
        | Error errors ->
            errors |> List.iter Console.Error.WriteLine
            Error 1

    [<EntryPoint>]
    let main argv =
        match Array.tryFindIndex ((=) "--health-check") argv with
        | Some index when index + 1 < argv.Length ->
            healthCheck argv[index + 1] |> fun pending -> pending.GetAwaiter().GetResult()
        | Some _ ->
            Console.Error.WriteLine("--health-check requires an absolute URL.")
            1
        | None ->
            let builder = WebApplication.CreateBuilder(argv)
            builder.Configuration.AddEnvironmentVariables(prefix = "WEB10_") |> ignore
            match loadOptions builder with
            | Error exitCode -> exitCode
            | Ok options ->
                builder.Services
                |> TelegramComposition.addTelegramApplication options
                |> TelegramLongPollingComposition.addTelegramLongPolling options.Telegram.UpdateMode
                |> ObservabilityComposition.addObservability options.Otel builder.Environment
                |> ignore

                let app = builder.Build()
                TelegramEndpoints.mapTelegramEndpoints app
                HealthEndpoints.mapHealthEndpoints app
                app.Run()
                0
