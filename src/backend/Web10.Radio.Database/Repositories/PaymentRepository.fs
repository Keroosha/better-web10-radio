namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Npgsql

module PaymentRepository =
    [<Literal>]
    let private markPaidSql = """UPDATE "Payments"
SET "Status" = 'Paid',
    "TelegramPaymentChargeId" = @TelegramPaymentChargeId,
    "PaidAtUtc" = @PaidAtUtc,
    "UpdatedAtUtc" = @PaidAtUtc
WHERE "Id" = @PaymentId
  AND "IsDeleted" = false
  AND "Status" IN ('InvoiceCreated', 'PreCheckoutApproved')
  AND "AmountStars" = @AmountStars
  AND "Currency" = @Currency;"""

    [<Literal>]
    let private readPaymentSql = """SELECT "Status", "TelegramPaymentChargeId", "AmountStars", "Currency"
FROM "Payments"
WHERE "Id" = @PaymentId
  AND "IsDeleted" = false
LIMIT 1;"""

    let private databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)

    let markDonationPaid
        (dataSource: NpgsqlDataSource)
        (paymentId: Guid)
        (telegramPaymentChargeId: string)
        (amountStars: int)
        (currency: string)
        (paidAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<bool, RepositoryError>> =
        taskResult {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)

                use updateCommand = new NpgsqlCommand(markPaidSql, connection)
                updateCommand.Parameters.AddWithValue("PaymentId", paymentId) |> ignore
                updateCommand.Parameters.AddWithValue("TelegramPaymentChargeId", telegramPaymentChargeId) |> ignore
                updateCommand.Parameters.AddWithValue("AmountStars", amountStars) |> ignore
                updateCommand.Parameters.AddWithValue("Currency", currency) |> ignore
                updateCommand.Parameters.AddWithValue("PaidAtUtc", paidAtUtc) |> ignore
                let! changed = updateCommand.ExecuteNonQueryAsync(cancellationToken)

                if changed = 1 then
                    return true
                else
                    use readCommand = new NpgsqlCommand(readPaymentSql, connection)
                    readCommand.Parameters.AddWithValue("PaymentId", paymentId) |> ignore
                    let! reader = readCommand.ExecuteReaderAsync(cancellationToken)
                    use reader = reader
                    let! hasRow = reader.ReadAsync(cancellationToken)

                    if not hasRow then
                        return false
                    else
                        let status = reader.GetString(0)
                        let chargeId = if reader.IsDBNull(1) then None else Some(reader.GetString(1))
                        let storedAmountStars = reader.GetInt32(2)
                        let storedCurrency = reader.GetString(3)
                        return
                            status = "Paid"
                            && chargeId = Some telegramPaymentChargeId
                            && storedAmountStars = amountStars
                            && storedCurrency = currency
            with ex ->
                return! Error(databaseError "PaymentRepository.markDonationPaid" ex)
        }
