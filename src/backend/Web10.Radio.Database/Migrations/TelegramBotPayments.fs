namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607100003L, "Add Telegram bot payment constraints and pg_trgm track search")>]
type AddTelegramBotPayments() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
CREATE EXTENSION IF NOT EXISTS pg_trgm;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM "Payments"
        WHERE "IsDeleted" = false
        GROUP BY "TelegramInvoicePayload"
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Duplicate active TelegramInvoicePayload prevents B4 migration.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM "Payments"
        WHERE "IsDeleted" = false
          AND "PurposeEntityId" IS NOT NULL
        GROUP BY "Purpose", "PurposeEntityId"
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Duplicate active payment purpose entity prevents B4 migration.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM "PlaybackQueue"
        WHERE "IsDeleted" = false
          AND "TrackRequestId" IS NOT NULL
        GROUP BY "TrackRequestId"
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Duplicate active playback TrackRequestId prevents B4 migration.';
    END IF;
END
$$;

CREATE INDEX "IX_Tracks_Active_Title_Trgm"
    ON "Tracks" USING gin (lower("Title") gin_trgm_ops)
    WHERE "IsDeleted" = false;

CREATE INDEX "IX_Tracks_Active_ArtistTitle_Trgm"
    ON "Tracks" USING gin (lower("Artist" || ' — ' || "Title") gin_trgm_ops)
    WHERE "IsDeleted" = false;

CREATE UNIQUE INDEX "UX_Payments_Active_InvoicePayload"
    ON "Payments" ("TelegramInvoicePayload")
    WHERE "IsDeleted" = false;

CREATE UNIQUE INDEX "UX_Payments_Active_PurposeEntity"
    ON "Payments" ("Purpose", "PurposeEntityId")
    WHERE "IsDeleted" = false
      AND "PurposeEntityId" IS NOT NULL;

CREATE UNIQUE INDEX "UX_PlaybackQueue_Active_TrackRequest"
    ON "PlaybackQueue" ("TrackRequestId")
    WHERE "IsDeleted" = false
      AND "TrackRequestId" IS NOT NULL;
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
DROP INDEX IF EXISTS "UX_PlaybackQueue_Active_TrackRequest";
DROP INDEX IF EXISTS "UX_Payments_Active_PurposeEntity";
DROP INDEX IF EXISTS "UX_Payments_Active_InvoicePayload";
DROP INDEX IF EXISTS "IX_Tracks_Active_ArtistTitle_Trgm";
DROP INDEX IF EXISTS "IX_Tracks_Active_Title_Trgm";
            """.Trim()
        )
        |> ignore
