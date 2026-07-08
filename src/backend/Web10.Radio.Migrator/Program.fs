namespace Web10.Radio.Migrator

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting

module Program =
    [<EntryPoint>]
    let main args =
        let builder = Host.CreateApplicationBuilder(args)
        builder.Configuration.AddEnvironmentVariables(prefix = "WEB10_") |> ignore

        match MigratorConfiguration.load builder.Configuration with
        | Error errors ->
            eprintfn "Invalid Web10 migrator configuration:"
            errors |> List.iter (eprintfn "- %s")
            2
        | Ok options ->
            try
                MigrationRunner.migrateToLatest options
                printfn "Web10 database migrated to latest version."
                0
            with ex ->
                eprintfn "Web10 database migration failed: %s" ex.Message
                1
