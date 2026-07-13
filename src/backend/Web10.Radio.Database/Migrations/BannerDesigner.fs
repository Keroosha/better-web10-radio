namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607130002L, "Add super chat banner type and default seed")>]
type AddSuperChatBanner() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
ALTER TABLE "Banners"
    DROP CONSTRAINT "Banners_Type_check";

ALTER TABLE "Banners"
    ADD CONSTRAINT "Banners_Type_check"
        CHECK ("Type" IN ('nowplaying', 'donation', 'social', 'superchat', 'custom'));

INSERT INTO "Banners" ("Id", "Type", "Title", "Subtitle", "Text", "Style", "ScreenPosition", "Accent", "Enabled", "SortOrder", "RotationSeconds")
SELECT gen_random_uuid(), 'superchat', 'SUPER CHAT', NULL, NULL, 'aero', 'bottom-left', '#e0439a', true, COALESCE(MAX("SortOrder"), -1) + 1, NULL
FROM "Banners";
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
DELETE FROM "Banners"
WHERE "Type" = 'superchat';

ALTER TABLE "Banners"
    DROP CONSTRAINT "Banners_Type_check";

ALTER TABLE "Banners"
    ADD CONSTRAINT "Banners_Type_check"
        CHECK ("Type" IN ('nowplaying', 'donation', 'social', 'custom'));
            """.Trim()
        )
        |> ignore
