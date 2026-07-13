namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607130001L, "Add admin-configurable storage cache settings")>]
type AddStorageCacheSettings() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
CREATE TABLE "StorageSettings" (
    "Id" uuid PRIMARY KEY,
    "SingletonKey" text NOT NULL DEFAULT 'primary',
    "S3CacheMaxBytes" bigint NOT NULL,
    "PresignTtlSeconds" integer NOT NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_StorageSettings_S3CacheMaxBytes_Positive" CHECK ("S3CacheMaxBytes" > 0),
    CONSTRAINT "CK_StorageSettings_PresignTtlSeconds_Positive" CHECK ("PresignTtlSeconds" > 0),
    CONSTRAINT "CK_StorageSettings_SingletonKey_Primary" CHECK ("SingletonKey" = 'primary')
);

CREATE UNIQUE INDEX "UX_StorageSettings_Active_Singleton"
    ON "StorageSettings" ("SingletonKey")
    WHERE "IsDeleted" = false AND "SingletonKey" = 'primary';
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
DROP INDEX IF EXISTS "UX_StorageSettings_Active_Singleton";
DROP TABLE IF EXISTS "StorageSettings";
            """.Trim()
        )
        |> ignore
