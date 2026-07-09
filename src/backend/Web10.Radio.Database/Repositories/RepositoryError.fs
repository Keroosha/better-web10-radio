namespace Web10.Radio.Database.Repositories

type RepositoryError =
    | InvalidBatchSize of value: int
    | InvalidStreamNodeStatus of value: string
    | DatabaseError of operation: string * message: string

module RepositoryError =
    let toMessage error =
        match error with
        | InvalidBatchSize value -> sprintf "Batch size must be positive. Actual: %i." value
        | InvalidStreamNodeStatus value -> sprintf "Invalid stream-node status: %s." value
        | DatabaseError(operation, message) -> sprintf "Database operation failed: %s: %s" operation message
