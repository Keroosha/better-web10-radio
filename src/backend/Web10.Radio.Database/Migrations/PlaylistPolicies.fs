namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607110003L, "Add playlist policies, schedules, and scheduler state")>]
type PlaylistPolicies() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
DROP INDEX IF EXISTS "UX_Playlists_Active_Singleton";

ALTER TABLE "Playlists"
    ADD COLUMN "Type" text NOT NULL DEFAULT 'General',
    ADD COLUMN "Source" text NOT NULL DEFAULT 'Manual',
    ADD COLUMN "Order" text NOT NULL DEFAULT 'Sequential',
    ADD COLUMN "Weight" smallint NOT NULL DEFAULT 3,
    ADD COLUMN "IsJingle" boolean NOT NULL DEFAULT false,
    ADD COLUMN "Interrupt" boolean NOT NULL DEFAULT false,
    ADD COLUMN "AvoidDuplicates" boolean NOT NULL DEFAULT true,
    ADD COLUMN "PlayEverySongs" integer NULL,
    ADD COLUMN "PlayEveryMinutes" integer NULL,
    ADD COLUMN "PlayAtMinute" integer NULL,
    ADD COLUMN "IsSystem" boolean NOT NULL DEFAULT false,
    ADD CONSTRAINT "Playlists_Type_check"
        CHECK ("Type" IN ('General', 'OncePerSongs', 'OncePerMinutes', 'OncePerHour')),
    ADD CONSTRAINT "Playlists_Source_check"
        CHECK ("Source" IN ('Manual', 'AllStorage')),
    ADD CONSTRAINT "Playlists_Order_check"
        CHECK ("Order" IN ('Sequential', 'Shuffle', 'Random')),
    ADD CONSTRAINT "Playlists_Weight_check"
        CHECK ("Weight" BETWEEN 1 AND 25),
    ADD CONSTRAINT "Playlists_Cadence_check"
        CHECK (
            ("Type" = 'General'
             AND "PlayEverySongs" IS NULL
             AND "PlayEveryMinutes" IS NULL
             AND "PlayAtMinute" IS NULL)
            OR
            ("Type" = 'OncePerSongs'
             AND "PlayEverySongs" IS NOT NULL
             AND "PlayEverySongs" BETWEEN 1 AND 1000
             AND "PlayEveryMinutes" IS NULL
             AND "PlayAtMinute" IS NULL)
            OR
            ("Type" = 'OncePerMinutes'
             AND "PlayEverySongs" IS NULL
             AND "PlayEveryMinutes" IS NOT NULL
             AND "PlayEveryMinutes" BETWEEN 1 AND 10080
             AND "PlayAtMinute" IS NULL)
            OR
            ("Type" = 'OncePerHour'
             AND "PlayEverySongs" IS NULL
             AND "PlayEveryMinutes" IS NULL
             AND "PlayAtMinute" IS NOT NULL
             AND "PlayAtMinute" BETWEEN 0 AND 59)
        );

ALTER TABLE "PlaybackQueue"
    ADD COLUMN "PlaylistId" uuid NULL REFERENCES "Playlists"("Id");

UPDATE "PlaybackQueue" AS queue_item
SET "PlaylistId" = playlist_item."PlaylistId"
FROM "PlaylistItems" AS playlist_item
WHERE queue_item."PlaylistItemId" = playlist_item."Id"
  AND queue_item."PlaylistId" IS NULL;
-- Legacy v0 queue rows could use playlist/jingle without a PlaylistItemId.
-- Preserve those playable rows as fallback items before enforcing the new ownership invariant.
UPDATE "PlaybackQueue"
SET "Source" = 'fallback'
WHERE "Source" IN ('playlist', 'jingle')
  AND "PlaylistId" IS NULL;
UPDATE "PlaybackQueue"
SET "PlaylistId" = NULL
WHERE "Source" IN ('request', 'admin', 'fallback');

ALTER TABLE "PlaybackQueue"
    DROP CONSTRAINT IF EXISTS "PlaybackQueue_Source_check",
    ADD CONSTRAINT "PlaybackQueue_Source_check"
        CHECK ("Source" IN ('playlist', 'jingle', 'request', 'admin', 'fallback')),
    ADD CONSTRAINT "PlaybackQueue_PlaylistId_Source_check"
        CHECK (
            (("Source" IN ('playlist', 'jingle')) AND "PlaylistId" IS NOT NULL)
            OR
            (("Source" IN ('admin', 'request', 'fallback')) AND "PlaylistId" IS NULL)
        );

CREATE INDEX "IX_PlaybackQueue_Active_PlaylistId_Status"
    ON "PlaybackQueue" ("PlaylistId", "Status")
    WHERE "IsDeleted" = false;

CREATE TABLE "PlaylistSchedules" (
    "Id" uuid PRIMARY KEY,
    "PlaylistId" uuid NOT NULL REFERENCES "Playlists"("Id"),
    "DaysOfWeek" smallint[] NOT NULL DEFAULT '{}'::smallint[],
    "StartTime" time NOT NULL,
    "EndTime" time NOT NULL,
    "StartDate" date NULL,
    "EndDate" date NULL,
    "TimeZoneId" varchar(100) NOT NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PlaylistSchedules_DaysOfWeek_check"
        CHECK ("DaysOfWeek" <@ ARRAY[1, 2, 3, 4, 5, 6, 7]::smallint[]),
    CONSTRAINT "PlaylistSchedules_DateRange_check"
        CHECK (("StartDate" IS NULL AND "EndDate" IS NULL)
               OR ("StartDate" IS NOT NULL AND "EndDate" IS NOT NULL AND "StartDate" <= "EndDate")),
    CONSTRAINT "PlaylistSchedules_TimeZoneId_check"
        CHECK (btrim("TimeZoneId") <> '')
);

CREATE INDEX "IX_PlaylistSchedules_Active_PlaylistId"
    ON "PlaylistSchedules" ("PlaylistId")
    WHERE "IsDeleted" = false;

CREATE TABLE "PlaylistSchedulerState" (
    "PlaylistId" uuid PRIMARY KEY REFERENCES "Playlists"("Id"),
    "SongsSinceLast" integer NOT NULL DEFAULT 0,
    "LastQueuedAtUtc" timestamptz NULL,
    "LastPlayedAtUtc" timestamptz NULL,
    "Cursor" bigint NOT NULL DEFAULT 0,
    "ShuffleSeed" uuid NOT NULL,
    "SelectionCredit" bigint NOT NULL DEFAULT 0,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PlaylistSchedulerState_SongsSinceLast_check"
        CHECK ("SongsSinceLast" >= 0),
    CONSTRAINT "PlaylistSchedulerState_Cursor_check"
        CHECK ("Cursor" >= 0)
);

CREATE UNIQUE INDEX "UX_Playlists_Active_System_AllStorage"
    ON "Playlists" ((1))
    WHERE "IsDeleted" = false
      AND "IsActive" = true
      AND "IsSystem" = true
      AND "Source" = 'AllStorage';
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
DROP INDEX IF EXISTS "UX_Playlists_Active_System_AllStorage";

DROP INDEX IF EXISTS "IX_PlaylistSchedules_Active_PlaylistId";
DROP TABLE IF EXISTS "PlaylistSchedules";
DROP TABLE IF EXISTS "PlaylistSchedulerState";

DROP INDEX IF EXISTS "IX_PlaybackQueue_Active_PlaylistId_Status";
UPDATE "PlaybackQueue"
SET "Source" = 'playlist'
WHERE "Source" = 'jingle';

ALTER TABLE "PlaybackQueue"
    DROP CONSTRAINT IF EXISTS "PlaybackQueue_PlaylistId_Source_check",
    DROP CONSTRAINT IF EXISTS "PlaybackQueue_Source_check",
    DROP COLUMN IF EXISTS "PlaylistId";

ALTER TABLE "PlaybackQueue"
    ADD CONSTRAINT "PlaybackQueue_Source_check"
        CHECK ("Source" IN ('playlist', 'request', 'admin', 'fallback'));

ALTER TABLE "Playlists"
    DROP CONSTRAINT IF EXISTS "Playlists_Cadence_check",
    DROP CONSTRAINT IF EXISTS "Playlists_Weight_check",
    DROP CONSTRAINT IF EXISTS "Playlists_Order_check",
    DROP CONSTRAINT IF EXISTS "Playlists_Source_check",
    DROP CONSTRAINT IF EXISTS "Playlists_Type_check",
    DROP COLUMN IF EXISTS "IsSystem",
    DROP COLUMN IF EXISTS "PlayAtMinute",
    DROP COLUMN IF EXISTS "PlayEveryMinutes",
    DROP COLUMN IF EXISTS "PlayEverySongs",
    DROP COLUMN IF EXISTS "AvoidDuplicates",
    DROP COLUMN IF EXISTS "Interrupt",
    DROP COLUMN IF EXISTS "IsJingle",
    DROP COLUMN IF EXISTS "Weight",
    DROP COLUMN IF EXISTS "Order",
    DROP COLUMN IF EXISTS "Source",
    DROP COLUMN IF EXISTS "Type";

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
            """.Trim()
        )
        |> ignore
