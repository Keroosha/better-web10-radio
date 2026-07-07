namespace Web10.Radio.Database

open Npgsql

type PostgresOptions =
    { ConnectionString: string }

module Postgres =
    let createDataSource (options: PostgresOptions) : NpgsqlDataSource =
        NpgsqlDataSourceBuilder(options.ConnectionString).Build()
