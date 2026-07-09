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

    let withTransactionResult<'T, 'E>
        (dataSource: NpgsqlDataSource)
        (work: NpgsqlConnection -> NpgsqlTransaction -> CancellationToken -> Task<Result<'T, 'E>>)
        (cancellationToken: CancellationToken)
        : Task<Result<'T, 'E>> =
        task {
            let! connection = dataSource.OpenConnectionAsync(cancellationToken)
            use connection = connection
            let! transaction = connection.BeginTransactionAsync(cancellationToken)
            use transaction = transaction

            try
                let! result = work connection transaction cancellationToken

                match result with
                | Ok _ ->
                    do! transaction.CommitAsync(cancellationToken)
                    return result
                | Error _ ->
                    do! transaction.RollbackAsync(cancellationToken)
                    return result
            with ex ->
                try
                    do! transaction.RollbackAsync(cancellationToken)
                with _ ->
                    ()

                return raise ex
        }
