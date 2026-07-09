namespace Web10.Radio.Tests

open System
open System.Threading
open Npgsql
open NUnit.Framework
open Web10.Radio.API
open Web10.Radio.Database.Repositories

module PaymentRepositoryTests =
    let private newId () =
        (UuidV7IdGenerator() :> IIdGenerator).NewId()

    [<Test>]
    let ``markDonationPaid persists paid state idempotently and rejects different charge id`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let paymentId = newId ()
                let createdAtUtc = DateTimeOffset(2026, 7, 8, 13, 0, 0, TimeSpan.Zero)
                let paidAtUtc = createdAtUtc.AddMinutes(1.0)
                let chargeId = "telegram-charge-1"

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "Payments" (
    "Id", "Purpose", "AmountStars", "TelegramInvoicePayload", "Status", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted"
)
VALUES (@PaymentId, 'Donation', 42, 'invoice-payload', 'InvoiceCreated', @CreatedAtUtc, @CreatedAtUtc, false);""",
                        connection
                    )

                insertCommand.Parameters.AddWithValue("PaymentId", paymentId) |> ignore
                insertCommand.Parameters.AddWithValue("CreatedAtUtc", createdAtUtc) |> ignore
                let! _ = insertCommand.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! firstResult = PaymentRepository.markDonationPaid dataSource paymentId chargeId 42 "XTR" paidAtUtc CancellationToken.None
                let! sameChargeResult = PaymentRepository.markDonationPaid dataSource paymentId chargeId 42 "XTR" paidAtUtc CancellationToken.None
                let! differentChargeResult = PaymentRepository.markDonationPaid dataSource paymentId "telegram-charge-2" 42 "XTR" paidAtUtc CancellationToken.None

                match firstResult with
                | Ok true -> ()
                | actual -> Assert.Fail(sprintf "Expected first markDonationPaid call to succeed, but got %A." actual)

                match sameChargeResult with
                | Ok true -> ()
                | actual -> Assert.Fail(sprintf "Expected same charge id to be idempotent, but got %A." actual)

                match differentChargeResult with
                | Ok false -> ()
                | actual -> Assert.Fail(sprintf "Expected different charge id to be rejected, but got %A." actual)

                use command =
                    new NpgsqlCommand(
                        """SELECT "Status", "TelegramPaymentChargeId", "PaidAtUtc", "UpdatedAtUtc", count(*) OVER ()
FROM "Payments"
WHERE "Id" = @PaymentId
  AND "IsDeleted" = false;""",
                        connection
                    )

                command.Parameters.AddWithValue("PaymentId", paymentId) |> ignore
                let! reader = command.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetString(0), Is.EqualTo("Paid"))
                Assert.That(reader.GetString(1), Is.EqualTo(chargeId))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(2), Is.EqualTo(paidAtUtc))
                Assert.That(reader.GetFieldValue<DateTimeOffset>(3), Is.EqualTo(paidAtUtc))
                Assert.That(reader.GetInt64(4), Is.EqualTo(1L))
            })
