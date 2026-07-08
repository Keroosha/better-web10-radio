namespace Web10.Radio.Database.Migrations

open FluentMigrator

[<Migration(202607080001L, "Create initial Web10.Radio schema")>]
type CreateInitialSchema() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            """
CREATE TABLE "Tracks" (
    "Id" uuid PRIMARY KEY,
    "Title" text NOT NULL,
    "Artist" text NOT NULL,
    "Album" text NULL,
    "DurationMs" integer NULL CHECK ("DurationMs" IS NULL OR "DurationMs" >= 0),
    "CoverImageUrl" text NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "StorageBackends" (
    "Id" uuid PRIMARY KEY,
    "Name" text NOT NULL,
    "Type" text NOT NULL CHECK ("Type" IN ('Local', 'S3')),
    "LocalRoot" text NULL,
    "S3Bucket" text NULL,
    "IsEnabled" boolean NOT NULL DEFAULT true,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "TrackLinks" (
    "Id" uuid PRIMARY KEY,
    "TrackId" uuid NOT NULL REFERENCES "Tracks"("Id"),
    "Kind" text NOT NULL CHECK ("Kind" IN ('bandcamp', 'soundcloud', 'youtube', 'artist', 'external')),
    "Url" text NOT NULL,
    "IsPrimary" boolean NOT NULL DEFAULT false,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "TrackFiles" (
    "Id" uuid PRIMARY KEY,
    "TrackId" uuid NOT NULL REFERENCES "Tracks"("Id"),
    "StorageBackendId" uuid NULL REFERENCES "StorageBackends"("Id"),
    "StoragePath" text NOT NULL,
    "CachePath" text NULL,
    "ContentType" text NULL,
    "SizeBytes" bigint NULL CHECK ("SizeBytes" IS NULL OR "SizeBytes" >= 0),
    "IsCached" boolean NOT NULL DEFAULT false,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "Playlists" (
    "Id" uuid PRIMARY KEY,
    "Name" text NOT NULL,
    "Description" text NULL,
    "IsActive" boolean NOT NULL DEFAULT true,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "PlaylistItems" (
    "Id" uuid PRIMARY KEY,
    "PlaylistId" uuid NOT NULL REFERENCES "Playlists"("Id"),
    "TrackId" uuid NOT NULL REFERENCES "Tracks"("Id"),
    "Position" integer NOT NULL CHECK ("Position" >= 0),
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "TrackRequests" (
    "Id" uuid PRIMARY KEY,
    "TelegramUserId" bigint NULL,
    "DisplayName" text NULL,
    "Query" text NOT NULL,
    "MatchedTrackId" uuid NULL REFERENCES "Tracks"("Id"),
    "Status" text NOT NULL CHECK ("Status" IN ('NeedsReview', 'Matched', 'Rejected', 'Queued', 'PaidPending', 'Paid')),
    "RequestedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "CorrelationId" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "PlaybackQueue" (
    "Id" uuid PRIMARY KEY,
    "TrackId" uuid NULL REFERENCES "Tracks"("Id"),
    "TrackRequestId" uuid NULL REFERENCES "TrackRequests"("Id"),
    "PlaylistItemId" uuid NULL REFERENCES "PlaylistItems"("Id"),
    "Source" text NOT NULL CHECK ("Source" IN ('playlist', 'request', 'admin', 'fallback')),
    "Status" text NOT NULL CHECK ("Status" IN ('Queued', 'Claimed', 'Playing', 'Played', 'Failed')),
    "Priority" integer NOT NULL DEFAULT 0,
    "RequestedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ClaimedAtUtc" timestamptz NULL,
    "StartedAtUtc" timestamptz NULL,
    "FinishedAtUtc" timestamptz NULL,
    "FailureReason" text NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "SayMessages" (
    "Id" uuid PRIMARY KEY,
    "TelegramUserId" bigint NULL,
    "DisplayName" text NOT NULL,
    "Text" text NOT NULL,
    "AmountStars" integer NOT NULL DEFAULT 0 CHECK ("AmountStars" >= 0),
    "Color" text NULL,
    "Status" text NOT NULL CHECK ("Status" IN ('PendingPayment', 'PaidPendingModeration', 'Approved', 'Rejected')),
    "SubmittedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "PaidAtUtc" timestamptz NULL,
    "ModeratedAtUtc" timestamptz NULL,
    "ModerationReason" text NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "Payments" (
    "Id" uuid PRIMARY KEY,
    "TelegramUserId" bigint NULL,
    "Purpose" text NOT NULL CHECK ("Purpose" IN ('Request', 'Say', 'Donation')),
    "PurposeEntityId" uuid NULL,
    "AmountStars" integer NOT NULL CHECK ("AmountStars" > 0),
    "Currency" text NOT NULL DEFAULT 'XTR' CHECK ("Currency" = 'XTR'),
    "ProviderToken" text NOT NULL DEFAULT '' CHECK ("ProviderToken" = ''),
    "TelegramInvoicePayload" text NOT NULL,
    "TelegramPaymentChargeId" text NULL,
    "Status" text NOT NULL CHECK ("Status" IN ('InvoiceCreated', 'PreCheckoutApproved', 'Paid', 'Refunded', 'Rejected')),
    "PaidAtUtc" timestamptz NULL,
    "RefundedAtUtc" timestamptz NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "DonationGoals" (
    "Id" uuid PRIMARY KEY,
    "Title" text NOT NULL,
    "GoalStars" integer NOT NULL CHECK ("GoalStars" > 0),
    "RaisedStars" integer NOT NULL DEFAULT 0 CHECK ("RaisedStars" >= 0),
    "IsActive" boolean NOT NULL DEFAULT true,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "SocialLinks" (
    "Id" uuid PRIMARY KEY,
    "Kind" text NOT NULL CHECK ("Kind" IN ('telegram', 'youtube', 'instagram', 'discord', 'external')),
    "Name" text NOT NULL,
    "Handle" text NULL,
    "Url" text NOT NULL,
    "Glyph" text NULL,
    "Color" text NULL,
    "QrImageUrl" text NULL,
    "IsFeatured" boolean NOT NULL DEFAULT false,
    "Position" integer NOT NULL DEFAULT 0 CHECK ("Position" >= 0),
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "LibraryScanJobs" (
    "Id" uuid PRIMARY KEY,
    "StorageBackendId" uuid NULL REFERENCES "StorageBackends"("Id"),
    "Status" text NOT NULL CHECK ("Status" IN ('Queued', 'Running', 'Completed', 'Failed')),
    "RequestedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "StartedAtUtc" timestamptz NULL,
    "FinishedAtUtc" timestamptz NULL,
    "FailureReason" text NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "StreamNodeHeartbeats" (
    "Id" uuid PRIMARY KEY,
    "Status" text NOT NULL CHECK ("Status" IN ('Starting', 'Live', 'Degraded', 'Restarting', 'Failed', 'Offline')),
    "HeartbeatAtUtc" timestamptz NOT NULL,
    "FailureReason" text NULL,
    "Metadata" jsonb NOT NULL DEFAULT '{}'::jsonb,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "OutboxEvents" (
    "Id" uuid PRIMARY KEY,
    "EventType" text NOT NULL,
    "OccurredAtUtc" timestamptz NOT NULL DEFAULT now(),
    "Producer" text NOT NULL,
    "CorrelationId" uuid NULL,
    "CausationId" uuid NULL,
    "Payload" jsonb NOT NULL,
    "Status" text NOT NULL DEFAULT 'Pending' CHECK ("Status" IN ('Pending', 'Processing', 'Processed', 'Failed')),
    "Attempts" integer NOT NULL DEFAULT 0 CHECK ("Attempts" >= 0),
    "NextAttemptAtUtc" timestamptz NULL,
    "ProcessedAtUtc" timestamptz NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE "TelegramUpdateInbox" (
    "Id" uuid PRIMARY KEY,
    "TelegramUpdateId" bigint NOT NULL,
    "EventType" text NOT NULL,
    "ReceivedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "CorrelationId" uuid NULL,
    "Payload" jsonb NOT NULL DEFAULT '{}'::jsonb,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX "IX_Tracks_Active_TitleArtist" ON "Tracks" ("Title", "Artist") WHERE "IsDeleted" = false;
CREATE INDEX "IX_StorageBackends_Active_Type" ON "StorageBackends" ("Type") WHERE "IsDeleted" = false;
CREATE INDEX "IX_TrackLinks_Active_TrackId" ON "TrackLinks" ("TrackId") WHERE "IsDeleted" = false;
CREATE UNIQUE INDEX "UX_TrackLinks_Active_TrackId_Url" ON "TrackLinks" ("TrackId", "Url") WHERE "IsDeleted" = false;
CREATE INDEX "IX_TrackFiles_Active_TrackId" ON "TrackFiles" ("TrackId") WHERE "IsDeleted" = false;
CREATE INDEX "IX_TrackFiles_Active_StorageBackendId" ON "TrackFiles" ("StorageBackendId") WHERE "IsDeleted" = false;
CREATE UNIQUE INDEX "UX_Playlists_Active_Name" ON "Playlists" ("Name") WHERE "IsDeleted" = false;
CREATE INDEX "IX_PlaylistItems_Active_PlaylistId" ON "PlaylistItems" ("PlaylistId", "Position") WHERE "IsDeleted" = false;
CREATE UNIQUE INDEX "UX_PlaylistItems_Active_PlaylistId_Position" ON "PlaylistItems" ("PlaylistId", "Position") WHERE "IsDeleted" = false;
CREATE INDEX "IX_TrackRequests_Active_Status_RequestedAtUtc" ON "TrackRequests" ("Status", "RequestedAtUtc") WHERE "IsDeleted" = false;
CREATE INDEX "IX_PlaybackQueue_Active_Claim" ON "PlaybackQueue" ("Priority" DESC, "RequestedAtUtc" ASC, "CreatedAtUtc" ASC) WHERE "IsDeleted" = false AND "Status" = 'Queued';
CREATE INDEX "IX_PlaybackQueue_Active_Status" ON "PlaybackQueue" ("Status", "UpdatedAtUtc") WHERE "IsDeleted" = false;
CREATE INDEX "IX_SayMessages_Active_Status_SubmittedAtUtc" ON "SayMessages" ("Status", "SubmittedAtUtc") WHERE "IsDeleted" = false;
CREATE UNIQUE INDEX "UX_Payments_Active_TelegramPaymentChargeId" ON "Payments" ("TelegramPaymentChargeId") WHERE "IsDeleted" = false AND "TelegramPaymentChargeId" IS NOT NULL;
CREATE INDEX "IX_Payments_Active_Status" ON "Payments" ("Status", "UpdatedAtUtc") WHERE "IsDeleted" = false;
CREATE INDEX "IX_DonationGoals_Active_IsActive" ON "DonationGoals" ("IsActive") WHERE "IsDeleted" = false;
CREATE INDEX "IX_SocialLinks_Active_Position" ON "SocialLinks" ("Position") WHERE "IsDeleted" = false;
CREATE INDEX "IX_SocialLinks_Active_Featured" ON "SocialLinks" ("IsFeatured") WHERE "IsDeleted" = false;
CREATE INDEX "IX_LibraryScanJobs_Active_Status_RequestedAtUtc" ON "LibraryScanJobs" ("Status", "RequestedAtUtc") WHERE "IsDeleted" = false;
CREATE INDEX "IX_StreamNodeHeartbeats_Active_HeartbeatAtUtc" ON "StreamNodeHeartbeats" ("HeartbeatAtUtc" DESC) WHERE "IsDeleted" = false;
CREATE INDEX "IX_OutboxEvents_Active_Status_NextAttemptAtUtc" ON "OutboxEvents" ("Status", "NextAttemptAtUtc", "OccurredAtUtc") WHERE "IsDeleted" = false;
CREATE UNIQUE INDEX "UX_TelegramUpdateInbox_Active_Update_Event" ON "TelegramUpdateInbox" ("TelegramUpdateId", "EventType") WHERE "IsDeleted" = false;
            """.Trim()
        )
        |> ignore

    override this.Down() =
        this.Execute.Sql(
            """
DROP TABLE IF EXISTS "TelegramUpdateInbox";
DROP TABLE IF EXISTS "OutboxEvents";
DROP TABLE IF EXISTS "StreamNodeHeartbeats";
DROP TABLE IF EXISTS "LibraryScanJobs";
DROP TABLE IF EXISTS "SocialLinks";
DROP TABLE IF EXISTS "DonationGoals";
DROP TABLE IF EXISTS "Payments";
DROP TABLE IF EXISTS "SayMessages";
DROP TABLE IF EXISTS "PlaybackQueue";
DROP TABLE IF EXISTS "TrackRequests";
DROP TABLE IF EXISTS "PlaylistItems";
DROP TABLE IF EXISTS "Playlists";
DROP TABLE IF EXISTS "TrackFiles";
DROP TABLE IF EXISTS "TrackLinks";
DROP TABLE IF EXISTS "StorageBackends";
DROP TABLE IF EXISTS "Tracks";
            """.Trim()
        )
        |> ignore
