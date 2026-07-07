namespace Web10.Radio.Database

open Microsoft.Extensions.DependencyInjection
open Npgsql

module DatabaseComposition =
    let addDatabase (options: PostgresOptions) (services: IServiceCollection) : IServiceCollection =
        services.AddSingleton<NpgsqlDataSource>(fun _ -> Postgres.createDataSource options)
