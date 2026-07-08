namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Web10.Radio.Database

module PlaybackQueueRepository =
    [<Literal>]
    let private claimNextSql = """WITH next_item AS (
    SELECT "Id"
    FROM "PlaybackQueue"
    WHERE "IsDeleted" = false
      AND "Status" = 'Queued'
    ORDER BY "Priority" DESC, "RequestedAtUtc" ASC, "CreatedAtUtc" ASC
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
UPDATE "PlaybackQueue" AS q
SET "Status" = 'Claimed',
    "ClaimedAtUtc" = @ClaimedAtUtc,
    "UpdatedAtUtc" = @ClaimedAtUtc
FROM next_item
WHERE q."Id" = next_item."Id"
RETURNING q."Id";"""

    let claimNext
        (dataSource: NpgsqlDataSource)
        (claimedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Guid option> =
        DatabaseSession.withTransaction
            dataSource
            (fun connection transaction cancellationToken ->
                task {
                    use command = new NpgsqlCommand(claimNextSql, connection, transaction)
                    command.Parameters.AddWithValue("ClaimedAtUtc", claimedAtUtc) |> ignore
                    let! claimed = command.ExecuteScalarAsync(cancellationToken)

                    match claimed with
                    | null -> return None
                    | :? DBNull -> return None
                    | :? Guid as id -> return Some id
                    | value -> return Some(unbox<Guid> value)
                })
            cancellationToken
