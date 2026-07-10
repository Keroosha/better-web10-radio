namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607100004L, "Add frontend blocker schema invariants")>]
type AddFrontendBlockerSchema() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
ALTER TABLE "LibraryScanJobs"
    ADD COLUMN "DiscoveredCount" integer NOT NULL DEFAULT 0,
    ADD CONSTRAINT "CK_LibraryScanJobs_DiscoveredCount_NonNegative" CHECK ("DiscoveredCount" >= 0);

ALTER TABLE "Payments"
    ADD COLUMN "PayerDisplayName" text NULL;

WITH ranked_active_jobs AS (
    SELECT "Id",
           row_number() OVER (
               PARTITION BY "StorageBackendId"
               ORDER BY "RequestedAtUtc" ASC, "CreatedAtUtc" ASC, "Id" ASC
           ) AS duplicate_rank
    FROM "LibraryScanJobs"
    WHERE "IsDeleted" = false
      AND "Status" IN ('Queued', 'Running')
)
UPDATE "LibraryScanJobs" AS job
SET "Status" = 'Failed',
    "FailureReason" = 'superseded by migration',
    "UpdatedAtUtc" = now()
FROM ranked_active_jobs
WHERE job."Id" = ranked_active_jobs."Id"
  AND ranked_active_jobs.duplicate_rank > 1;

CREATE UNIQUE INDEX "UX_LibraryScanJobs_Active_DefaultBackend"
    ON "LibraryScanJobs" ((1))
    WHERE "IsDeleted" = false
      AND "StorageBackendId" IS NULL
      AND "Status" IN ('Queued', 'Running');

CREATE UNIQUE INDEX "UX_LibraryScanJobs_Active_StorageBackend"
    ON "LibraryScanJobs" ("StorageBackendId")
    WHERE "IsDeleted" = false
      AND "StorageBackendId" IS NOT NULL
      AND "Status" IN ('Queued', 'Running');

CREATE TABLE "AdminUsers" (
    "Id" uuid PRIMARY KEY,
    "Username" text NOT NULL,
    "NormalizedUsername" text NOT NULL,
    "PasswordHash" text NOT NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX "UX_AdminUsers_Active_NormalizedUsername"
    ON "AdminUsers" ("NormalizedUsername")
    WHERE "IsDeleted" = false;

CREATE TABLE "AdminSessions" (
    "Id" uuid PRIMARY KEY,
    "UserId" uuid NOT NULL REFERENCES "AdminUsers"("Id"),
    "TokenHash" bytea NOT NULL,
    "CsrfToken" text NOT NULL,
    "ExpiresAtUtc" timestamptz NOT NULL,
    "RevokedAtUtc" timestamptz NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX "UX_AdminSessions_Active_TokenHash"
    ON "AdminSessions" ("TokenHash")
    WHERE "IsDeleted" = false
      AND "RevokedAtUtc" IS NULL;

CREATE INDEX "IX_AdminSessions_Active_User_ExpiresAtUtc"
    ON "AdminSessions" ("UserId", "ExpiresAtUtc")
    WHERE "IsDeleted" = false
      AND "RevokedAtUtc" IS NULL;

WITH ranked_active_playlists AS (
    SELECT "Id",
           row_number() OVER (
               ORDER BY "CreatedAtUtc" ASC, "Id" ASC
           ) AS duplicate_rank
    FROM "Playlists"
    WHERE "IsDeleted" = false
      AND "IsActive" = true
)
UPDATE "Playlists" AS playlist
SET "IsActive" = false,
    "UpdatedAtUtc" = now()
FROM ranked_active_playlists
WHERE playlist."Id" = ranked_active_playlists."Id"
  AND ranked_active_playlists.duplicate_rank > 1;

CREATE UNIQUE INDEX "UX_Playlists_Active_Singleton"
    ON "Playlists" ("IsActive")
    WHERE "IsDeleted" = false
      AND "IsActive" = true;

WITH ranked_active_goals AS (
    SELECT "Id",
           row_number() OVER (
               ORDER BY "UpdatedAtUtc" DESC, "CreatedAtUtc" DESC, "Id" DESC
           ) AS duplicate_rank
    FROM "DonationGoals"
    WHERE "IsDeleted" = false
      AND "IsActive" = true
)
UPDATE "DonationGoals" AS goal
SET "IsActive" = false,
    "UpdatedAtUtc" = now()
FROM ranked_active_goals
WHERE goal."Id" = ranked_active_goals."Id"
  AND ranked_active_goals.duplicate_rank > 1;

CREATE UNIQUE INDEX "UX_DonationGoals_Active_Singleton"
    ON "DonationGoals" ("IsActive")
    WHERE "IsDeleted" = false
      AND "IsActive" = true;

CREATE TABLE "StreamNodeControlState" (
    "Id" uuid PRIMARY KEY,
    "SingletonKey" text NOT NULL DEFAULT 'primary',
    "DesiredState" text NOT NULL,
    "RestartGeneration" integer NOT NULL DEFAULT 0,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_StreamNodeControlState_DesiredState"
        CHECK ("DesiredState" IN ('Running', 'Stopped')),
    CONSTRAINT "CK_StreamNodeControlState_RestartGeneration_NonNegative"
        CHECK ("RestartGeneration" >= 0),
    CONSTRAINT "CK_StreamNodeControlState_SingletonKey_Primary"
        CHECK ("SingletonKey" = 'primary')
);

CREATE UNIQUE INDEX "UX_StreamNodeControlState_Active_Singleton"
    ON "StreamNodeControlState" ("SingletonKey")
    WHERE "IsDeleted" = false
      AND "SingletonKey" = 'primary';
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
DROP INDEX IF EXISTS "UX_StreamNodeControlState_Active_Singleton";
DROP TABLE IF EXISTS "StreamNodeControlState";

DROP INDEX IF EXISTS "UX_DonationGoals_Active_Singleton";
DROP INDEX IF EXISTS "UX_Playlists_Active_Singleton";

DROP INDEX IF EXISTS "IX_AdminSessions_Active_User_ExpiresAtUtc";
DROP INDEX IF EXISTS "UX_AdminSessions_Active_TokenHash";
DROP TABLE IF EXISTS "AdminSessions";
DROP INDEX IF EXISTS "UX_AdminUsers_Active_NormalizedUsername";
DROP TABLE IF EXISTS "AdminUsers";

DROP INDEX IF EXISTS "UX_LibraryScanJobs_Active_StorageBackend";
DROP INDEX IF EXISTS "UX_LibraryScanJobs_Active_DefaultBackend";

ALTER TABLE "Payments"
    DROP COLUMN IF EXISTS "PayerDisplayName";

ALTER TABLE "LibraryScanJobs"
    DROP CONSTRAINT IF EXISTS "CK_LibraryScanJobs_DiscoveredCount_NonNegative",
    DROP COLUMN IF EXISTS "DiscoveredCount";
            """.Trim()
        )
        |> ignore
