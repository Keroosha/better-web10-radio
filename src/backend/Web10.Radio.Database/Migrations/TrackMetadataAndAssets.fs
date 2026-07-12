namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607110001L, "Add track metadata source and managed assets")>]
type TrackMetadataAndAssets() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
ALTER TABLE "Tracks"
    ADD COLUMN "MetadataSource" text NOT NULL DEFAULT 'Filename',
    ADD CONSTRAINT "Tracks_MetadataSource_check"
        CHECK ("MetadataSource" IN ('Filename', 'Embedded', 'Manual'));

CREATE TABLE "TrackAssets" (
    "Id" uuid PRIMARY KEY,
    "TrackId" uuid NOT NULL REFERENCES "Tracks"("Id"),
    "Kind" text NOT NULL,
    "Source" text NOT NULL,
    "CachePath" text NULL,
    "ExternalUrl" text NULL,
    "ContentType" text NULL,
    "SizeBytes" bigint NULL,
    "Sha256" text NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "TrackAssets_Kind_check"
        CHECK ("Kind" = 'Cover'),
    CONSTRAINT "TrackAssets_Source_check"
        CHECK ("Source" IN ('Embedded', 'Manual', 'LegacyExternal')),
    CONSTRAINT "TrackAssets_EmbeddedManual_check"
        CHECK (
            ("Source" IN ('Embedded', 'Manual')
             AND "CachePath" IS NOT NULL
             AND btrim("CachePath") <> ''
             AND "ExternalUrl" IS NULL
             AND "ContentType" IS NOT NULL
             AND "ContentType" IN ('image/jpeg', 'image/png', 'image/webp')
             AND "SizeBytes" IS NOT NULL
             AND "SizeBytes" BETWEEN 1 AND 10485760
             AND "Sha256" IS NOT NULL
             AND "Sha256" ~ '^[0-9a-f]{64}$')
            OR
            ("Source" = 'LegacyExternal'
             AND "ExternalUrl" IS NOT NULL
            AND ("ExternalUrl" ~ '^https?://[^[:space:]]+$'
                 OR (left("ExternalUrl", 1) = '/' AND left("ExternalUrl", 2) <> '//' AND "ExternalUrl" !~ '[[:space:]]'))
             AND "CachePath" IS NULL
             AND "ContentType" IS NULL
             AND "SizeBytes" IS NULL
             AND "Sha256" IS NULL)
        )
);

CREATE UNIQUE INDEX "UX_TrackAssets_Active_Track_Kind"
    ON "TrackAssets" ("TrackId", "Kind")
    WHERE "IsDeleted" = false;

INSERT INTO "TrackAssets" (
    "Id", "TrackId", "Kind", "Source", "ExternalUrl",
    "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc"
)
SELECT
    (
        lpad(to_hex((extract(epoch FROM clock_timestamp()) * 1000)::bigint), 12, '0')
        || '-' ||
        substr(lpad(to_hex((extract(epoch FROM clock_timestamp()) * 1000)::bigint), 12, '0'), 9, 4)
        || '-7' || substr(md5(random()::text || clock_timestamp()::text), 1, 3)
        || '-8' || substr(md5(random()::text || clock_timestamp()::text), 5, 3)
        || '-' || substr(md5(random()::text || clock_timestamp()::text), 8, 12)
    )::uuid,
    "Id",
    'Cover',
    'LegacyExternal',
    "CoverImageUrl",
    false,
    "CreatedAtUtc",
    now()
FROM "Tracks"
WHERE "CoverImageUrl" IS NOT NULL
  AND btrim("CoverImageUrl") <> '';

ALTER TABLE "Tracks" DROP COLUMN "CoverImageUrl";
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
ALTER TABLE "Tracks"
    ADD COLUMN "CoverImageUrl" text NULL;

UPDATE "Tracks" AS track
SET "CoverImageUrl" = asset."ExternalUrl"
FROM "TrackAssets" AS asset
WHERE asset."TrackId" = track."Id"
  AND asset."Kind" = 'Cover'
  AND asset."Source" = 'LegacyExternal'
  AND asset."IsDeleted" = false;

DROP INDEX IF EXISTS "UX_TrackAssets_Active_Track_Kind";
DROP TABLE IF EXISTS "TrackAssets";

ALTER TABLE "Tracks"
    DROP CONSTRAINT IF EXISTS "Tracks_MetadataSource_check",
    DROP COLUMN IF EXISTS "MetadataSource";
            """.Trim()
        )
        |> ignore
