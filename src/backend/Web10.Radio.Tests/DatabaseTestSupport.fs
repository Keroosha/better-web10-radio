namespace Web10.Radio.Tests

open System
open System.Threading.Tasks
open Testcontainers.PostgreSql
open Web10.Radio.Database
open Web10.Radio.Migrator

module DatabaseTestSupport =
    let withMigratedDatabase (work: string -> Task<'T>) : Task<'T> =
        task {
            let container =
                PostgreSqlBuilder("postgres:17")
                    .WithDatabase("web10")
                    .WithUsername("web10")
                    .WithPassword("web10")
                    .Build()

            let mutable result = Unchecked.defaultof<'T>
            let mutable exceptionOption: exn option = None

            try
                do! container.StartAsync()
                let connectionString = container.GetConnectionString()
                MigrationRunner.migrateToLatest { ConnectionString = connectionString }
                let! workResult = work connectionString
                result <- workResult
            with ex ->
                exceptionOption <- Some ex

            do! container.DisposeAsync().AsTask()

            match exceptionOption with
            | Some ex -> return raise ex
            | None -> return result
        }
