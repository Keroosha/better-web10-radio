namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607120001L, "Allow paused stream-node desired state")>]
type AllowPausedStreamNodeState() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
ALTER TABLE "StreamNodeControlState"
    DROP CONSTRAINT "CK_StreamNodeControlState_DesiredState";

ALTER TABLE "StreamNodeControlState"
    ADD CONSTRAINT "CK_StreamNodeControlState_DesiredState"
        CHECK ("DesiredState" IN ('Running', 'Paused', 'Stopped'));
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
UPDATE "StreamNodeControlState"
    SET "DesiredState" = 'Stopped'
    WHERE "DesiredState" = 'Paused';

ALTER TABLE "StreamNodeControlState"
    DROP CONSTRAINT "CK_StreamNodeControlState_DesiredState";

ALTER TABLE "StreamNodeControlState"
    ADD CONSTRAINT "CK_StreamNodeControlState_DesiredState"
        CHECK ("DesiredState" IN ('Running', 'Stopped'));
            """.Trim()
        )
        |> ignore

[<Migration(202607120002L, "Add overlay banners and default seed")>]
type AddOverlayBanners() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
CREATE TABLE "Banners" (
    "Id" uuid PRIMARY KEY,
    "Type" text NOT NULL CHECK ("Type" IN ('nowplaying', 'donation', 'social', 'custom')),
    "Title" text NOT NULL,
    "Subtitle" text NULL,
    "Text" text NULL,
    "Style" text NOT NULL CHECK ("Style" IN ('aero', 'win9x')),
    "ScreenPosition" text NOT NULL CHECK ("ScreenPosition" IN ('top-left', 'top-center', 'top-right', 'bottom-left', 'bottom-center', 'bottom-right')),
    "Accent" text NULL,
    "Enabled" boolean NOT NULL DEFAULT true,
    "SortOrder" integer NOT NULL DEFAULT 0 CHECK ("SortOrder" >= 0),
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX "IX_Banners_Active_SortOrder"
    ON "Banners" ("SortOrder")
    WHERE "IsDeleted" = false;

INSERT INTO "Banners" ("Id", "Type", "Title", "Subtitle", "Text", "Style", "ScreenPosition", "Accent", "Enabled", "SortOrder")
VALUES
    (gen_random_uuid(), 'nowplaying', 'NOW PLAYING', '24/7', NULL, 'aero', 'top-center', '#e74c3c', true, 0),
    (gen_random_uuid(), 'donation', 'DONATION GOAL', NULL, NULL, 'aero', 'top-left', '#2ecc71', true, 1),
    (gen_random_uuid(), 'social', 'FOLLOW US', '@web1.radio', NULL, 'aero', 'bottom-right', '#c0392b', true, 2),
    (gen_random_uuid(), 'custom', 'РОЗЫГРЫШ', NULL, 'Розыгрыш подписки — напишите /join в чат', 'win9x', 'bottom-center', '#f39c12', false, 3);
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
DROP INDEX IF EXISTS "IX_Banners_Active_SortOrder";
DROP TABLE IF EXISTS "Banners";
            """.Trim()
        )
        |> ignore

[<Migration(202607120003L, "Add social banner link-rotation interval")>]
type AddBannerRotationSeconds() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
ALTER TABLE "Banners" ADD COLUMN "RotationSeconds" integer NULL;
UPDATE "Banners" SET "RotationSeconds" = 5 WHERE "Type" = 'social' AND "IsDeleted" = false;
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
ALTER TABLE "Banners" DROP COLUMN IF EXISTS "RotationSeconds";
            """.Trim()
        )
        |> ignore
