namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607100001L, "Add durable workflow ownership, leases, fencing, and track-file uniqueness")>]
type AddDurableWorkflowFencing() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
ALTER TABLE "PlaybackQueue"
    ADD COLUMN "ClaimOwner" uuid NULL,
    ADD COLUMN "ClaimAttempt" integer NOT NULL DEFAULT 0,
    ADD COLUMN "ClaimLeaseExpiresAtUtc" timestamptz NULL,
    ADD CONSTRAINT "CK_PlaybackQueue_ClaimAttempt_NonNegative" CHECK ("ClaimAttempt" >= 0);

ALTER TABLE "OutboxEvents"
    ADD COLUMN "ClaimOwner" uuid NULL,
    ADD COLUMN "ClaimLeaseExpiresAtUtc" timestamptz NULL;

ALTER TABLE "LibraryScanJobs"
    ADD COLUMN "ClaimOwner" uuid NULL,
    ADD COLUMN "ClaimAttempt" integer NOT NULL DEFAULT 0,
    ADD COLUMN "ClaimLeaseExpiresAtUtc" timestamptz NULL,
    ADD CONSTRAINT "CK_LibraryScanJobs_ClaimAttempt_NonNegative" CHECK ("ClaimAttempt" >= 0);

UPDATE "PlaybackQueue"
SET "ClaimLeaseExpiresAtUtc" = '-infinity'::timestamptz
WHERE "IsDeleted" = false
  AND "Status" IN ('Claimed', 'Playing');

UPDATE "LibraryScanJobs"
SET "ClaimLeaseExpiresAtUtc" = '-infinity'::timestamptz
WHERE "IsDeleted" = false
  AND "Status" = 'Running';

UPDATE "OutboxEvents"
SET "Status" = 'Failed',
    "NextAttemptAtUtc" = now(),
    "UpdatedAtUtc" = now()
WHERE "IsDeleted" = false
  AND "Status" = 'Processing';

WITH ranked AS (
    SELECT "Id",
           row_number() OVER (
               PARTITION BY "StorageBackendId", "StoragePath"
               ORDER BY "CreatedAtUtc" ASC, "Id" ASC
           ) AS duplicate_rank
    FROM "TrackFiles"
    WHERE "IsDeleted" = false
)
UPDATE "TrackFiles" AS track_file
SET "IsDeleted" = true,
    "UpdatedAtUtc" = now()
FROM ranked
WHERE track_file."Id" = ranked."Id"
  AND ranked.duplicate_rank > 1;

CREATE UNIQUE INDEX "UX_TrackFiles_Active_Backend_StoragePath"
    ON "TrackFiles" ("StorageBackendId", "StoragePath")
    WHERE "IsDeleted" = false AND "StorageBackendId" IS NOT NULL;

CREATE UNIQUE INDEX "UX_TrackFiles_Active_NullBackend_StoragePath"
    ON "TrackFiles" ("StoragePath")
    WHERE "IsDeleted" = false AND "StorageBackendId" IS NULL;

CREATE INDEX "IX_PlaybackQueue_Active_ClaimLease"
    ON "PlaybackQueue" ("Status", "ClaimLeaseExpiresAtUtc")
    WHERE "IsDeleted" = false AND "Status" IN ('Claimed', 'Playing');

CREATE INDEX "IX_LibraryScanJobs_Active_ClaimLease"
    ON "LibraryScanJobs" ("Status", "ClaimLeaseExpiresAtUtc", "RequestedAtUtc")
    WHERE "IsDeleted" = false AND "Status" IN ('Queued', 'Running');

CREATE INDEX "IX_OutboxEvents_Active_GlobalOrder"
    ON "OutboxEvents" ("OccurredAtUtc", "CreatedAtUtc", "Id")
    WHERE "IsDeleted" = false AND "Status" <> 'Processed';
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
DROP INDEX IF EXISTS "IX_OutboxEvents_Active_GlobalOrder";
DROP INDEX IF EXISTS "IX_LibraryScanJobs_Active_ClaimLease";
DROP INDEX IF EXISTS "IX_PlaybackQueue_Active_ClaimLease";
DROP INDEX IF EXISTS "UX_TrackFiles_Active_NullBackend_StoragePath";
DROP INDEX IF EXISTS "UX_TrackFiles_Active_Backend_StoragePath";

ALTER TABLE "LibraryScanJobs"
    DROP CONSTRAINT IF EXISTS "CK_LibraryScanJobs_ClaimAttempt_NonNegative",
    DROP COLUMN IF EXISTS "ClaimLeaseExpiresAtUtc",
    DROP COLUMN IF EXISTS "ClaimAttempt",
    DROP COLUMN IF EXISTS "ClaimOwner";

ALTER TABLE "OutboxEvents"
    DROP COLUMN IF EXISTS "ClaimLeaseExpiresAtUtc",
    DROP COLUMN IF EXISTS "ClaimOwner";

ALTER TABLE "PlaybackQueue"
    DROP CONSTRAINT IF EXISTS "CK_PlaybackQueue_ClaimAttempt_NonNegative",
    DROP COLUMN IF EXISTS "ClaimLeaseExpiresAtUtc",
    DROP COLUMN IF EXISTS "ClaimAttempt",
    DROP COLUMN IF EXISTS "ClaimOwner";
            """.Trim()
        )
        |> ignore
