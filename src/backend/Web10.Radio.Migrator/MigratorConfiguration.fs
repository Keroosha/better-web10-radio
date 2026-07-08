namespace Web10.Radio.Migrator

open System
open Microsoft.Extensions.Configuration
open Web10.Radio.Database

module MigratorConfiguration =
    let load (configuration: IConfiguration) : Result<PostgresOptions, string list> =
        let connectionString = configuration["POSTGRES:CONNECTION_STRING"]

        if String.IsNullOrWhiteSpace connectionString then
            Error [ "WEB10_POSTGRES__CONNECTION_STRING is required and must be non-empty." ]
        else
            Ok { ConnectionString = connectionString }
