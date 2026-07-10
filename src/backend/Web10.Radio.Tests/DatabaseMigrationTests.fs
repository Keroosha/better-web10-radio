namespace Web10.Radio.Tests

open System
open System.Threading
open Npgsql
open System.Text.RegularExpressions
open FluentMigrator.Runner
open Microsoft.Extensions.DependencyInjection
open NUnit.Framework
open Web10.Radio.Database
open Web10.Radio.API
open Web10.Radio.Database.Migrations
open Web10.Radio.Database.Repositories

module DatabaseMigrationTests =
    let private readPublicTables (connection: NpgsqlConnection) =
        task {
            use command =
                new NpgsqlCommand(
                    """SELECT table_name
FROM information_schema.tables
WHERE table_schema = 'public'
ORDER BY table_name;""",
                    connection
                )

            let! reader = command.ExecuteReaderAsync()
            use reader = reader
            let tables = ResizeArray<string>()
            let mutable keepReading = true

            while keepReading do
                let! hasRow = reader.ReadAsync()

                if hasRow then
                    tables.Add(reader.GetString(0))
                else
                    keepReading <- false

            return Set.ofSeq tables
        }

    [<Test>]
    let ``migrator creates all first version tables and FluentMigrator version row`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                let! tables = readPublicTables connection

                let expectedDomainTables =
                    [ "Tracks"
                      "TrackLinks"
                      "TrackFiles"
                      "StorageBackends"
                      "Playlists"
                      "PlaylistItems"
                      "PlaybackQueue"
                      "TrackRequests"
                      "SayMessages"
                      "Payments"
                      "DonationGoals"
                      "SocialLinks"
                      "LibraryScanJobs"
                      "StreamNodeHeartbeats"
                      "OutboxEvents"
                      "TelegramUpdateInbox" ]

                let missingTables = expectedDomainTables |> List.filter (fun tableName -> not (tables.Contains tableName))
                Assert.That(missingTables, Is.Empty)

                use versionCommand =
                    new NpgsqlCommand(
                        """SELECT "Version" FROM "VersionInfo" WHERE "Version" = 202607080001;""",
                        connection
                    )

                let! version = versionCommand.ExecuteScalarAsync()
                Assert.That(version, Is.EqualTo(202607080001L))
            })

    [<Test>]
    let ``normal track reads exclude soft deleted rows`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let activeId = Guid.NewGuid()
                let deletedId = Guid.NewGuid()

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                use insertCommand =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@ActiveId, 'Active title', 'Active artist', false),
       (@DeletedId, 'Deleted title', 'Deleted artist', true);""",
                        connection
                    )

                insertCommand.Parameters.AddWithValue("ActiveId", activeId) |> ignore
                insertCommand.Parameters.AddWithValue("DeletedId", deletedId) |> ignore
                let! _ = insertCommand.ExecuteNonQueryAsync()

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! activeTracks = TrackRepository.listActive dataSource CancellationToken.None

                Assert.That(List.length activeTracks, Is.EqualTo(1))
                let track = List.head activeTracks
                Assert.That(track.Id, Is.EqualTo(activeId))
                Assert.That(track.Title, Is.EqualTo("Active title"))
                Assert.That(track.Artist, Is.EqualTo("Active artist"))
            })

    let private mutableDomainTables =
        Set.ofList
            [ "Tracks"
              "TrackLinks"
              "TrackFiles"
              "StorageBackends"
              "Playlists"
              "PlaylistItems"
              "PlaybackQueue"
              "TrackRequests"
              "SayMessages"
              "Payments"
              "DonationGoals"
              "SocialLinks"
              "LibraryScanJobs"
              "StreamNodeHeartbeats"
              "OutboxEvents"
              "TelegramUpdateInbox" ]

    let private normalizeCatalogExpression (expression: string) =
        Regex.Replace(expression, "\\s+", "")
            .Replace("\"", "")
            .Replace("(", "")
            .Replace(")", "")
            .Replace("::text", "")
            .Replace("=ANYARRAY", "IN")
            .Replace("[", "")
            .Replace("]", "")

    let private readAuditColumns (connection: NpgsqlConnection) =
        task {
            use command =
                new NpgsqlCommand(
                    """SELECT table_name, column_name
FROM information_schema.columns
WHERE table_schema = 'public'
  AND column_name IN ('IsDeleted', 'CreatedAtUtc', 'UpdatedAtUtc')
ORDER BY table_name, column_name;""",
                    connection
                )

            let! reader = command.ExecuteReaderAsync()
            use reader = reader
            let mutable auditColumns = Map.empty<string, Set<string>>
            let mutable hasRow = true

            while hasRow do
                let! next = reader.ReadAsync()
                hasRow <- next

                if next then
                    let tableName = reader.GetString(0)
                    let columnName = reader.GetString(1)
                    let columns = auditColumns |> Map.tryFind tableName |> Option.defaultValue Set.empty
                    auditColumns <- auditColumns |> Map.add tableName (columns |> Set.add columnName)

            return auditColumns
        }

    let private readForeignKeys (connection: NpgsqlConnection) =
        task {
            use command =
                new NpgsqlCommand(
                    """SELECT source.relname, catalog_constraint.conname, pg_get_constraintdef(catalog_constraint.oid)
FROM pg_constraint AS catalog_constraint
INNER JOIN pg_class AS source ON source.oid = catalog_constraint.conrelid
INNER JOIN pg_namespace AS schema ON schema.oid = source.relnamespace
WHERE schema.nspname = 'public'
  AND catalog_constraint.contype = 'f'
ORDER BY source.relname, catalog_constraint.conname;""",
                    connection
                )

            let! reader = command.ExecuteReaderAsync()
            use reader = reader
            let foreignKeys = ResizeArray<string * string * string>()
            let mutable hasRow = true

            while hasRow do
                let! next = reader.ReadAsync()
                hasRow <- next

                if next then
                    foreignKeys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2) |> normalizeCatalogExpression))

            return Set.ofSeq foreignKeys
        }

    let private readCheckConstraints (connection: NpgsqlConnection) =
        task {
            use command =
                new NpgsqlCommand(
                    """SELECT source.relname, catalog_constraint.conname, pg_get_constraintdef(catalog_constraint.oid)
FROM pg_constraint AS catalog_constraint
INNER JOIN pg_class AS source ON source.oid = catalog_constraint.conrelid
INNER JOIN pg_namespace AS schema ON schema.oid = source.relnamespace
WHERE schema.nspname = 'public'
  AND catalog_constraint.contype = 'c'
ORDER BY source.relname, catalog_constraint.conname;""",
                    connection
                )

            let! reader = command.ExecuteReaderAsync()
            use reader = reader
            let checkConstraints = ResizeArray<string * string * string>()
            let mutable hasRow = true

            while hasRow do
                let! next = reader.ReadAsync()
                hasRow <- next

                if next then
                    checkConstraints.Add(
                        (reader.GetString(0), reader.GetString(1), reader.GetString(2) |> normalizeCatalogExpression)
                    )

            return Set.ofSeq checkConstraints
        }

    let private readActiveIndexPredicates (connection: NpgsqlConnection) =
        task {
            use command =
                new NpgsqlCommand(
                    """SELECT index_class.relname, pg_get_expr(index_definition.indpred, index_definition.indrelid)
FROM pg_index AS index_definition
INNER JOIN pg_class AS index_class ON index_class.oid = index_definition.indexrelid
INNER JOIN pg_namespace AS schema ON schema.oid = index_class.relnamespace
WHERE schema.nspname = 'public'
  AND index_definition.indpred IS NOT NULL
ORDER BY index_class.relname;""",
                    connection
                )

            let! reader = command.ExecuteReaderAsync()
            use reader = reader
            let predicates = ResizeArray<string * string>()
            let mutable hasRow = true

            while hasRow do
                let! next = reader.ReadAsync()
                hasRow <- next

                if next then
                    predicates.Add((reader.GetString(0), reader.GetString(1) |> normalizeCatalogExpression))

            return Set.ofSeq predicates
        }

    let private readB4IndexDefinitions (connection: NpgsqlConnection) =
        task {
            use command =
                new NpgsqlCommand(
                    """SELECT index_class.relname,
       table_class.relname,
       access_method.amname,
       index_definition.indisunique,
       COALESCE(pg_get_expr(index_definition.indexprs, index_definition.indrelid), ''),
       COALESCE(pg_get_expr(index_definition.indpred, index_definition.indrelid), ''),
       (
           SELECT string_agg(
               pg_get_indexdef(index_definition.indexrelid, key_position, true),
               ', ' ORDER BY key_position
           )
           FROM generate_series(1, index_definition.indnkeyatts) AS key_position
       ),
       (
           SELECT string_agg(opclass.opcname, ', ' ORDER BY key_position)
           FROM generate_series(1, index_definition.indnkeyatts) AS key_position
           INNER JOIN pg_opclass AS opclass
               ON opclass.oid = index_definition.indclass[(key_position - 1)::integer]
       )
FROM pg_index AS index_definition
INNER JOIN pg_class AS index_class ON index_class.oid = index_definition.indexrelid
INNER JOIN pg_class AS table_class ON table_class.oid = index_definition.indrelid
INNER JOIN pg_namespace AS schema ON schema.oid = index_class.relnamespace
INNER JOIN pg_am AS access_method ON access_method.oid = index_class.relam
WHERE schema.nspname = 'public'
  AND index_class.relname IN (
      'IX_Tracks_Active_Title_Trgm',
      'IX_Tracks_Active_ArtistTitle_Trgm',
      'UX_Payments_Active_InvoicePayload',
      'UX_Payments_Active_PurposeEntity',
      'UX_PlaybackQueue_Active_TrackRequest'
  )
ORDER BY index_class.relname;""",
                    connection
                )

            let! reader = command.ExecuteReaderAsync()
            use reader = reader
            let definitions = ResizeArray<string * string * string * bool * string * string * string * string>()
            let mutable hasRow = true

            while hasRow do
                let! next = reader.ReadAsync()
                hasRow <- next

                if next then
                    definitions.Add(
                        ( reader.GetString(0),
                          reader.GetString(1),
                          reader.GetString(2),
                          reader.GetBoolean(3),
                          reader.GetString(4) |> normalizeCatalogExpression,
                          reader.GetString(5) |> normalizeCatalogExpression,
                          reader.GetString(6) |> normalizeCatalogExpression,
                          reader.GetString(7) |> normalizeCatalogExpression )
                    )

            return Set.ofSeq definitions
        }

    let private hasPgTrgmExtension (connection: NpgsqlConnection) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm');",
                    connection
                )

            let! extensionInstalled = command.ExecuteScalarAsync()
            return extensionInstalled :?> bool
        }

    let private withMigrationRunner (connectionString: string) operation =
        let services = ServiceCollection()

        services
            .AddFluentMigratorCore()
            .ConfigureRunner(fun runnerBuilder ->
                runnerBuilder
                    .AddPostgres()
                    .WithGlobalConnectionString(connectionString)
                    .ScanIn(typeof<CreateInitialSchema>.Assembly)
                    .For.Migrations()
                |> ignore)
        |> ignore

        use provider = services.BuildServiceProvider()
        operation (provider.GetRequiredService<IMigrationRunner>())

    let private postgresExceptionMessage (error: exn) =
        let rec findMessage (current: exn) =
            match current with
            | :? PostgresException as postgresError -> Some postgresError.MessageText
            | _ ->
                match current.InnerException with
                | null -> None
                | inner -> findMessage inner

        findMessage error

    let private assertB4DuplicatePreflightRejects
        (seedSql: string)
        (expectedMessage: string)
        : System.Threading.Tasks.Task<unit> =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                withMigrationRunner connectionString (fun runner -> runner.MigrateDown(202607100002L))

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use seed = new NpgsqlCommand(seedSql, connection)
                let! _ = seed.ExecuteNonQueryAsync()

                let migrationFailure =
                    try
                        withMigrationRunner connectionString (fun runner -> runner.MigrateUp())
                        None
                    with error ->
                        Some error

                match migrationFailure |> Option.bind postgresExceptionMessage with
                | Some actualMessage -> Assert.That(actualMessage, Is.EqualTo(expectedMessage))
                | None -> Assert.Fail("Expected B4 migration preflight to surface a PostgreSQL exception with its actionable diagnostic.")
            })

    [<Test>]
    let ``migrations preserve audit columns foreign keys checks and all active index predicates`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                let! auditColumns = readAuditColumns connection
                let! foreignKeys = readForeignKeys connection
                let! checkConstraints = readCheckConstraints connection
                let! activeIndexPredicates = readActiveIndexPredicates connection

                let expectedAuditColumns =
                    mutableDomainTables
                    |> Seq.map (fun tableName -> tableName, Set.ofList [ "IsDeleted"; "CreatedAtUtc"; "UpdatedAtUtc" ])
                    |> Map.ofSeq

                let expectedForeignKeys =
                    Set.ofList
                        [ "LibraryScanJobs", "LibraryScanJobs_StorageBackendId_fkey", "FOREIGNKEYStorageBackendIdREFERENCESStorageBackendsId"
                          "PlaybackQueue", "PlaybackQueue_PlaylistItemId_fkey", "FOREIGNKEYPlaylistItemIdREFERENCESPlaylistItemsId"
                          "PlaybackQueue", "PlaybackQueue_TrackId_fkey", "FOREIGNKEYTrackIdREFERENCESTracksId"
                          "PlaybackQueue", "PlaybackQueue_TrackRequestId_fkey", "FOREIGNKEYTrackRequestIdREFERENCESTrackRequestsId"
                          "PlaylistItems", "PlaylistItems_PlaylistId_fkey", "FOREIGNKEYPlaylistIdREFERENCESPlaylistsId"
                          "PlaylistItems", "PlaylistItems_TrackId_fkey", "FOREIGNKEYTrackIdREFERENCESTracksId"
                          "TrackFiles", "TrackFiles_StorageBackendId_fkey", "FOREIGNKEYStorageBackendIdREFERENCESStorageBackendsId"
                          "TrackFiles", "TrackFiles_TrackId_fkey", "FOREIGNKEYTrackIdREFERENCESTracksId"
                          "TrackLinks", "TrackLinks_TrackId_fkey", "FOREIGNKEYTrackIdREFERENCESTracksId"
                          "TrackRequests", "TrackRequests_MatchedTrackId_fkey", "FOREIGNKEYMatchedTrackIdREFERENCESTracksId" ]

                let expectedCheckConstraints =
                    Set.ofList
                        [ "DonationGoals", "DonationGoals_GoalStars_check", "CHECKGoalStars>0"
                          "DonationGoals", "DonationGoals_RaisedStars_check", "CHECKRaisedStars>=0"
                          "LibraryScanJobs", "CK_LibraryScanJobs_ClaimAttempt_NonNegative", "CHECKClaimAttempt>=0"
                          "LibraryScanJobs", "LibraryScanJobs_Status_check", "CHECKStatusIN'Queued','Running','Completed','Failed'"
                          "OutboxEvents", "OutboxEvents_Attempts_check", "CHECKAttempts>=0"
                          "OutboxEvents", "OutboxEvents_Status_check", "CHECKStatusIN'Pending','Processing','Processed','Failed'"
                          "Payments", "Payments_AmountStars_check", "CHECKAmountStars>0"
                          "Payments", "Payments_Currency_check", "CHECKCurrency='XTR'"
                          "Payments", "Payments_ProviderToken_check", "CHECKProviderToken=''"
                          "Payments", "Payments_Purpose_check", "CHECKPurposeIN'Request','Say','Donation'"
                          "Payments", "Payments_Status_check", "CHECKStatusIN'InvoiceCreated','PreCheckoutApproved','Paid','Refunded','Rejected'"
                          "PlaybackQueue", "CK_PlaybackQueue_ClaimAttempt_NonNegative", "CHECKClaimAttempt>=0"
                          "PlaybackQueue", "PlaybackQueue_Source_check", "CHECKSourceIN'playlist','request','admin','fallback'"
                          "PlaybackQueue", "PlaybackQueue_Status_check", "CHECKStatusIN'Queued','Claimed','Playing','Played','Failed'"
                          "PlaylistItems", "PlaylistItems_Position_check", "CHECKPosition>=0"
                          "SayMessages", "SayMessages_AmountStars_check", "CHECKAmountStars>=0"
                          "SayMessages", "SayMessages_Status_check", "CHECKStatusIN'PendingPayment','PaidPendingModeration','Approved','Rejected'"
                          "SocialLinks", "SocialLinks_Kind_check", "CHECKKindIN'telegram','youtube','instagram','discord','external'"
                          "SocialLinks", "SocialLinks_Position_check", "CHECKPosition>=0"
                          "StorageBackends", "StorageBackends_Type_check", "CHECKTypeIN'Local','S3'"
                          "StreamNodeHeartbeats", "StreamNodeHeartbeats_Status_check", "CHECKStatusIN'Starting','Live','Degraded','Restarting','Failed','Offline'"
                          "TrackFiles", "TrackFiles_SizeBytes_check", "CHECKSizeBytesISNULLORSizeBytes>=0"
                          "TrackLinks", "TrackLinks_Kind_check", "CHECKKindIN'bandcamp','soundcloud','youtube','artist','external'"
                          "TrackRequests", "TrackRequests_Status_check", "CHECKStatusIN'NeedsReview','Matched','Rejected','Queued','PaidPending','Paid'"
                          "Tracks", "Tracks_DurationMs_check", "CHECKDurationMsISNULLORDurationMs>=0" ]

                let expectedActiveIndexPredicates =
                    Set.ofList
                        [ "IX_Tracks_Active_TitleArtist", "IsDeleted=false"
                          "IX_Tracks_Active_Title_Trgm", "IsDeleted=false"
                          "IX_Tracks_Active_ArtistTitle_Trgm", "IsDeleted=false"
                          "IX_StorageBackends_Active_Type", "IsDeleted=false"
                          "IX_TrackLinks_Active_TrackId", "IsDeleted=false"
                          "UX_TrackLinks_Active_TrackId_Url", "IsDeleted=false"
                          "IX_TrackFiles_Active_TrackId", "IsDeleted=false"
                          "IX_TrackFiles_Active_StorageBackendId", "IsDeleted=false"
                          "UX_TrackFiles_Active_Backend_StoragePath", "IsDeleted=falseANDStorageBackendIdISNOTNULL"
                          "UX_TrackFiles_Active_NullBackend_StoragePath", "IsDeleted=falseANDStorageBackendIdISNULL"
                          "UX_Playlists_Active_Name", "IsDeleted=false"
                          "IX_PlaylistItems_Active_PlaylistId", "IsDeleted=false"
                          "UX_PlaylistItems_Active_PlaylistId_Position", "IsDeleted=false"
                          "IX_TrackRequests_Active_Status_RequestedAtUtc", "IsDeleted=false"
                          "IX_PlaybackQueue_Active_Claim", "IsDeleted=falseANDStatus='Queued'"
                          "IX_PlaybackQueue_Active_Status", "IsDeleted=false"
                          "IX_PlaybackQueue_Active_ClaimLease", "IsDeleted=falseANDStatusIN'Claimed','Playing'"
                          "UX_PlaybackQueue_Active_TrackRequest", "IsDeleted=falseANDTrackRequestIdISNOTNULL"
                          "IX_SayMessages_Active_Status_SubmittedAtUtc", "IsDeleted=false"
                          "UX_Payments_Active_TelegramPaymentChargeId", "IsDeleted=falseANDTelegramPaymentChargeIdISNOTNULL"
                          "UX_Payments_Active_InvoicePayload", "IsDeleted=false"
                          "UX_Payments_Active_PurposeEntity", "IsDeleted=falseANDPurposeEntityIdISNOTNULL"
                          "IX_Payments_Active_Status", "IsDeleted=false"
                          "IX_DonationGoals_Active_IsActive", "IsDeleted=false"
                          "IX_SocialLinks_Active_Position", "IsDeleted=false"
                          "IX_SocialLinks_Active_Featured", "IsDeleted=false"
                          "IX_LibraryScanJobs_Active_Status_RequestedAtUtc", "IsDeleted=false"
                          "IX_LibraryScanJobs_Active_ClaimLease", "IsDeleted=falseANDStatusIN'Queued','Running'"
                          "IX_StreamNodeHeartbeats_Active_HeartbeatAtUtc", "IsDeleted=false"
                          "IX_OutboxEvents_Active_Status_NextAttemptAtUtc", "IsDeleted=false"
                          "IX_OutboxEvents_Active_GlobalOrder", "IsDeleted=falseANDStatus<>'Processed'"
                          "UX_TelegramUpdateInbox_Active_Update_Event", "IsDeleted=false" ]

                Assert.That((auditColumns = expectedAuditColumns), Is.True, sprintf "Audit-column drift. Expected: %A; actual: %A" expectedAuditColumns auditColumns)
                Assert.That((foreignKeys = expectedForeignKeys), Is.True, sprintf "Foreign-key drift. Expected: %A; actual: %A" expectedForeignKeys foreignKeys)
                Assert.That((checkConstraints = expectedCheckConstraints), Is.True, sprintf "Check-constraint drift. Expected: %A; actual: %A" expectedCheckConstraints checkConstraints)
                Assert.That((activeIndexPredicates = expectedActiveIndexPredicates), Is.True, sprintf "Active-index predicate drift. Expected: %A; actual: %A" expectedActiveIndexPredicates activeIndexPredicates)
            })

    [<Test>]
    let ``202607100003 installs pg_trgm and the five exact B4 indexes`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                let! pgTrgmInstalled = hasPgTrgmExtension connection
                let! b4Indexes = readB4IndexDefinitions connection

                let expectedB4Indexes =
                    Set.ofList
                        [ "IX_Tracks_Active_Title_Trgm", "Tracks", "gin", false, "lowerTitle", "IsDeleted=false", "lowerTitle", "gin_trgm_ops"
                          "IX_Tracks_Active_ArtistTitle_Trgm", "Tracks", "gin", false, "lowerArtist||'—'||Title", "IsDeleted=false", "lowerArtist||'—'||Title", "gin_trgm_ops"
                          "UX_Payments_Active_InvoicePayload", "Payments", "btree", true, "", "IsDeleted=false", "TelegramInvoicePayload", "text_ops"
                          "UX_Payments_Active_PurposeEntity", "Payments", "btree", true, "", "IsDeleted=falseANDPurposeEntityIdISNOTNULL", "Purpose,PurposeEntityId", "text_ops,uuid_ops"
                          "UX_PlaybackQueue_Active_TrackRequest", "PlaybackQueue", "btree", true, "", "IsDeleted=falseANDTrackRequestIdISNOTNULL", "TrackRequestId", "uuid_ops" ]

                Assert.That(pgTrgmInstalled, Is.True, "B4 must install pg_trgm before creating trigram indexes.")

                Assert.That(
                    (b4Indexes = expectedB4Indexes),
                    Is.True,
                    sprintf
                        "B4 index access methods, expressions, uniqueness, key columns, and active-row predicates must not drift. Expected: %A; actual: %A"
                        expectedB4Indexes
                        b4Indexes
                )
            })

    [<Test>]
    let ``202607100003 refuses duplicate active invoice payloads before adding indexes`` () =
        assertB4DuplicatePreflightRejects
            """INSERT INTO "Payments" ("Id", "Purpose", "AmountStars", "TelegramInvoicePayload", "Status", "IsDeleted")
VALUES ('00000000-0000-0000-0000-000000000101', 'Donation', 1, 'duplicate-invoice-payload', 'InvoiceCreated', false),
       ('00000000-0000-0000-0000-000000000102', 'Donation', 1, 'duplicate-invoice-payload', 'InvoiceCreated', false);"""
            "Duplicate active TelegramInvoicePayload prevents B4 migration."

    [<Test>]
    let ``202607100003 refuses duplicate active payment purpose entities before adding indexes`` () =
        assertB4DuplicatePreflightRejects
            """INSERT INTO "Payments" ("Id", "Purpose", "PurposeEntityId", "AmountStars", "TelegramInvoicePayload", "Status", "IsDeleted")
VALUES ('00000000-0000-0000-0000-000000000201', 'Request', '00000000-0000-0000-0000-000000000299', 1, 'purpose-invoice-one', 'InvoiceCreated', false),
       ('00000000-0000-0000-0000-000000000202', 'Request', '00000000-0000-0000-0000-000000000299', 1, 'purpose-invoice-two', 'InvoiceCreated', false);"""
            "Duplicate active payment purpose entity prevents B4 migration."

    [<Test>]
    let ``202607100003 refuses duplicate active playback request rows before adding indexes`` () =
        assertB4DuplicatePreflightRejects
            """INSERT INTO "TrackRequests" ("Id", "Query", "Status", "IsDeleted")
VALUES ('00000000-0000-0000-0000-000000000301', 'duplicate playback request', 'NeedsReview', false);
INSERT INTO "PlaybackQueue" ("Id", "TrackRequestId", "Source", "Status", "IsDeleted")
VALUES ('00000000-0000-0000-0000-000000000302', '00000000-0000-0000-0000-000000000301', 'request', 'Queued', false),
       ('00000000-0000-0000-0000-000000000303', '00000000-0000-0000-0000-000000000301', 'request', 'Queued', false);"""
            "Duplicate active playback TrackRequestId prevents B4 migration."


    [<Test>]
    let ``migrations roll back to zero and rebuild the complete current schema`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                withMigrationRunner connectionString (fun runner -> runner.MigrateDown(0L))

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                let! tablesAfterDown = readPublicTables connection
                let remainingDomainTables = Set.intersect mutableDomainTables tablesAfterDown
                Assert.That(remainingDomainTables, Is.Empty, "Down must remove every domain table before an upgrade can rebuild it.")
                let! extensionAfterDown = hasPgTrgmExtension connection
                Assert.That(extensionAfterDown, Is.True, "B4 rollback must retain pg_trgm because the migration cannot prove extension ownership.")

                withMigrationRunner connectionString (fun runner -> runner.MigrateUp())
                let! tablesAfterUp = readPublicTables connection
                Assert.That(Set.intersect mutableDomainTables tablesAfterUp, Is.EqualTo(mutableDomainTables :> obj))

                use versionCommand =
                    new NpgsqlCommand("""SELECT "Version" FROM "VersionInfo" ORDER BY "Version";""", connection)

                let! reader = versionCommand.ExecuteReaderAsync()
                use reader = reader
                let versions = ResizeArray<int64>()
                let mutable hasRow = true

                while hasRow do
                    let! next = reader.ReadAsync()
                    hasRow <- next

                    if next then
                        versions.Add(reader.GetInt64(0))

                Assert.That(
                    List.ofSeq versions,
                    Is.EqualTo(([ 202607080001L; 202607100001L; 202607100002L; 202607100003L ] : int64 list) :> obj),
                    "A full down/up cycle must restore every migration in version order."
                )
            })

    [<Test>]
    let ``soft-deleted track parents cannot supply an active stream file join`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let activeTrackId = Guid.NewGuid()
                let deletedTrackId = Guid.NewGuid()
                let activeTrackFileId = Guid.NewGuid()
                let deletedTrackFileId = Guid.NewGuid()
                let activeQueueItemId = Guid.NewGuid()
                let deletedQueueItemId = Guid.NewGuid()
                let nowUtc = DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero)
                let activeCachePath = "/cache/active-track.mp3"
                let deletedCachePath = "/cache/deleted-parent.mp3"

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use insert =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@ActiveTrackId, 'Active', 'Artist', false),
       (@DeletedTrackId, 'Deleted', 'Artist', true);
INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "CachePath", "IsCached", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES (@ActiveTrackFileId, @ActiveTrackId, '/library/active.mp3', @ActiveCachePath, true, @NowUtc, @NowUtc, false),
       (@DeletedTrackFileId, @DeletedTrackId, '/library/deleted.mp3', @DeletedCachePath, true, @NowUtc, @NowUtc, false);
INSERT INTO "PlaybackQueue" ("Id", "TrackId", "Source", "Status", "StartedAtUtc", "RequestedAtUtc", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES (@ActiveQueueItemId, @ActiveTrackId, 'playlist', 'Playing', @ActiveStartedAtUtc, @NowUtc, @NowUtc, @NowUtc, false),
       (@DeletedQueueItemId, @DeletedTrackId, 'playlist', 'Playing', @DeletedStartedAtUtc, @NowUtc, @NowUtc, @NowUtc, false);""",
                        connection
                    )

                [ "ActiveTrackId", box activeTrackId
                  "DeletedTrackId", box deletedTrackId
                  "ActiveTrackFileId", box activeTrackFileId
                  "DeletedTrackFileId", box deletedTrackFileId
                  "ActiveQueueItemId", box activeQueueItemId
                  "DeletedQueueItemId", box deletedQueueItemId
                  "ActiveCachePath", box activeCachePath
                  "DeletedCachePath", box deletedCachePath
                  "NowUtc", box nowUtc
                  "ActiveStartedAtUtc", box nowUtc
                  "DeletedStartedAtUtc", box (nowUtc.AddMinutes(1.0)) ]
                |> List.iter (fun (name, value) -> insert.Parameters.AddWithValue(name, value) |> ignore)

                let! _ = insert.ExecuteNonQueryAsync()
                use dataSource = NpgsqlDataSource.Create(connectionString)
                let clock = { new IClock with member _.UtcNow = nowUtc }
                let! heartbeatResult =
                    StreamNodeHeartbeatRepository.insertHeartbeat dataSource (Guid.NewGuid()) "Live" nowUtc None "{}" CancellationToken.None

                match heartbeatResult with
                | Error error -> Assert.Fail(sprintf "Expected heartbeat setup success, got %A." error)
                | Ok () -> ()

                let! streamFile = PlayerStateReadModel.loadStreamFile dataSource clock CancellationToken.None

                match streamFile with
                | Ok(Some file) -> Assert.That(file.CachePath, Is.EqualTo(activeCachePath))
                | Ok None -> Assert.Fail("Expected the active parent track to provide a stream file.")
                | Error error -> Assert.Fail(sprintf "Expected stream-file read success, got %A." error)
            })

    [<Test>]
    let ``UUIDv7 identifiers retain version bits after PostgreSQL uuid persistence`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let idGenerator = UuidV7IdGenerator() :> IIdGenerator
                let generatedId = idGenerator.NewId()
                let nowUtc = DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero)
                let versionNibble = int (generatedId.ToByteArray(true)[6] >>> 4)
                Assert.That(versionNibble, Is.EqualTo(7), "New domain identifiers must be RFC9562 UUIDv7 values.")

                use dataSource = NpgsqlDataSource.Create(connectionString)
                let! writeResult =
                    StreamNodeHeartbeatRepository.insertHeartbeat
                        dataSource
                        generatedId
                        "Live"
                        nowUtc
                        None
                        "{}"
                        CancellationToken.None

                match writeResult with
                | Error error -> Assert.Fail(sprintf "Expected heartbeat persistence success, got %A." error)
                | Ok () -> ()

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use select = new NpgsqlCommand("""SELECT "Id" FROM "StreamNodeHeartbeats" WHERE "Id" = @Id;""", connection)
                select.Parameters.AddWithValue("Id", generatedId) |> ignore
                let! persisted = select.ExecuteScalarAsync()
                let persistedId = persisted :?> Guid

                Assert.That(persistedId, Is.EqualTo(generatedId))
                Assert.That(int (persistedId.ToByteArray(true)[6] >>> 4), Is.EqualTo(7))
            })

    [<Test>]
    let ``202607100002 deterministically merges duplicate tracks and rewires every track reference`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                withMigrationRunner connectionString (fun runner -> runner.MigrateDown(202607080001L))

                let backendId = Guid.Parse("00000000-0000-0000-0000-000000000001")
                let winnerId = Guid.Parse("00000000-0000-0000-0000-000000000010")
                let loserOneId = Guid.Parse("00000000-0000-0000-0000-000000000020")
                let loserTwoId = Guid.Parse("00000000-0000-0000-0000-000000000030")
                let playlistId = Guid.Parse("00000000-0000-0000-0000-000000000040")
                let playlistItemOneId = Guid.Parse("00000000-0000-0000-0000-000000000041")
                let playlistItemTwoId = Guid.Parse("00000000-0000-0000-0000-000000000042")
                let trackRequestOneId = Guid.Parse("00000000-0000-0000-0000-000000000050")
                let trackRequestTwoId = Guid.Parse("00000000-0000-0000-0000-000000000051")
                let queueItemOneId = Guid.Parse("00000000-0000-0000-0000-000000000060")
                let queueItemTwoId = Guid.Parse("00000000-0000-0000-0000-000000000061")
                let winnerFileId = Guid.Parse("00000000-0000-0000-0000-000000000070")
                let loserOneFileId = Guid.Parse("00000000-0000-0000-0000-000000000071")
                let loserTwoFileId = Guid.Parse("00000000-0000-0000-0000-000000000072")
                let winnerLinkId = Guid.Parse("00000000-0000-0000-0000-000000000080")
                let loserOneLinkId = Guid.Parse("00000000-0000-0000-0000-000000000081")
                let loserTwoLinkId = Guid.Parse("00000000-0000-0000-0000-000000000082")
                let winnerCreatedAtUtc = DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                use seed =
                    new NpgsqlCommand(
                        """INSERT INTO "StorageBackends" ("Id", "Name", "Type", "S3Bucket", "IsEnabled", "IsDeleted")
VALUES (@BackendId, 'legacy', 'S3', 'bucket', true, false);
INSERT INTO "Tracks" ("Id", "Title", "Artist", "IsDeleted")
VALUES (@WinnerId, 'Winner', 'Artist', false),
       (@LoserOneId, 'Loser one', 'Artist', false),
       (@LoserTwoId, 'Loser two', 'Artist', false);
INSERT INTO "TrackFiles" ("Id", "TrackId", "StorageBackendId", "StoragePath", "IsDeleted", "CreatedAtUtc")
VALUES (@WinnerFileId, @WinnerId, @BackendId, 'same.mp3', false, @WinnerCreatedAtUtc),
       (@LoserOneFileId, @LoserOneId, @BackendId, 'same.mp3', false, @LoserOneCreatedAtUtc),
       (@LoserTwoFileId, @LoserTwoId, @BackendId, 'same.mp3', false, @LoserTwoCreatedAtUtc);
INSERT INTO "TrackLinks" ("Id", "TrackId", "Kind", "Url", "IsDeleted", "CreatedAtUtc")
VALUES (@WinnerLinkId, @WinnerId, 'external', 'https://same', false, @WinnerCreatedAtUtc),
       (@LoserOneLinkId, @LoserOneId, 'external', 'https://same', false, @LoserOneCreatedAtUtc),
       (@LoserTwoLinkId, @LoserTwoId, 'external', 'https://same', false, @LoserTwoCreatedAtUtc);
INSERT INTO "Playlists" ("Id", "Name", "IsDeleted") VALUES (@PlaylistId, 'legacy playlist', false);
INSERT INTO "PlaylistItems" ("Id", "PlaylistId", "TrackId", "Position", "IsDeleted")
VALUES (@PlaylistItemOneId, @PlaylistId, @LoserOneId, 0, false),
       (@PlaylistItemTwoId, @PlaylistId, @LoserTwoId, 1, false);
INSERT INTO "TrackRequests" ("Id", "Query", "MatchedTrackId", "Status", "IsDeleted")
VALUES (@TrackRequestOneId, 'one', @LoserOneId, 'Matched', false),
       (@TrackRequestTwoId, 'two', @LoserTwoId, 'Matched', false);
INSERT INTO "PlaybackQueue" ("Id", "TrackId", "TrackRequestId", "PlaylistItemId", "Source", "Status", "RequestedAtUtc", "IsDeleted")
VALUES (@QueueItemOneId, @LoserOneId, @TrackRequestOneId, @PlaylistItemOneId, 'playlist', 'Queued', @WinnerCreatedAtUtc, false),
       (@QueueItemTwoId, @LoserTwoId, @TrackRequestTwoId, @PlaylistItemTwoId, 'request', 'Queued', @WinnerCreatedAtUtc, false);""",
                        connection
                    )

                [ "BackendId", box backendId
                  "WinnerId", box winnerId
                  "LoserOneId", box loserOneId
                  "LoserTwoId", box loserTwoId
                  "PlaylistId", box playlistId
                  "PlaylistItemOneId", box playlistItemOneId
                  "PlaylistItemTwoId", box playlistItemTwoId
                  "TrackRequestOneId", box trackRequestOneId
                  "TrackRequestTwoId", box trackRequestTwoId
                  "QueueItemOneId", box queueItemOneId
                  "QueueItemTwoId", box queueItemTwoId
                  "WinnerFileId", box winnerFileId
                  "LoserOneFileId", box loserOneFileId
                  "LoserTwoFileId", box loserTwoFileId
                  "WinnerLinkId", box winnerLinkId
                  "LoserOneLinkId", box loserOneLinkId
                  "LoserTwoLinkId", box loserTwoLinkId
                  "WinnerCreatedAtUtc", box winnerCreatedAtUtc
                  "LoserOneCreatedAtUtc", box (winnerCreatedAtUtc.AddSeconds(1.0))
                  "LoserTwoCreatedAtUtc", box (winnerCreatedAtUtc.AddSeconds(2.0)) ]
                |> List.iter (fun (name, value) -> seed.Parameters.AddWithValue(name, value) |> ignore)

                let! _ = seed.ExecuteNonQueryAsync()
                withMigrationRunner connectionString (fun runner -> runner.MigrateUp())

                use check =
                    new NpgsqlCommand(
                        """SELECT
    (SELECT count(*) FROM "Tracks" WHERE "Id" IN (@WinnerId, @LoserOneId, @LoserTwoId) AND "IsDeleted" = false),
    (SELECT count(*) FROM "Tracks" WHERE "Id" = @WinnerId AND "IsDeleted" = false),
    (SELECT count(*) FROM "TrackFiles" WHERE "StorageBackendId" = @BackendId AND "StoragePath" = 'same.mp3' AND "IsDeleted" = false),
    (SELECT count(*) FROM "TrackFiles" WHERE "StorageBackendId" = @BackendId AND "StoragePath" = 'same.mp3' AND "TrackId" = @WinnerId AND "IsDeleted" = false),
    (SELECT count(*) FROM "TrackLinks" WHERE "TrackId" IN (@WinnerId, @LoserOneId, @LoserTwoId) AND "Url" = 'https://same' AND "IsDeleted" = false),
    (SELECT count(*) FROM "TrackLinks" WHERE "TrackId" = @WinnerId AND "Url" = 'https://same' AND "IsDeleted" = false),
    (SELECT count(*) FROM "TrackLinks" WHERE "Id" IN (@LoserOneLinkId, @LoserTwoLinkId) AND "IsDeleted" = true),
    (SELECT count(*) FROM "PlaylistItems" WHERE "Id" IN (@PlaylistItemOneId, @PlaylistItemTwoId) AND "TrackId" = @WinnerId AND "IsDeleted" = false),
    (SELECT count(*) FROM "PlaybackQueue" WHERE "Id" IN (@QueueItemOneId, @QueueItemTwoId) AND "TrackId" = @WinnerId AND "IsDeleted" = false),
    (SELECT count(*) FROM "TrackRequests" WHERE "Id" IN (@TrackRequestOneId, @TrackRequestTwoId) AND "MatchedTrackId" = @WinnerId AND "IsDeleted" = false),
    (SELECT count(*) FROM "Tracks" WHERE "Id" IN (@LoserOneId, @LoserTwoId) AND "IsDeleted" = true);""",
                        connection
                    )

                [ "BackendId", box backendId
                  "WinnerId", box winnerId
                  "LoserOneId", box loserOneId
                  "LoserTwoId", box loserTwoId
                  "PlaylistItemOneId", box playlistItemOneId
                  "PlaylistItemTwoId", box playlistItemTwoId
                  "QueueItemOneId", box queueItemOneId
                  "QueueItemTwoId", box queueItemTwoId
                  "TrackRequestOneId", box trackRequestOneId
                  "TrackRequestTwoId", box trackRequestTwoId
                  "LoserOneLinkId", box loserOneLinkId
                  "LoserTwoLinkId", box loserTwoLinkId ]
                |> List.iter (fun (name, value) -> check.Parameters.AddWithValue(name, value) |> ignore)

                let! reader = check.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True, "The migration verification query must return one aggregate row.")
                Assert.That(reader.GetInt64(0), Is.EqualTo(1L), "Exactly one active candidate Track must remain.")
                Assert.That(reader.GetInt64(1), Is.EqualTo(1L), "The earliest TrackFile owner must deterministically win.")
                Assert.That(reader.GetInt64(2), Is.EqualTo(1L), "Exactly one active TrackFile may remain for a backend/path collision.")
                Assert.That(reader.GetInt64(3), Is.EqualTo(1L), "The active TrackFile must belong to the deterministic winner.")
                Assert.That(reader.GetInt64(4), Is.EqualTo(1L), "Exactly one active TrackLink may remain for the shared URL.")
                Assert.That(reader.GetInt64(5), Is.EqualTo(1L), "The winner must retain the shared active TrackLink.")
                Assert.That(reader.GetInt64(6), Is.EqualTo(2L), "Conflicting loser TrackLinks must be soft deleted.")
                Assert.That(reader.GetInt64(7), Is.EqualTo(2L), "PlaylistItems must be rewired to the winner.")
                Assert.That(reader.GetInt64(8), Is.EqualTo(2L), "PlaybackQueue rows must be rewired to the winner.")
                Assert.That(reader.GetInt64(9), Is.EqualTo(2L), "TrackRequests must be rewired to the winner.")
                Assert.That(reader.GetInt64(10), Is.EqualTo(2L), "Loser Tracks must be soft deleted.")
            })
