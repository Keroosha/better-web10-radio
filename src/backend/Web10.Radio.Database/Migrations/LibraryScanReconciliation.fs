namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607140001L, "Add scan-generation marker for library reconciliation")>]
type AddLibraryScanReconciliation() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
ALTER TABLE "TrackFiles"
    ADD COLUMN IF NOT EXISTS "LastSeenScanJobId" uuid NULL;

CREATE INDEX IF NOT EXISTS "IX_TrackFiles_Active_StorageBackend_LastSeenScanJob"
    ON "TrackFiles" ("StorageBackendId", "LastSeenScanJobId")
    WHERE "IsDeleted" = false;
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
DROP INDEX IF EXISTS "IX_TrackFiles_Active_StorageBackend_LastSeenScanJob";

ALTER TABLE "TrackFiles"
    DROP COLUMN IF EXISTS "LastSeenScanJobId";
            """.Trim()
        )
        |> ignore
