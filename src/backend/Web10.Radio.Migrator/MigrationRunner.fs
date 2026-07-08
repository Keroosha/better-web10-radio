namespace Web10.Radio.Migrator

open FluentMigrator.Runner
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Web10.Radio.Database
open Web10.Radio.Database.Migrations

module MigrationRunner =
    let migrateToLatest (options: PostgresOptions) : unit =
        let services = ServiceCollection()

        services
            .AddFluentMigratorCore()
            .ConfigureRunner(fun runnerBuilder ->
                runnerBuilder
                    .AddPostgres()
                    .WithGlobalConnectionString(options.ConnectionString)
                    .ScanIn(typeof<CreateInitialSchema>.Assembly)
                    .For.Migrations()
                |> ignore)
            .AddLogging(fun logging -> logging.AddFluentMigratorConsole() |> ignore)
        |> ignore

        use provider = services.BuildServiceProvider()
        let runner = provider.GetRequiredService<IMigrationRunner>()
        runner.MigrateUp()
