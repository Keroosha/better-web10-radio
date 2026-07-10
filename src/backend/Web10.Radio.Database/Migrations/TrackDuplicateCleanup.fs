namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607100002L, "Merge orphan tracks left by active track-file deduplication")>]
type MergeDuplicateTrackOrphans() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
CREATE TEMPORARY TABLE "TrackDuplicateMergeMap" (
    "LoserTrackId" uuid PRIMARY KEY,
    "WinnerTrackId" uuid NOT NULL
) ON COMMIT DROP;

INSERT INTO "TrackDuplicateMergeMap" ("LoserTrackId", "WinnerTrackId")
SELECT loser_file."TrackId",
       (array_agg(DISTINCT winner_file."TrackId" ORDER BY winner_file."TrackId"))[1]
FROM "TrackFiles" AS loser_file
INNER JOIN "Tracks" AS loser_track
    ON loser_track."Id" = loser_file."TrackId"
   AND loser_track."IsDeleted" = false
INNER JOIN "TrackFiles" AS winner_file
    ON winner_file."StorageBackendId" IS NOT DISTINCT FROM loser_file."StorageBackendId"
   AND winner_file."StoragePath" = loser_file."StoragePath"
   AND winner_file."IsDeleted" = false
   AND winner_file."TrackId" <> loser_file."TrackId"
INNER JOIN "Tracks" AS winner_track
    ON winner_track."Id" = winner_file."TrackId"
   AND winner_track."IsDeleted" = false
WHERE loser_file."IsDeleted" = true
  AND NOT EXISTS (
      SELECT 1
      FROM "TrackFiles" AS active_loser_file
      WHERE active_loser_file."TrackId" = loser_file."TrackId"
        AND active_loser_file."IsDeleted" = false
  )
GROUP BY loser_file."TrackId"
HAVING count(DISTINCT winner_file."TrackId") = 1;

UPDATE "TrackLinks" AS loser_link
SET "IsDeleted" = true,
    "UpdatedAtUtc" = now()
FROM "TrackDuplicateMergeMap" AS merge_map
WHERE loser_link."TrackId" = merge_map."LoserTrackId"
  AND loser_link."IsDeleted" = false
  AND EXISTS (
      SELECT 1
      FROM "TrackLinks" AS winner_link
      WHERE winner_link."TrackId" = merge_map."WinnerTrackId"
        AND winner_link."Url" = loser_link."Url"
        AND winner_link."IsDeleted" = false
  );

WITH ranked_loser_links AS (
    SELECT loser_link."Id",
           row_number() OVER (
               PARTITION BY merge_map."WinnerTrackId", loser_link."Url"
               ORDER BY loser_link."CreatedAtUtc" ASC, loser_link."Id" ASC
           ) AS duplicate_rank
    FROM "TrackLinks" AS loser_link
    INNER JOIN "TrackDuplicateMergeMap" AS merge_map
        ON merge_map."LoserTrackId" = loser_link."TrackId"
    WHERE loser_link."IsDeleted" = false
)
UPDATE "TrackLinks" AS loser_link
SET "IsDeleted" = true,
    "UpdatedAtUtc" = now()
FROM ranked_loser_links
WHERE loser_link."Id" = ranked_loser_links."Id"
  AND ranked_loser_links.duplicate_rank > 1;

UPDATE "TrackLinks" AS track_link
SET "TrackId" = merge_map."WinnerTrackId",
    "UpdatedAtUtc" = now()
FROM "TrackDuplicateMergeMap" AS merge_map
WHERE track_link."TrackId" = merge_map."LoserTrackId";

UPDATE "PlaylistItems" AS playlist_item
SET "TrackId" = merge_map."WinnerTrackId",
    "UpdatedAtUtc" = now()
FROM "TrackDuplicateMergeMap" AS merge_map
WHERE playlist_item."TrackId" = merge_map."LoserTrackId";

UPDATE "PlaybackQueue" AS queue_item
SET "TrackId" = merge_map."WinnerTrackId",
    "UpdatedAtUtc" = now()
FROM "TrackDuplicateMergeMap" AS merge_map
WHERE queue_item."TrackId" = merge_map."LoserTrackId";

UPDATE "TrackRequests" AS track_request
SET "MatchedTrackId" = merge_map."WinnerTrackId",
    "UpdatedAtUtc" = now()
FROM "TrackDuplicateMergeMap" AS merge_map
WHERE track_request."MatchedTrackId" = merge_map."LoserTrackId";

UPDATE "TrackFiles" AS track_file
SET "TrackId" = merge_map."WinnerTrackId",
    "UpdatedAtUtc" = now()
FROM "TrackDuplicateMergeMap" AS merge_map
WHERE track_file."TrackId" = merge_map."LoserTrackId";

UPDATE "Tracks" AS loser_track
SET "IsDeleted" = true,
    "UpdatedAtUtc" = now()
FROM "TrackDuplicateMergeMap" AS merge_map
WHERE loser_track."Id" = merge_map."LoserTrackId"
  AND loser_track."IsDeleted" = false;
            """.Trim()
        )
        |> ignore

    override this.Down() =
        // Reference rewrites and soft-deletion are intentionally irreversible data repair.
        this.Execute.Sql("SELECT 1;") |> ignore
