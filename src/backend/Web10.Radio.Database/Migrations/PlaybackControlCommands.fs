namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607110002L, "Add ordered durable playback control commands")>]
type PlaybackControlCommands() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
ALTER TABLE "PlaybackQueue"
    ALTER COLUMN "Priority" TYPE bigint USING "Priority"::bigint;

CREATE TABLE "PlaybackControlCommands" (
    "Id" uuid PRIMARY KEY,
    "Generation" bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
    "Action" text NOT NULL,
    "QueueItemId" uuid NOT NULL REFERENCES "PlaybackQueue"("Id"),
    "ClaimOwner" uuid NOT NULL,
    "ClaimAttempt" integer NOT NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PlaybackControlCommands_Action_check"
        CHECK ("Action" IN ('Skip', 'Restart')),
    CONSTRAINT "PlaybackControlCommands_ClaimAttempt_check"
        CHECK ("ClaimAttempt" > 0)
);

CREATE INDEX "IX_PlaybackControlCommands_Active_Generation"
    ON "PlaybackControlCommands" ("Generation" ASC)
    WHERE "IsDeleted" = false;
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
DROP INDEX IF EXISTS "IX_PlaybackControlCommands_Active_Generation";
DROP TABLE IF EXISTS "PlaybackControlCommands";

ALTER TABLE "PlaybackQueue"
    ALTER COLUMN "Priority" TYPE integer USING "Priority"::integer;
            """.Trim()
        )
        |> ignore
