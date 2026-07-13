namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607130003L, "Add logical FLAC CUE track segments")>]
type AddFlacCueTracks() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
ALTER TABLE "TrackFiles"
    ADD COLUMN IF NOT EXISTS "CueSheetPath" text NULL,
    ADD COLUMN IF NOT EXISTS "CueTrackNumber" integer NULL,
    ADD COLUMN IF NOT EXISTS "CueStartMs" integer NULL,
    ADD COLUMN IF NOT EXISTS "CueDurationMs" integer NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'CK_TrackFiles_CueSegment_AllOrNone'
          AND conrelid = '"TrackFiles"'::regclass
    ) THEN
        ALTER TABLE "TrackFiles"
            ADD CONSTRAINT "CK_TrackFiles_CueSegment_AllOrNone"
                CHECK (
                    ("CueSheetPath" IS NULL
                     AND "CueTrackNumber" IS NULL
                     AND "CueStartMs" IS NULL
                     AND "CueDurationMs" IS NULL)
                    OR
                    ("CueSheetPath" IS NOT NULL
                     AND "CueTrackNumber" IS NOT NULL
                     AND "CueStartMs" IS NOT NULL
                     AND "CueDurationMs" IS NOT NULL)
                );
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'CK_TrackFiles_CueSegment_Values'
          AND conrelid = '"TrackFiles"'::regclass
    ) THEN
        ALTER TABLE "TrackFiles"
            ADD CONSTRAINT "CK_TrackFiles_CueSegment_Values"
                CHECK (
                    "CueSheetPath" IS NULL
                    OR (
                        btrim("CueSheetPath") <> ''
                        AND "CueTrackNumber" > 0
                        AND "CueStartMs" >= 0
                        AND "CueDurationMs" > 0
                    )
                );
    END IF;
END $$;

DROP INDEX IF EXISTS "UX_TrackFiles_Active_Backend_StoragePath_Cue";
DROP INDEX IF EXISTS "UX_TrackFiles_Active_NullBackend_StoragePath_Cue";
DROP INDEX IF EXISTS "UX_TrackFiles_Active_Backend_StoragePath";
DROP INDEX IF EXISTS "UX_TrackFiles_Active_NullBackend_StoragePath";

CREATE UNIQUE INDEX "UX_TrackFiles_Active_Backend_StoragePath_Cue"
    ON "TrackFiles" (
        "StorageBackendId",
        "StoragePath",
        COALESCE("CueSheetPath", ''),
        COALESCE("CueTrackNumber", 0)
    )
    WHERE "IsDeleted" = false AND "StorageBackendId" IS NOT NULL;

CREATE UNIQUE INDEX "UX_TrackFiles_Active_NullBackend_StoragePath_Cue"
    ON "TrackFiles" (
        "StoragePath",
        COALESCE("CueSheetPath", ''),
        COALESCE("CueTrackNumber", 0)
    )
    WHERE "IsDeleted" = false AND "StorageBackendId" IS NULL;

ALTER TABLE "Banners"
    DROP CONSTRAINT IF EXISTS "Banners_Type_check",
    ADD CONSTRAINT "Banners_Type_check"
        CHECK ("Type" IN ('nowplaying', 'donation', 'social', 'superchat', 'custom'));

INSERT INTO "Banners" ("Id", "Type", "Title", "Subtitle", "Text", "Style", "ScreenPosition", "Accent", "Enabled", "SortOrder", "RotationSeconds")
SELECT gen_random_uuid(), 'superchat', 'SUPER CHAT', NULL, NULL, 'aero', 'bottom-left', '#e0439a', true, COALESCE(MAX("SortOrder"), -1) + 1, NULL
FROM "Banners"
WHERE NOT EXISTS (
    SELECT 1
    FROM "Banners"
    WHERE "Type" = 'superchat' AND "IsDeleted" = false
);

ALTER TABLE "Tracks"
    DROP CONSTRAINT IF EXISTS "Tracks_MetadataSource_check",
    ADD CONSTRAINT "Tracks_MetadataSource_check"
        CHECK ("MetadataSource" IN ('Filename', 'Embedded', 'Manual', 'Cue'));
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
DROP INDEX IF EXISTS "UX_TrackFiles_Active_NullBackend_StoragePath_Cue";
DROP INDEX IF EXISTS "UX_TrackFiles_Active_Backend_StoragePath_Cue";

ALTER TABLE "TrackFiles"
    DROP CONSTRAINT IF EXISTS "CK_TrackFiles_CueSegment_Values",
    DROP CONSTRAINT IF EXISTS "CK_TrackFiles_CueSegment_AllOrNone",
    DROP COLUMN IF EXISTS "CueDurationMs",
    DROP COLUMN IF EXISTS "CueStartMs",
    DROP COLUMN IF EXISTS "CueTrackNumber",
    DROP COLUMN IF EXISTS "CueSheetPath";

CREATE UNIQUE INDEX "UX_TrackFiles_Active_Backend_StoragePath"
    ON "TrackFiles" ("StorageBackendId", "StoragePath")
    WHERE "IsDeleted" = false AND "StorageBackendId" IS NOT NULL;

CREATE UNIQUE INDEX "UX_TrackFiles_Active_NullBackend_StoragePath"
    ON "TrackFiles" ("StoragePath")
    WHERE "IsDeleted" = false AND "StorageBackendId" IS NULL;

UPDATE "Tracks"
SET "MetadataSource" = 'Filename'
WHERE "MetadataSource" = 'Cue';

ALTER TABLE "Tracks"
    DROP CONSTRAINT IF EXISTS "Tracks_MetadataSource_check",
    ADD CONSTRAINT "Tracks_MetadataSource_check"
        CHECK ("MetadataSource" IN ('Filename', 'Embedded', 'Manual'));
            """.Trim()
        )
        |> ignore
