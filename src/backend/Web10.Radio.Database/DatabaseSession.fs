namespace Web10.Radio.Database

open System.Threading
open System.Threading.Tasks
open Npgsql

module DatabaseSession =
    let withConnection<'T>
        (dataSource: NpgsqlDataSource)
        (work: NpgsqlConnection -> CancellationToken -> Task<'T>)
        (cancellationToken: CancellationToken)
        : Task<'T> =
        task {
            let! connection = dataSource.OpenConnectionAsync(cancellationToken)
            use connection = connection
            return! work connection cancellationToken
        }

    let withTransaction<'T>
        (dataSource: NpgsqlDataSource)
        (work: NpgsqlConnection -> NpgsqlTransaction -> CancellationToken -> Task<'T>)
        (cancellationToken: CancellationToken)
        : Task<'T> =
        task {
            let! connection = dataSource.OpenConnectionAsync(cancellationToken)
            use connection = connection
            let! transaction = connection.BeginTransactionAsync(cancellationToken)
            use transaction = transaction

            try
                let! result = work connection transaction cancellationToken
                do! transaction.CommitAsync(cancellationToken)
                return result
            with ex ->
                try
                    do! transaction.RollbackAsync(cancellationToken)
                with _ ->
                    ()

                return raise ex
        }
