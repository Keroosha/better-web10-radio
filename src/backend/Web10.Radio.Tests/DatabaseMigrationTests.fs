namespace Web10.Radio.Tests

open System
open System.Threading
open Npgsql
open System.Text.RegularExpressions
open FluentMigrator.Runner
open Microsoft.Extensions.DependencyInjection
open NUnit.Framework
open Web10.Radio.Database
open Dodo.Primitives
open Web10.Radio.Application
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
                      "Banners"
                      "LibraryScanJobs"
                      "StreamNodeHeartbeats"
                      "AdminUsers"
                      "AdminSessions"
                      "StreamNodeControlState"
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
              "TrackAssets"
              "StorageBackends"
              "StorageSettings"
              "Playlists"
              "PlaylistItems"
              "PlaylistSchedules"
              "PlaylistSchedulerState"
              "PlaybackControlCommands"
              "PlaybackQueue"
              "TrackRequests"
              "SayMessages"
              "Payments"
              "DonationGoals"
              "SocialLinks"
              "Banners"
              "LibraryScanJobs"
              "StreamNodeHeartbeats"
              "AdminUsers"
              "AdminSessions"
              "StreamNodeControlState"
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
    let private readMigration004Columns (connection: NpgsqlConnection) =
        task {
            use command =
                new NpgsqlCommand(
                    """SELECT table_name, column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_schema = 'public'
  AND (
      table_name IN ('AdminUsers', 'AdminSessions', 'StreamNodeControlState')
      OR (table_name = 'LibraryScanJobs' AND column_name = 'DiscoveredCount')
      OR (table_name = 'Payments' AND column_name = 'PayerDisplayName')
  )
ORDER BY table_name, column_name;""",
                    connection
                )

            let! reader = command.ExecuteReaderAsync()
            use reader = reader
            let columns = ResizeArray<string * string * string * string>()
            let mutable hasRow = true

            while hasRow do
                let! next = reader.ReadAsync()
                hasRow <- next

                if next then
                    columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)))

            return Set.ofSeq columns
        }

    let private readMigration004IndexDefinitions (connection: NpgsqlConnection) =
        task {
            use command =
                new NpgsqlCommand(
                    """SELECT index_class.relname,
       table_class.relname,
       access_method.amname,
       index_definition.indisunique,
       (
           SELECT string_agg(
               pg_get_indexdef(index_definition.indexrelid, key_position, true),
               ', ' ORDER BY key_position
           )
           FROM generate_series(1, index_definition.indnkeyatts) AS key_position
       ),
       COALESCE(pg_get_expr(index_definition.indpred, index_definition.indrelid), '')
FROM pg_index AS index_definition
INNER JOIN pg_class AS index_class ON index_class.oid = index_definition.indexrelid
INNER JOIN pg_class AS table_class ON table_class.oid = index_definition.indrelid
INNER JOIN pg_namespace AS schema ON schema.oid = index_class.relnamespace
INNER JOIN pg_am AS access_method ON access_method.oid = index_class.relam
WHERE schema.nspname = 'public'
  AND index_class.relname IN (
      'UX_LibraryScanJobs_Active_DefaultBackend',
      'UX_LibraryScanJobs_Active_StorageBackend',
      'UX_Playlists_Active_Singleton',
      'UX_Playlists_Active_System_AllStorage',
      'UX_DonationGoals_Active_Singleton',
      'UX_AdminUsers_Active_NormalizedUsername',
      'UX_AdminSessions_Active_TokenHash',
      'IX_AdminSessions_Active_User_ExpiresAtUtc',
      'UX_StreamNodeControlState_Active_Singleton'
  )
ORDER BY index_class.relname;""",
                    connection
                )

            let! reader = command.ExecuteReaderAsync()
            use reader = reader
            let definitions = ResizeArray<string * string * string * bool * string * string>()
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
                          reader.GetString(5) |> normalizeCatalogExpression )
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

    let private postgresException (error: exn) =
        let rec findPostgresException (current: exn) =
            match current with
            | :? PostgresException as postgresError -> Some postgresError
            | _ ->
                match current.InnerException with
                | null -> None
                | inner -> findPostgresException inner

        findPostgresException error

    let private assertPostgresViolation
        (expectedSqlState: string)
        (contract: string)
        (execute: unit -> System.Threading.Tasks.Task<unit>)
        =
        task {
            let! failure =
                task {
                    try
                        do! execute ()
                        return None
                    with error ->
                        return Some error
                }

            match failure |> Option.bind postgresException with
            | Some postgresError ->
                Assert.That(
                    postgresError.SqlState,
                    Is.EqualTo(expectedSqlState),
                    sprintf "%s must surface PostgreSQL SQLSTATE %s." contract expectedSqlState
                )
            | None ->
                Assert.Fail(sprintf "%s must reject the violating write." contract)
        }

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
                        [ "AdminSessions", "AdminSessions_UserId_fkey", "FOREIGNKEYUserIdREFERENCESAdminUsersId"
                          "LibraryScanJobs", "LibraryScanJobs_StorageBackendId_fkey", "FOREIGNKEYStorageBackendIdREFERENCESStorageBackendsId"
                          "PlaybackControlCommands", "PlaybackControlCommands_QueueItemId_fkey", "FOREIGNKEYQueueItemIdREFERENCESPlaybackQueueId"
                          "PlaybackQueue", "PlaybackQueue_PlaylistId_fkey", "FOREIGNKEYPlaylistIdREFERENCESPlaylistsId"
                          "PlaybackQueue", "PlaybackQueue_PlaylistItemId_fkey", "FOREIGNKEYPlaylistItemIdREFERENCESPlaylistItemsId"
                          "PlaybackQueue", "PlaybackQueue_TrackId_fkey", "FOREIGNKEYTrackIdREFERENCESTracksId"
                          "PlaybackQueue", "PlaybackQueue_TrackRequestId_fkey", "FOREIGNKEYTrackRequestIdREFERENCESTrackRequestsId"
                          "PlaylistItems", "PlaylistItems_PlaylistId_fkey", "FOREIGNKEYPlaylistIdREFERENCESPlaylistsId"
                          "PlaylistItems", "PlaylistItems_TrackId_fkey", "FOREIGNKEYTrackIdREFERENCESTracksId"
                          "PlaylistSchedules", "PlaylistSchedules_PlaylistId_fkey", "FOREIGNKEYPlaylistIdREFERENCESPlaylistsId"
                          "PlaylistSchedulerState", "PlaylistSchedulerState_PlaylistId_fkey", "FOREIGNKEYPlaylistIdREFERENCESPlaylistsId"
                          "TrackAssets", "TrackAssets_TrackId_fkey", "FOREIGNKEYTrackIdREFERENCESTracksId"
                          "TrackFiles", "TrackFiles_StorageBackendId_fkey", "FOREIGNKEYStorageBackendIdREFERENCESStorageBackendsId"
                          "TrackFiles", "TrackFiles_TrackId_fkey", "FOREIGNKEYTrackIdREFERENCESTracksId"
                          "TrackLinks", "TrackLinks_TrackId_fkey", "FOREIGNKEYTrackIdREFERENCESTracksId"
                          "TrackRequests", "TrackRequests_MatchedTrackId_fkey", "FOREIGNKEYMatchedTrackIdREFERENCESTracksId" ]

                let expectedCheckConstraints =
                    Set.ofList
                        [ "DonationGoals", "DonationGoals_GoalStars_check"
                          "DonationGoals", "DonationGoals_RaisedStars_check"
                          "LibraryScanJobs", "CK_LibraryScanJobs_ClaimAttempt_NonNegative"
                          "LibraryScanJobs", "CK_LibraryScanJobs_DiscoveredCount_NonNegative"
                          "LibraryScanJobs", "LibraryScanJobs_Status_check"
                          "OutboxEvents", "OutboxEvents_Attempts_check"
                          "OutboxEvents", "OutboxEvents_Audience_check"
                          "OutboxEvents", "OutboxEvents_Status_check"
                          "Payments", "Payments_AmountStars_check"
                          "Payments", "Payments_Currency_check"
                          "Payments", "Payments_ProviderToken_check"
                          "Payments", "Payments_Purpose_check"
                          "Payments", "Payments_Status_check"
                          "PlaybackControlCommands", "PlaybackControlCommands_Action_check"
                          "PlaybackControlCommands", "PlaybackControlCommands_ClaimAttempt_check"
                          "PlaybackQueue", "CK_PlaybackQueue_ClaimAttempt_NonNegative"
                          "PlaybackQueue", "PlaybackQueue_PlaylistId_Source_check"
                          "PlaybackQueue", "PlaybackQueue_Source_check"
                          "PlaybackQueue", "PlaybackQueue_Status_check"
                          "PlaylistItems", "PlaylistItems_Position_check"
                          "PlaylistSchedules", "PlaylistSchedules_DaysOfWeek_check"
                          "PlaylistSchedules", "PlaylistSchedules_DateRange_check"
                          "PlaylistSchedules", "PlaylistSchedules_TimeZoneId_check"
                          "PlaylistSchedulerState", "PlaylistSchedulerState_Cursor_check"
                          "PlaylistSchedulerState", "PlaylistSchedulerState_SongsSinceLast_check"
                          "Playlists", "Playlists_Cadence_check"
                          "Playlists", "Playlists_Order_check"
                          "Playlists", "Playlists_Source_check"
                          "Playlists", "Playlists_Type_check"
                          "Playlists", "Playlists_Weight_check"
                          "SayMessages", "SayMessages_AmountStars_check"
                          "SayMessages", "SayMessages_Status_check"
                          "SocialLinks", "SocialLinks_Kind_check"
                          "SocialLinks", "SocialLinks_Position_check"
                          "Banners", "Banners_Type_check"
                          "Banners", "Banners_Style_check"
                          "Banners", "Banners_ScreenPosition_check"
                          "Banners", "Banners_SortOrder_check"
                          "StorageBackends", "StorageBackends_Type_check"
                          "StorageSettings", "CK_StorageSettings_S3CacheMaxBytes_Positive"
                          "StorageSettings", "CK_StorageSettings_PresignTtlSeconds_Positive"
                          "StorageSettings", "CK_StorageSettings_SingletonKey_Primary"
                          "StreamNodeHeartbeats", "StreamNodeHeartbeats_Status_check"
                          "StreamNodeControlState", "CK_StreamNodeControlState_DesiredState"
                          "StreamNodeControlState", "CK_StreamNodeControlState_RestartGeneration_NonNegative"
                          "StreamNodeControlState", "CK_StreamNodeControlState_SingletonKey_Primary"
                          "TrackAssets", "TrackAssets_EmbeddedManual_check"
                          "TrackAssets", "TrackAssets_Kind_check"
                          "TrackAssets", "TrackAssets_Source_check"
                          "TrackFiles", "TrackFiles_SizeBytes_check"
                          "TrackFiles", "CK_TrackFiles_CueSegment_AllOrNone"
                          "TrackFiles", "CK_TrackFiles_CueSegment_Values"
                          "TrackLinks", "TrackLinks_Kind_check"
                          "TrackRequests", "TrackRequests_Status_check"
                          "Tracks", "Tracks_DurationMs_check"
                          "Tracks", "Tracks_MetadataSource_check" ]

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
                          "UX_TrackFiles_Active_Backend_StoragePath_Cue", "IsDeleted=falseANDStorageBackendIdISNOTNULL"
                          "UX_TrackFiles_Active_NullBackend_StoragePath_Cue", "IsDeleted=falseANDStorageBackendIdISNULL"
                          "UX_Playlists_Active_Name", "IsDeleted=false"
                          "IX_PlaylistItems_Active_PlaylistId", "IsDeleted=false"
                          "IX_PlaylistSchedules_Active_PlaylistId", "IsDeleted=false"
                          "UX_PlaylistItems_Active_PlaylistId_Position", "IsDeleted=false"
                          "IX_TrackRequests_Active_Status_RequestedAtUtc", "IsDeleted=false"
                          "IX_PlaybackQueue_Active_Claim", "IsDeleted=falseANDStatus='Queued'"
                          "IX_PlaybackQueue_Active_PlaylistId_Status", "IsDeleted=false"
                          "IX_PlaybackQueue_Active_Status", "IsDeleted=false"
                          "IX_PlaybackQueue_Active_ClaimLease", "IsDeleted=falseANDStatusIN'Claimed','Playing'"
                          "UX_PlaybackQueue_Active_TrackRequest", "IsDeleted=falseANDTrackRequestIdISNOTNULL"
                          "IX_SayMessages_Active_Status_SubmittedAtUtc", "IsDeleted=false"
                          "UX_Payments_Active_TelegramPaymentChargeId", "IsDeleted=falseANDTelegramPaymentChargeIdISNOTNULL"
                          "UX_Payments_Active_InvoicePayload", "IsDeleted=false"
                          "UX_Payments_Active_PurposeEntity", "IsDeleted=falseANDPurposeEntityIdISNOTNULL"
                          "IX_Payments_Active_Status", "IsDeleted=false"
                          "IX_DonationGoals_Active_IsActive", "IsDeleted=false"
                          "UX_DonationGoals_Active_Singleton", "IsDeleted=falseANDIsActive=true"
                          "IX_SocialLinks_Active_Position", "IsDeleted=false"
                          "IX_SocialLinks_Active_Featured", "IsDeleted=false"
                          "IX_Banners_Active_SortOrder", "IsDeleted=false"
                          "IX_LibraryScanJobs_Active_Status_RequestedAtUtc", "IsDeleted=false"
                          "IX_LibraryScanJobs_Active_ClaimLease", "IsDeleted=falseANDStatusIN'Queued','Running'"
                          "IX_StreamNodeHeartbeats_Active_HeartbeatAtUtc", "IsDeleted=false"
                          "UX_LibraryScanJobs_Active_DefaultBackend", "IsDeleted=falseANDStorageBackendIdISNULLANDStatusIN'Queued','Running'"
                          "UX_LibraryScanJobs_Active_StorageBackend", "IsDeleted=falseANDStorageBackendIdISNOTNULLANDStatusIN'Queued','Running'"
                          "IX_OutboxEvents_Active_Status_NextAttemptAtUtc", "IsDeleted=false"
                          "UX_StreamNodeControlState_Active_Singleton", "IsDeleted=falseANDSingletonKey='primary'"
                          "UX_StorageSettings_Active_Singleton", "IsDeleted=falseANDSingletonKey='primary'"
                          "IX_PlaybackControlCommands_Active_Generation", "IsDeleted=false"
                          "UX_Playlists_Active_System_AllStorage", "IsDeleted=falseANDIsActive=trueANDIsSystem=trueANDSource='AllStorage'"
                          "UX_TrackAssets_Active_Track_Kind", "IsDeleted=false"
                          "UX_AdminUsers_Active_NormalizedUsername", "IsDeleted=false"
                          "UX_AdminSessions_Active_TokenHash", "IsDeleted=falseANDRevokedAtUtcISNULL"
                          "IX_AdminSessions_Active_User_ExpiresAtUtc", "IsDeleted=falseANDRevokedAtUtcISNULL"
                          "IX_OutboxEvents_Active_GlobalOrder", "IsDeleted=falseANDStatus<>'Processed'"
                          "UX_TelegramUpdateInbox_Active_Update_Event", "IsDeleted=false" ]

                Assert.That((auditColumns = expectedAuditColumns), Is.True, sprintf "Audit-column drift. Expected: %A; actual: %A" expectedAuditColumns auditColumns)
                Assert.That((foreignKeys = expectedForeignKeys), Is.True, sprintf "Foreign-key drift. Expected: %A; actual: %A" expectedForeignKeys foreignKeys)
                let actualCheckConstraintNames = checkConstraints |> Set.map (fun (tableName, constraintName, _) -> tableName, constraintName)
                Assert.That((actualCheckConstraintNames = expectedCheckConstraints), Is.True, sprintf "Check-constraint drift. Expected: %A; actual: %A" expectedCheckConstraints actualCheckConstraintNames)
                Assert.That((activeIndexPredicates = expectedActiveIndexPredicates), Is.True, sprintf "Active-index predicate drift. Expected: %A; actual: %A" expectedActiveIndexPredicates activeIndexPredicates)
            })

    [<Test>]
    let ``202607100004 installs the exact admin scan and control catalog`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                let! columns = readMigration004Columns connection
                let! indexes = readMigration004IndexDefinitions connection

                let expectedColumns =
                    Set.ofList
                        [ "AdminSessions", "CreatedAtUtc", "timestamp with time zone", "NO"
                          "AdminSessions", "CsrfToken", "text", "NO"
                          "AdminSessions", "ExpiresAtUtc", "timestamp with time zone", "NO"
                          "AdminSessions", "Id", "uuid", "NO"
                          "AdminSessions", "IsDeleted", "boolean", "NO"
                          "AdminSessions", "RevokedAtUtc", "timestamp with time zone", "YES"
                          "AdminSessions", "TokenHash", "bytea", "NO"
                          "AdminSessions", "UpdatedAtUtc", "timestamp with time zone", "NO"
                          "AdminSessions", "UserId", "uuid", "NO"
                          "AdminUsers", "CreatedAtUtc", "timestamp with time zone", "NO"
                          "AdminUsers", "Id", "uuid", "NO"
                          "AdminUsers", "IsDeleted", "boolean", "NO"
                          "AdminUsers", "NormalizedUsername", "text", "NO"
                          "AdminUsers", "PasswordHash", "text", "NO"
                          "AdminUsers", "UpdatedAtUtc", "timestamp with time zone", "NO"
                          "AdminUsers", "Username", "text", "NO"
                          "LibraryScanJobs", "DiscoveredCount", "integer", "NO"
                          "Payments", "PayerDisplayName", "text", "YES"
                          "StreamNodeControlState", "CreatedAtUtc", "timestamp with time zone", "NO"
                          "StreamNodeControlState", "DesiredState", "text", "NO"
                          "StreamNodeControlState", "Id", "uuid", "NO"
                          "StreamNodeControlState", "IsDeleted", "boolean", "NO"
                          "StreamNodeControlState", "RestartGeneration", "integer", "NO"
                          "StreamNodeControlState", "SingletonKey", "text", "NO"
                          "StreamNodeControlState", "UpdatedAtUtc", "timestamp with time zone", "NO" ]

                let expectedIndexes =
                    Set.ofList
                        [ "IX_AdminSessions_Active_User_ExpiresAtUtc", "AdminSessions", "btree", false, "UserId,ExpiresAtUtc", "IsDeleted=falseANDRevokedAtUtcISNULL"
                          "UX_AdminSessions_Active_TokenHash", "AdminSessions", "btree", true, "TokenHash", "IsDeleted=falseANDRevokedAtUtcISNULL"
                          "UX_AdminUsers_Active_NormalizedUsername", "AdminUsers", "btree", true, "NormalizedUsername", "IsDeleted=false"
                          "UX_DonationGoals_Active_Singleton", "DonationGoals", "btree", true, "IsActive", "IsDeleted=falseANDIsActive=true"
                          "UX_LibraryScanJobs_Active_DefaultBackend", "LibraryScanJobs", "btree", true, "1", "IsDeleted=falseANDStorageBackendIdISNULLANDStatusIN'Queued','Running'"
                          "UX_LibraryScanJobs_Active_StorageBackend", "LibraryScanJobs", "btree", true, "StorageBackendId", "IsDeleted=falseANDStorageBackendIdISNOTNULLANDStatusIN'Queued','Running'"
                          "UX_Playlists_Active_System_AllStorage", "Playlists", "btree", true, "1", "IsDeleted=falseANDIsActive=trueANDIsSystem=trueANDSource='AllStorage'"
                          "UX_StreamNodeControlState_Active_Singleton", "StreamNodeControlState", "btree", true, "SingletonKey", "IsDeleted=falseANDSingletonKey='primary'" ]

                Assert.That(
                    (columns = expectedColumns),
                    Is.True,
                    sprintf "202607100004 column/nullability/type drift. Expected: %A; actual: %A" expectedColumns columns
                )

                Assert.That(
                    (indexes = expectedIndexes),
                    Is.True,
                    sprintf "202607100004 unique-index key/predicate drift. Expected: %A; actual: %A" expectedIndexes indexes
                )
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
                    Is.EqualTo(([ 202607080001L; 202607100001L; 202607100002L; 202607100003L; 202607100004L; 202607110001L; 202607110002L; 202607110003L; 202607110004L; 202607120001L; 202607120002L; 202607120003L; 202607130001L; 202607130002L; 202607130003L ] : int64 list) :> obj),
                    "A full down/up cycle must restore every migration in version order."
                )
            })

    [<Test>]
    let ``202607130003 reconciles the legacy FLAC CUE migration version collision`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                withMigrationRunner connectionString (fun runner -> runner.MigrateDown(202607130001L))

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                use legacyFlacSchema =
                    new NpgsqlCommand(
                        """ALTER TABLE "TrackFiles"
    ADD COLUMN "CueSheetPath" text NULL,
    ADD COLUMN "CueTrackNumber" integer NULL,
    ADD COLUMN "CueStartMs" integer NULL,
    ADD COLUMN "CueDurationMs" integer NULL;

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

INSERT INTO "VersionInfo" ("Version", "AppliedOn", "Description")
VALUES (202607130002, CURRENT_TIMESTAMP, 'Add logical FLAC CUE track segments');""",
                        connection
                    )

                let! _ = legacyFlacSchema.ExecuteNonQueryAsync()
                withMigrationRunner connectionString (fun runner -> runner.MigrateUp())

                use verification =
                    new NpgsqlCommand(
                        """SELECT
    (
        SELECT COUNT(*)
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'TrackFiles'
          AND column_name IN ('CueSheetPath', 'CueTrackNumber', 'CueStartMs', 'CueDurationMs')
    ) = 4,
    EXISTS (
        SELECT 1
        FROM "Banners"
        WHERE "Type" = 'superchat' AND "IsDeleted" = false
    ),
    EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'CK_TrackFiles_CueSegment_AllOrNone'
          AND conrelid = '"TrackFiles"'::regclass
    );""",
                        connection
                    )

                let! reader = verification.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True)
                Assert.That(reader.GetBoolean(0), Is.True, "Legacy FLAC CUE columns must remain available after reconciliation.")
                Assert.That(reader.GetBoolean(1), Is.True, "The skipped version-202607130002 banner migration must be reconciled.")
                Assert.That(reader.GetBoolean(2), Is.True, "Legacy FLAC CUE columns must receive their missing schema invariant.")
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
VALUES (@ActiveQueueItemId, @ActiveTrackId, 'fallback', 'Playing', @ActiveStartedAtUtc, @NowUtc, @NowUtc, @NowUtc, false),
       (@DeletedQueueItemId, @DeletedTrackId, 'fallback', 'Playing', @DeletedStartedAtUtc, @NowUtc, @NowUtc, @NowUtc, false);""",
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
                let timeProvider =
                    { new TimeProvider() with
                        member _.GetUtcNow() = nowUtc }
                let! heartbeatResult =
                    StreamNodeHeartbeatRepository.insertHeartbeat dataSource (Guid.NewGuid()) "Live" nowUtc None "{}" CancellationToken.None

                match heartbeatResult with
                | Error error -> Assert.Fail(sprintf "Expected heartbeat setup success, got %A." error)
                | Ok () -> ()

                let! streamFile = PlayerStateReadModel.loadStreamFile dataSource timeProvider CancellationToken.None

                match streamFile with
                | Ok(Some file) -> Assert.That(file.CachePath, Is.EqualTo(activeCachePath))
                | Ok None -> Assert.Fail("Expected the active parent track to provide a stream file.")
                | Error error -> Assert.Fail(sprintf "Expected stream-file read success, got %A." error)
            })

    [<Test>]
    let ``UUIDv7 identifiers retain version bits after PostgreSQL uuid persistence`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let generatedId = Uuid.CreateVersion7().ToGuidBigEndian()
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

    [<Test>]
    let ``202607100004 retains the oldest active scan per backend and fences later active duplicates`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                withMigrationRunner connectionString (fun runner -> runner.MigrateDown(202607100003L))

                let backendId = Guid.Parse("00000000-0000-0000-0000-000000000401")
                let defaultWinnerId = Guid.Parse("00000000-0000-0000-0000-000000000402")
                let defaultLoserId = Guid.Parse("00000000-0000-0000-0000-000000000403")
                let backendWinnerId = Guid.Parse("00000000-0000-0000-0000-000000000404")
                let backendLoserId = Guid.Parse("00000000-0000-0000-0000-000000000405")
                let requestedAtUtc = DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                use seed =
                    new NpgsqlCommand(
                        """INSERT INTO "StorageBackends" ("Id", "Name", "Type", "S3Bucket", "IsEnabled", "IsDeleted")
VALUES (@BackendId, 'migration-scan-backend', 'S3', 'migration-bucket', true, false);
INSERT INTO "LibraryScanJobs" ("Id", "StorageBackendId", "Status", "RequestedAtUtc", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES (@DefaultWinnerId, NULL, 'Queued', @DefaultWinnerRequestedAtUtc, @DefaultWinnerRequestedAtUtc, @DefaultWinnerRequestedAtUtc, false),
       (@DefaultLoserId, NULL, 'Running', @DefaultLoserRequestedAtUtc, @DefaultLoserRequestedAtUtc, @DefaultLoserRequestedAtUtc, false),
       (@BackendWinnerId, @BackendId, 'Running', @BackendWinnerRequestedAtUtc, @BackendWinnerRequestedAtUtc, @BackendWinnerRequestedAtUtc, false),
       (@BackendLoserId, @BackendId, 'Queued', @BackendLoserRequestedAtUtc, @BackendLoserRequestedAtUtc, @BackendLoserRequestedAtUtc, false);""",
                        connection
                    )

                [ "BackendId", box backendId
                  "DefaultWinnerId", box defaultWinnerId
                  "DefaultLoserId", box defaultLoserId
                  "BackendWinnerId", box backendWinnerId
                  "BackendLoserId", box backendLoserId
                  "DefaultWinnerRequestedAtUtc", box requestedAtUtc
                  "DefaultLoserRequestedAtUtc", box (requestedAtUtc.AddMinutes(1.0))
                  "BackendWinnerRequestedAtUtc", box (requestedAtUtc.AddMinutes(2.0))
                  "BackendLoserRequestedAtUtc", box (requestedAtUtc.AddMinutes(3.0)) ]
                |> List.iter (fun (name, value) -> seed.Parameters.AddWithValue(name, value) |> ignore)

                let! _ = seed.ExecuteNonQueryAsync()
                withMigrationRunner connectionString (fun runner -> runner.MigrateUp())

                use normalized =
                    new NpgsqlCommand(
                        """SELECT "Id", "Status", "FailureReason"
FROM "LibraryScanJobs"
WHERE "Id" IN (@DefaultWinnerId, @DefaultLoserId, @BackendWinnerId, @BackendLoserId)
ORDER BY "Id";""",
                        connection
                    )

                [ "DefaultWinnerId", box defaultWinnerId
                  "DefaultLoserId", box defaultLoserId
                  "BackendWinnerId", box backendWinnerId
                  "BackendLoserId", box backendLoserId ]
                |> List.iter (fun (name, value) -> normalized.Parameters.AddWithValue(name, value) |> ignore)

                let! reader = normalized.ExecuteReaderAsync()
                use reader = reader
                let actual = ResizeArray<Guid * string * string option>()
                let mutable hasRow = true

                while hasRow do
                    let! next = reader.ReadAsync()
                    hasRow <- next

                    if next then
                        actual.Add(
                            ( reader.GetGuid(0),
                              reader.GetString(1),
                              if reader.IsDBNull(2) then None else Some(reader.GetString(2)) )
                        )

                Assert.That(
                    Set.ofSeq actual,
                    Is.EqualTo(
                        Set.ofList
                            [ defaultWinnerId, "Queued", None
                              defaultLoserId, "Failed", Some "superseded by migration"
                              backendWinnerId, "Running", None
                              backendLoserId, "Failed", Some "superseded by migration" ]
                        :> obj
                    ),
                    "Migration normalization must retain the oldest active scan in each null/non-null backend partition and fail every later duplicate."
                )
                reader.Dispose()

                let insertActiveJob jobId storageBackendId =
                    task {
                        use insert =
                            new NpgsqlCommand(
                                """INSERT INTO "LibraryScanJobs" ("Id", "StorageBackendId", "Status", "DiscoveredCount", "RequestedAtUtc", "IsDeleted")
VALUES (@Id, @StorageBackendId, 'Queued', 0, @RequestedAtUtc, false);""",
                                connection
                            )

                        insert.Parameters.AddWithValue("Id", jobId) |> ignore

                        match storageBackendId with
                        | Some value -> insert.Parameters.AddWithValue("StorageBackendId", value) |> ignore
                        | None -> insert.Parameters.AddWithValue("StorageBackendId", DBNull.Value) |> ignore

                        insert.Parameters.AddWithValue("RequestedAtUtc", requestedAtUtc.AddHours(1.0)) |> ignore
                        let! _ = insert.ExecuteNonQueryAsync()
                        return ()
                    }

                do!
                    assertPostgresViolation
                        "23505"
                        "A second active default-backend LibraryScanJob"
                        (fun () -> insertActiveJob (Guid.Parse("00000000-0000-0000-0000-000000000406")) None)

                do!
                    assertPostgresViolation
                        "23505"
                        "A second active explicit-backend LibraryScanJob"
                        (fun () -> insertActiveJob (Guid.Parse("00000000-0000-0000-0000-000000000407")) (Some backendId))
            })

    [<Test>]
    let ``202607100004 normalizes playlist and donation singletons before enforcing active uniqueness`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                withMigrationRunner connectionString (fun runner -> runner.MigrateDown(202607100003L))

                let playlistWinnerId = Guid.Parse("00000000-0000-0000-0000-000000000501")
                let playlistLoserOneId = Guid.Parse("00000000-0000-0000-0000-000000000502")
                let playlistLoserTwoId = Guid.Parse("00000000-0000-0000-0000-000000000503")
                let goalOldId = Guid.Parse("00000000-0000-0000-0000-000000000511")
                let goalUpdatedWinnerId = Guid.Parse("00000000-0000-0000-0000-000000000512")
                let goalCreatedWinnerId = Guid.Parse("00000000-0000-0000-0000-000000000513")
                let goalIdTieWinnerId = Guid.Parse("00000000-0000-0000-0000-000000000514")
                let timestamp = DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                use seed =
                    new NpgsqlCommand(
                        """INSERT INTO "Playlists" ("Id", "Name", "IsActive", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES (@PlaylistWinnerId, 'migration playlist winner', true, @PlaylistWinnerCreatedAtUtc, @PlaylistWinnerCreatedAtUtc, false),
       (@PlaylistLoserOneId, 'migration playlist loser one', true, @PlaylistLoserOneCreatedAtUtc, @PlaylistLoserOneCreatedAtUtc, false),
       (@PlaylistLoserTwoId, 'migration playlist loser two', true, @PlaylistLoserTwoCreatedAtUtc, @PlaylistLoserTwoCreatedAtUtc, false);
INSERT INTO "DonationGoals" ("Id", "Title", "GoalStars", "RaisedStars", "IsActive", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES (@GoalOldId, 'migration goal old', 1, 0, true, @GoalOldCreatedAtUtc, @GoalOldUpdatedAtUtc, false),
       (@GoalUpdatedWinnerId, 'migration goal updated winner', 1, 0, true, @GoalUpdatedWinnerCreatedAtUtc, @GoalUpdatedWinnerUpdatedAtUtc, false),
       (@GoalCreatedWinnerId, 'migration goal created winner', 1, 0, true, @GoalCreatedWinnerCreatedAtUtc, @GoalCreatedWinnerUpdatedAtUtc, false),
       (@GoalIdTieWinnerId, 'migration goal id winner', 1, 0, true, @GoalIdTieWinnerCreatedAtUtc, @GoalIdTieWinnerUpdatedAtUtc, false);""",
                        connection
                    )

                [ "PlaylistWinnerId", box playlistWinnerId
                  "PlaylistLoserOneId", box playlistLoserOneId
                  "PlaylistLoserTwoId", box playlistLoserTwoId
                  "GoalOldId", box goalOldId
                  "GoalUpdatedWinnerId", box goalUpdatedWinnerId
                  "GoalCreatedWinnerId", box goalCreatedWinnerId
                  "GoalIdTieWinnerId", box goalIdTieWinnerId
                  "PlaylistWinnerCreatedAtUtc", box timestamp
                  "PlaylistLoserOneCreatedAtUtc", box (timestamp.AddMinutes(1.0))
                  "PlaylistLoserTwoCreatedAtUtc", box (timestamp.AddMinutes(2.0))
                  "GoalOldCreatedAtUtc", box timestamp
                  "GoalOldUpdatedAtUtc", box timestamp
                  "GoalUpdatedWinnerCreatedAtUtc", box timestamp
                  "GoalUpdatedWinnerUpdatedAtUtc", box (timestamp.AddMinutes(1.0))
                  "GoalCreatedWinnerCreatedAtUtc", box (timestamp.AddMinutes(1.0))
                  "GoalCreatedWinnerUpdatedAtUtc", box (timestamp.AddMinutes(1.0))
                  "GoalIdTieWinnerCreatedAtUtc", box (timestamp.AddMinutes(1.0))
                  "GoalIdTieWinnerUpdatedAtUtc", box (timestamp.AddMinutes(1.0)) ]
                |> List.iter (fun (name, value) -> seed.Parameters.AddWithValue(name, value) |> ignore)

                let! _ = seed.ExecuteNonQueryAsync()
                withMigrationRunner connectionString (fun runner -> runner.MigrateUp())

                use normalized =
                    new NpgsqlCommand(
                        """SELECT 'playlist', "Id", "IsActive"
FROM "Playlists"
WHERE "Id" IN (@PlaylistWinnerId, @PlaylistLoserOneId, @PlaylistLoserTwoId)
UNION ALL
SELECT 'goal', "Id", "IsActive"
FROM "DonationGoals"
WHERE "Id" IN (@GoalOldId, @GoalUpdatedWinnerId, @GoalCreatedWinnerId, @GoalIdTieWinnerId);""",
                        connection
                    )

                [ "PlaylistWinnerId", box playlistWinnerId
                  "PlaylistLoserOneId", box playlistLoserOneId
                  "PlaylistLoserTwoId", box playlistLoserTwoId
                  "GoalOldId", box goalOldId
                  "GoalUpdatedWinnerId", box goalUpdatedWinnerId
                  "GoalCreatedWinnerId", box goalCreatedWinnerId
                  "GoalIdTieWinnerId", box goalIdTieWinnerId ]
                |> List.iter (fun (name, value) -> normalized.Parameters.AddWithValue(name, value) |> ignore)

                let! reader = normalized.ExecuteReaderAsync()
                use reader = reader
                let actual = ResizeArray<string * Guid * bool>()
                let mutable hasRow = true

                while hasRow do
                    let! next = reader.ReadAsync()
                    hasRow <- next

                    if next then
                        actual.Add((reader.GetString(0), reader.GetGuid(1), reader.GetBoolean(2)))

                Assert.That(
                    Set.ofSeq actual,
                    Is.EqualTo(
                        Set.ofList
                            [ "playlist", playlistWinnerId, true
                              "playlist", playlistLoserOneId, false
                              "playlist", playlistLoserTwoId, false
                              "goal", goalOldId, false
                              "goal", goalUpdatedWinnerId, false
                              "goal", goalCreatedWinnerId, false
                              "goal", goalIdTieWinnerId, true ]
                        :> obj
                    ),
                    "Migration normalization must retain the oldest active Playlist and the newest DonationGoal by UpdatedAtUtc, CreatedAtUtc, and Id."
                )

                reader.Dispose()

                let insertActivePlaylist () =
                    task {
                        use insert =
                            new NpgsqlCommand(
                                """INSERT INTO "Playlists" ("Id", "Name", "IsActive", "IsSystem", "Source", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES ('00000000-0000-0000-0000-000000000504', 'migration playlist uniqueness probe', true, true, 'AllStorage', @Timestamp, @Timestamp, false);""",
                                connection
                            )

                        insert.Parameters.AddWithValue("Timestamp", timestamp.AddHours(1.0)) |> ignore
                        let! _ = insert.ExecuteNonQueryAsync()
                        return ()
                    }

                let insertSecondActivePlaylist () =
                    task {
                        use insert =
                            new NpgsqlCommand(
                                """INSERT INTO "Playlists" ("Id", "Name", "IsActive", "IsSystem", "Source", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES ('00000000-0000-0000-0000-000000000505', 'migration playlist uniqueness probe two', true, true, 'AllStorage', @Timestamp, @Timestamp, false);""",
                                connection
                            )

                        insert.Parameters.AddWithValue("Timestamp", timestamp.AddHours(2.0)) |> ignore
                        let! _ = insert.ExecuteNonQueryAsync()
                        return ()
                    }

                let insertActiveGoal () =
                    task {
                        use insert =
                            new NpgsqlCommand(
                                """INSERT INTO "DonationGoals" ("Id", "Title", "GoalStars", "RaisedStars", "IsActive", "CreatedAtUtc", "UpdatedAtUtc", "IsDeleted")
VALUES ('00000000-0000-0000-0000-000000000515', 'migration goal uniqueness probe', 1, 0, true, @Timestamp, @Timestamp, false);""",
                                connection
                            )

                        insert.Parameters.AddWithValue("Timestamp", timestamp.AddHours(1.0)) |> ignore
                        let! _ = insert.ExecuteNonQueryAsync()
                        return ()
                    }

                do! insertActivePlaylist ()
                do! assertPostgresViolation "23505" "A second active system AllStorage Playlist" insertSecondActivePlaylist
                do! assertPostgresViolation "23505" "A second active DonationGoal" insertActiveGoal
            })

    [<Test>]
    let ``202607100004 enforces active admin session identity and stream-control state invariants`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                let userId = Guid.Parse("00000000-0000-0000-0000-000000000601")
                let firstSessionId = Guid.Parse("00000000-0000-0000-0000-000000000602")
                let controlStateId = Guid.Parse("00000000-0000-0000-0000-000000000603")
                let paymentId = Guid.Parse("00000000-0000-0000-0000-000000000604")
                let tokenHash = [| 1uy; 2uy; 3uy; 4uy |]
                let timestamp = DateTimeOffset(2026, 7, 10, 10, 0, 0, TimeSpan.Zero)

                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                use seed =
                    new NpgsqlCommand(
                        """INSERT INTO "AdminUsers" ("Id", "Username", "NormalizedUsername", "PasswordHash", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@UserId, 'migration-admin', 'MIGRATION-ADMIN', 'hash', false, @Timestamp, @Timestamp);
INSERT INTO "AdminSessions" ("Id", "UserId", "TokenHash", "CsrfToken", "ExpiresAtUtc", "RevokedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@FirstSessionId, @UserId, @TokenHash, 'csrf-token', @ExpiresAtUtc, NULL, false, @Timestamp, @Timestamp);
INSERT INTO "StreamNodeControlState" ("Id", "SingletonKey", "DesiredState", "RestartGeneration", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@ControlStateId, 'primary', 'Running', 0, false, @Timestamp, @Timestamp);
INSERT INTO "Payments" ("Id", "TelegramUserId", "PayerDisplayName", "Purpose", "AmountStars", "TelegramInvoicePayload", "Status", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@PaymentId, 77, 'migration payer snapshot', 'Donation', 1, 'migration-payer-display-name', 'InvoiceCreated', false, @Timestamp, @Timestamp);""",
                        connection
                    )

                [ "UserId", box userId
                  "FirstSessionId", box firstSessionId
                  "ControlStateId", box controlStateId
                  "PaymentId", box paymentId
                  "TokenHash", box tokenHash
                  "Timestamp", box timestamp
                  "ExpiresAtUtc", box (timestamp.AddHours(8.0)) ]
                |> List.iter (fun (name, value) -> seed.Parameters.AddWithValue(name, value) |> ignore)

                let! _ = seed.ExecuteNonQueryAsync()

                let insertUser id username normalizedUsername isDeleted =
                    task {
                        use insert =
                            new NpgsqlCommand(
                                """INSERT INTO "AdminUsers" ("Id", "Username", "NormalizedUsername", "PasswordHash", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @Username, @NormalizedUsername, 'hash', @IsDeleted, @Timestamp, @Timestamp);""",
                                connection
                            )

                        insert.Parameters.AddWithValue("Id", id) |> ignore
                        insert.Parameters.AddWithValue("Username", username) |> ignore
                        insert.Parameters.AddWithValue("NormalizedUsername", normalizedUsername) |> ignore
                        insert.Parameters.AddWithValue("IsDeleted", isDeleted) |> ignore
                        insert.Parameters.AddWithValue("Timestamp", timestamp.AddMinutes(1.0)) |> ignore
                        let! _ = insert.ExecuteNonQueryAsync()
                        return ()
                    }

                let insertSession id sessionUserId sessionTokenHash =
                    task {
                        use insert =
                            new NpgsqlCommand(
                                """INSERT INTO "AdminSessions" ("Id", "UserId", "TokenHash", "CsrfToken", "ExpiresAtUtc", "RevokedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @UserId, @TokenHash, 'csrf-token', @ExpiresAtUtc, NULL, false, @Timestamp, @Timestamp);""",
                                connection
                            )

                        insert.Parameters.AddWithValue("Id", id) |> ignore
                        insert.Parameters.AddWithValue("UserId", sessionUserId) |> ignore
                        insert.Parameters.AddWithValue("TokenHash", sessionTokenHash) |> ignore
                        insert.Parameters.AddWithValue("ExpiresAtUtc", timestamp.AddHours(8.0)) |> ignore
                        insert.Parameters.AddWithValue("Timestamp", timestamp.AddMinutes(1.0)) |> ignore
                        let! _ = insert.ExecuteNonQueryAsync()
                        return ()
                    }

                do!
                    assertPostgresViolation
                        "23505"
                        "Two active AdminUsers with the same normalized username"
                        (fun () ->
                            insertUser
                                (Guid.Parse("00000000-0000-0000-0000-000000000605"))
                                "migration-admin-other-case"
                                "MIGRATION-ADMIN"
                                false)

                do!
                    assertPostgresViolation
                        "23503"
                        "An AdminSession whose UserId has no AdminUser"
                        (fun () ->
                            insertSession
                                (Guid.Parse("00000000-0000-0000-0000-000000000606"))
                                (Guid.Parse("00000000-0000-0000-0000-000000000699"))
                                [| 9uy |])

                do!
                    assertPostgresViolation
                        "23505"
                        "Two active AdminSessions with the same token hash"
                        (fun () ->
                            insertSession
                                (Guid.Parse("00000000-0000-0000-0000-000000000607"))
                                userId
                                tokenHash)

                use revoke =
                    new NpgsqlCommand(
                        """UPDATE "AdminSessions"
SET "RevokedAtUtc" = @RevokedAtUtc
WHERE "Id" = @FirstSessionId;""",
                        connection
                    )

                revoke.Parameters.AddWithValue("RevokedAtUtc", timestamp.AddMinutes(2.0)) |> ignore
                revoke.Parameters.AddWithValue("FirstSessionId", firstSessionId) |> ignore
                let! _ = revoke.ExecuteNonQueryAsync()

                do!
                    insertSession
                        (Guid.Parse("00000000-0000-0000-0000-000000000608"))
                        userId
                        tokenHash

                use sessionState =
                    new NpgsqlCommand(
                        """SELECT
    count(*) FILTER (WHERE "RevokedAtUtc" IS NULL AND "IsDeleted" = false),
    count(*) FILTER (WHERE "RevokedAtUtc" IS NOT NULL AND "IsDeleted" = false),
    (SELECT "PayerDisplayName" FROM "Payments" WHERE "Id" = @PaymentId)
FROM "AdminSessions"
WHERE "UserId" = @UserId;""",
                        connection
                    )

                sessionState.Parameters.AddWithValue("PaymentId", paymentId) |> ignore
                sessionState.Parameters.AddWithValue("UserId", userId) |> ignore
                let! reader = sessionState.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                Assert.That(hasRow, Is.True, "Session state verification must return one aggregate row.")
                Assert.That(reader.GetInt64(0), Is.EqualTo(1L), "Revoking a session must release its token hash only for one replacement active session.")
                Assert.That(reader.GetInt64(1), Is.EqualTo(1L), "The original session must remain auditable as revoked.")
                Assert.That(reader.GetString(2), Is.EqualTo("migration payer snapshot"), "Payments must retain the payer display-name snapshot.")
                reader.Dispose()

                use deactivateControlState =
                    new NpgsqlCommand(
                        """UPDATE "StreamNodeControlState"
SET "IsDeleted" = true
WHERE "Id" = @ControlStateId;""",
                        connection
                    )

                deactivateControlState.Parameters.AddWithValue("ControlStateId", controlStateId) |> ignore
                let! _ = deactivateControlState.ExecuteNonQueryAsync()

                let insertControlState id desiredState restartGeneration =
                    task {
                        use insert =
                            new NpgsqlCommand(
                                """INSERT INTO "StreamNodeControlState" ("Id", "SingletonKey", "DesiredState", "RestartGeneration", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, 'primary', @DesiredState, @RestartGeneration, false, @Timestamp, @Timestamp);""",
                                connection
                            )

                        insert.Parameters.AddWithValue("Id", id) |> ignore
                        insert.Parameters.AddWithValue("DesiredState", desiredState) |> ignore
                        insert.Parameters.AddWithValue("RestartGeneration", restartGeneration) |> ignore
                        insert.Parameters.AddWithValue("Timestamp", timestamp.AddMinutes(3.0)) |> ignore
                        let! _ = insert.ExecuteNonQueryAsync()
                        return ()
                    }

                do!
                    assertPostgresViolation
                        "23514"
                        "A StreamNodeControlState desired state outside Running|Paused|Stopped"
                        (fun () -> insertControlState (Guid.Parse("00000000-0000-0000-0000-000000000609")) "Suspended" 0)

                do!
                    assertPostgresViolation
                        "23514"
                        "A negative StreamNodeControlState restart generation"
                        (fun () -> insertControlState (Guid.Parse("00000000-0000-0000-0000-000000000610")) "Running" -1)

                do! insertControlState (Guid.Parse("00000000-0000-0000-0000-000000000611")) "Stopped" 2

                do!
                    assertPostgresViolation
                        "23505"
                        "A second active StreamNodeControlState singleton"
                        (fun () -> insertControlState (Guid.Parse("00000000-0000-0000-0000-000000000612")) "Running" 3)
            })

    [<Test>]
    let ``FLAC CUE migration adds logical segment columns constraints and indexes`` () =
        DatabaseTestSupport.withMigratedDatabase (fun connectionString ->
            task {
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()
                use columns =
                    new NpgsqlCommand(
                        """SELECT column_name FROM information_schema.columns
WHERE table_schema = 'public' AND table_name = 'TrackFiles'
  AND column_name IN ('CueSheetPath', 'CueTrackNumber', 'CueStartMs', 'CueDurationMs')
ORDER BY column_name;""",
                        connection
                    )
                use! reader = columns.ExecuteReaderAsync()
                let names = ResizeArray<string>()
                let mutable reading = true
                while reading do
                    let! found = reader.ReadAsync()
                    if found then names.Add(reader.GetString(0)) else reading <- false
                reader.Close()
                Assert.That(names |> Set.ofSeq, Is.EqualTo<Set<string>>(Set.ofList [ "CueSheetPath"; "CueTrackNumber"; "CueStartMs"; "CueDurationMs" ]))
                let cueTrack = Guid.NewGuid()
                let cueFile = Guid.NewGuid()
                use insertCue =
                    new NpgsqlCommand(
                        """INSERT INTO "Tracks" ("Id", "Title", "Artist", "MetadataSource", "IsDeleted")
VALUES (@TrackId, 'cue', 'artist', 'Cue', false);
INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "ContentType", "IsCached", "CueSheetPath", "CueTrackNumber", "CueStartMs", "CueDurationMs", "IsDeleted")
VALUES (@FileId, @TrackId, 'album.flac', 'audio/flac', false, 'album.cue', 1, 0, 1000, false);""",
                        connection
                    )
                insertCue.Parameters.AddWithValue("TrackId", cueTrack) |> ignore
                insertCue.Parameters.AddWithValue("FileId", cueFile) |> ignore
                let! _ = insertCue.ExecuteNonQueryAsync()
                use invalid =
                    new NpgsqlCommand(
                        """INSERT INTO "TrackFiles" ("Id", "TrackId", "StoragePath", "IsCached", "CueSheetPath", "CueTrackNumber", "CueStartMs", "CueDurationMs", "IsDeleted")
VALUES (@FileId, @TrackId, 'invalid.flac', false, 'album.cue', 2, 1000, 0, false);""",
                        connection
                    )
                invalid.Parameters.AddWithValue("FileId", Guid.NewGuid()) |> ignore
                invalid.Parameters.AddWithValue("TrackId", cueTrack) |> ignore
                let! violation =
                    task {
                        try
                            let! _ = invalid.ExecuteNonQueryAsync()
                            return None
                        with :? PostgresException as error -> return Some error.SqlState
                    }
                Assert.That(violation, Is.EqualTo(Some "23514"))
                use indexes =
                    new NpgsqlCommand(
                        """SELECT indexname FROM pg_indexes
WHERE schemaname = 'public'
  AND tablename = 'TrackFiles'
  AND indexname IN ('UX_TrackFiles_Active_Backend_StoragePath_Cue', 'UX_TrackFiles_Active_NullBackend_StoragePath_Cue');""",
                        connection
                    )
                use! indexReader = indexes.ExecuteReaderAsync()
                let indexNames = ResizeArray<string>()
                let mutable readingIndexes = true
                while readingIndexes do
                    let! found = indexReader.ReadAsync()
                    if found then indexNames.Add(indexReader.GetString(0)) else readingIndexes <- false
                Assert.That(indexNames, Has.Count.EqualTo(2))
            })
