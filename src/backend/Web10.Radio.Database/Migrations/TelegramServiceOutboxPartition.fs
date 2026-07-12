namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607110004L, "Partition outbox dispatch by service audience")>]
type TelegramServiceOutboxPartition() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
ALTER TABLE "OutboxEvents"
    ADD COLUMN "Audience" text NULL;

UPDATE "OutboxEvents"
SET "Audience" = CASE
    WHEN "EventType" IN (
        'TrackRequested',
        'TrackRequestMatched',
        'SayMessageSubmitted',
        'SayMessageModerated',
        'TelegramCommandReceived',
        'TelegramCallbackReceived',
        'DonationInvoiceCreated',
        'DonationPaid',
        'PaymentRefunded'
    ) THEN 'Telegram'
    ELSE 'Api'
END;

ALTER TABLE "OutboxEvents"
    ALTER COLUMN "Audience" SET NOT NULL,
    ADD CONSTRAINT "OutboxEvents_Audience_check"
        CHECK ("Audience" IN ('Api', 'Telegram'));

DROP INDEX IF EXISTS "IX_OutboxEvents_Active_GlobalOrder";

CREATE INDEX "IX_OutboxEvents_Active_GlobalOrder"
    ON "OutboxEvents" ("Audience", "OccurredAtUtc", "CreatedAtUtc", "Id")
    WHERE "IsDeleted" = false
      AND "Status" <> 'Processed';
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
DROP INDEX IF EXISTS "IX_OutboxEvents_Active_GlobalOrder";

ALTER TABLE "OutboxEvents"
    DROP CONSTRAINT IF EXISTS "OutboxEvents_Audience_check",
    DROP COLUMN IF EXISTS "Audience";

CREATE INDEX "IX_OutboxEvents_Active_GlobalOrder"
    ON "OutboxEvents" ("OccurredAtUtc", "CreatedAtUtc", "Id")
    WHERE "IsDeleted" = false
      AND "Status" <> 'Processed';
            """.Trim()
        )
        |> ignore
