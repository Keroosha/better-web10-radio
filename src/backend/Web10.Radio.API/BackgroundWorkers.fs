namespace Web10.Radio.API

open System
open Dodo.Primitives
open Web10.Radio.Application
open System.IO
open System.Text
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql
open Web10.Radio.Database
open Web10.Radio.Database.Repositories

[<RequireQualifiedAccess>]
module private BackgroundWorkerLog =
    let private currentTraceId () =
        let current = System.Diagnostics.Activity.Current

        if isNull current then
            String.Empty
        else
            let traceId = current.TraceId.ToString()
            if String.IsNullOrWhiteSpace traceId then String.Empty else traceId


    let private backgroundAgentFailedMessage =
        LoggerMessage.Define<string, Guid, string, string>(
            LogLevel.Error,
            EventId(3101, "BackgroundAgentFailed"),
            "Background agent {operation} failed for event {eventId} type {eventType} traceId {traceId}"
        )

    let private outboxFailureFenceRejectedMessage =
        LoggerMessage.Define<Guid, Guid, int, string>(
            LogLevel.Warning,
            EventId(3102, "OutboxFailureFenceRejected"),
            "Outbox failure fence rejected for event {eventId} claimOwner {claimOwner} claimAttempt {claimAttempt} traceId {traceId}"
        )

    let private outboxMarkFailedMessage =
        LoggerMessage.Define<Guid, Guid, int, string, string>(
            LogLevel.Error,
            EventId(3103, "OutboxMarkFailed"),
            "Outbox failure marking failed for event {eventId} claimOwner {claimOwner} claimAttempt {claimAttempt} traceId {traceId}: {error}"
        )

    let private operationFailedMessage =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Error,
            EventId(3104, "BackgroundOperationFailed"),
            "{operation} failed traceId {traceId}: {error}"
        )

    let errorKind = function
        | DomainEventError _ -> "domain_event"
        | RepositoryError _ -> "repository"
        | UnknownEventType _ -> "unknown_event_type"
        | InvalidPayload _ -> "invalid_payload"
        | StateTransitionRejected _ -> "state_transition_rejected"
        | UnexpectedException _ -> "unexpected_exception"
        | _ -> "background_worker"


    let backgroundAgentFailed (logger: ILogger) (error: exn) operation eventId eventType =
        backgroundAgentFailedMessage.Invoke(logger, operation, eventId, eventType, currentTraceId (), error)

    let outboxFailureFenceRejected (logger: ILogger) eventId claimOwner claimAttempt =
        outboxFailureFenceRejectedMessage.Invoke(logger, eventId, claimOwner, claimAttempt, currentTraceId (), null)

    let outboxMarkFailed (logger: ILogger) eventId claimOwner claimAttempt =
        outboxMarkFailedMessage.Invoke(logger, eventId, claimOwner, claimAttempt, currentTraceId (), "repository_error", null)

    let operationFailed (logger: ILogger) operation error =
        operationFailedMessage.Invoke(logger, operation, currentTraceId (), errorKind error, null)


module private JsonPayload =
    let objectWithStrings (fields: (string * string) list) =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))
        writer.WriteStartObject()

        for key, value in fields do
            writer.WriteString(key, value)

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(buffer.ToArray())

    let objectWithStringsAndRaw (fields: (string * string) list) (rawFields: (string * string) list) =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))
        writer.WriteStartObject()

        for key, value in fields do
            writer.WriteString(key, value)

        for key, rawValue in rawFields do
            writer.WritePropertyName(key)
            writer.WriteRawValue(rawValue)

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(buffer.ToArray())

    let parseObject eventType (payloadJson: string) =
        if String.IsNullOrWhiteSpace payloadJson then
            Error(InvalidPayload(eventType, "payload must be a JSON object."))
        else
            try
                use document = JsonDocument.Parse(payloadJson)

                if document.RootElement.ValueKind = JsonValueKind.Object then
                    Ok(document.RootElement.Clone())
                else
                    Error(InvalidPayload(eventType, "payload must be a JSON object."))
            with
            | :? JsonException as ex -> Error(InvalidPayload(eventType, ex.Message))

    let tryGetString (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String then
            let text = value.GetString()

            if String.IsNullOrWhiteSpace text then None else Some text
        else
            None

    let tryGetInt (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.Number then
            let mutable parsed = 0

            if value.TryGetInt32(&parsed) then Some parsed else None
        else
            None

    let tryGetInt64 (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.Number then
            let mutable parsed = 0L
            if value.TryGetInt64(&parsed) then Some parsed else None
        else
            None

    let tryGetObjectRaw (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.Object then
            Some(value.GetRawText())
        else
            None

module private EventPublishing =
    let publish
        (publisher: IDomainEventPublisher)
        (timeProvider: TimeProvider)
        (eventType: DomainEventType)
        (producer: string)
        (correlationId: Guid option)
        (causationId: Guid option)
        (payloadJson: string)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, BackgroundWorkerError>> =
        taskResult {
            let! envelope =
                DomainEventEnvelope.create timeProvider eventType producer correlationId causationId payloadJson
                |> Result.mapError DomainEventError

            do! publisher.PublishDurableAsync envelope cancellationToken
        }



module private StreamNodeAgent =
    let private parseFailurePayload eventType payloadJson =
        result {
            let! root = JsonPayload.parseObject eventType payloadJson
            return JsonPayload.tryGetString "reason" root
        }

    let private parseHeartbeatPayload eventType payloadJson =
        result {
            let! root = JsonPayload.parseObject eventType payloadJson
            let! status = JsonPayload.tryGetString "status" root |> Result.requireSome (InvalidPayload(eventType, "status is required."))
            let failureReason = JsonPayload.tryGetString "failureReason" root
            let metadataJson = JsonPayload.tryGetObjectRaw "metadata" root |> Option.defaultValue "{}"
            return status, failureReason, metadataJson
        }

    let recordFailure
        (dataSource: NpgsqlDataSource)
        (timeProvider: TimeProvider)
        (state: StreamNodeHeartbeatState)
        (envelope: DomainEventEnvelope)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, BackgroundWorkerError>> =
        taskResult {
            let eventType = DomainEventType.toString envelope.EventType
            let! reason = parseFailurePayload eventType envelope.PayloadJson
            let heartbeatAtUtc = timeProvider.GetUtcNow()

            do!
                StreamNodeHeartbeatRepository.insertHeartbeat
                    dataSource
                    (Uuid.CreateVersion7().ToGuidBigEndian())
                    "Degraded"
                    heartbeatAtUtc
                    reason
                    envelope.PayloadJson
                    cancellationToken
                |> TaskResult.mapError RepositoryError

            state.RecordHeartbeat(heartbeatAtUtc, reason)
            return ()
        }

    let recordHeartbeat
        (dataSource: NpgsqlDataSource)
        (timeProvider: TimeProvider)
        (state: StreamNodeHeartbeatState)
        (envelope: DomainEventEnvelope)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, BackgroundWorkerError>> =
        taskResult {
            let eventType = DomainEventType.toString envelope.EventType
            let! status, failureReason, metadataJson = parseHeartbeatPayload eventType envelope.PayloadJson
            let heartbeatAtUtc = timeProvider.GetUtcNow()

            do!
                StreamNodeHeartbeatRepository.insertHeartbeat
                    dataSource
                    (Uuid.CreateVersion7().ToGuidBigEndian())
                    status
                    heartbeatAtUtc
                    failureReason
                    metadataJson
                    cancellationToken
                |> TaskResult.mapError RepositoryError

            state.RecordHeartbeat(heartbeatAtUtc, failureReason)
            return ()
        }

module private PayloadValidation =
    let requireUuid eventType fieldName root =
        result {
            let! text =
                JsonPayload.tryGetString fieldName root
                |> Result.requireSome (InvalidPayload(eventType, sprintf "%s is required." fieldName))
            let mutable parsed = Guid.Empty
            do! Guid.TryParse(text, &parsed) |> Result.requireTrue (InvalidPayload(eventType, sprintf "%s must be a UUID." fieldName))
            return parsed
        }

    let requireString eventType fieldName root =
        JsonPayload.tryGetString fieldName root
        |> Result.requireSome (InvalidPayload(eventType, sprintf "%s is required." fieldName))
    let private requireProperty (eventType: string) (fieldName: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if root.TryGetProperty(fieldName, &value) then
            Ok value
        else
            Error(InvalidPayload(eventType, sprintf "%s is required." fieldName))

    let private requireInt (eventType: string) (fieldName: string) (root: JsonElement) =
        result {
            let! value = requireProperty eventType fieldName root
            let mutable parsed = 0

            if value.ValueKind = JsonValueKind.Number && value.TryGetInt32(&parsed) then
                return parsed
            else
                return! Error(InvalidPayload(eventType, sprintf "%s must be an integer." fieldName))
        }

    let private requireInt64 (eventType: string) (fieldName: string) (root: JsonElement) =
        result {
            let! value = requireProperty eventType fieldName root
            let mutable parsed = 0L

            if value.ValueKind = JsonValueKind.Number && value.TryGetInt64(&parsed) then
                return parsed
            else
                return! Error(InvalidPayload(eventType, sprintf "%s must be an integer." fieldName))
        }

    let private requireArray (eventType: string) (fieldName: string) (root: JsonElement) =
        result {
            let! value = requireProperty eventType fieldName root

            if value.ValueKind = JsonValueKind.Array then
                return ()
            else
                return! Error(InvalidPayload(eventType, sprintf "%s must be an array." fieldName))
        }

    let private requireNullableUuid (eventType: string) (fieldName: string) (root: JsonElement) =
        result {
            let! value = requireProperty eventType fieldName root

            if value.ValueKind = JsonValueKind.Null then
                return ()
            elif value.ValueKind = JsonValueKind.String then
                let! _ = requireUuid eventType fieldName root
                return ()
            else
                return! Error(InvalidPayload(eventType, sprintf "%s must be a UUID or null." fieldName))
        }

    let validateProjectionPayload domainEventType payloadJson =
        let eventType = DomainEventType.toString domainEventType
        result {
            let! root = JsonPayload.parseObject eventType payloadJson

            match domainEventType with
            | AdminGoalChanged ->
                let! _ = requireString eventType "title" root
                let! goalStars = requireInt eventType "goalStars" root
                do! (goalStars > 0) |> Result.requireTrue (InvalidPayload(eventType, "goalStars must be positive."))
                return ()
            | SocialLinkChanged ->
                let! count = requireInt eventType "count" root
                do! (count >= 0) |> Result.requireTrue (InvalidPayload(eventType, "count must be non-negative."))
                return ()
            | BannerChanged ->
                let! count = requireInt eventType "count" root
                do! (count >= 0) |> Result.requireTrue (InvalidPayload(eventType, "count must be non-negative."))
                return ()
            | PlaybackReordered ->
                do! requireArray eventType "queueItemIds" root
                return ()
            | PlaybackSkipped
            | PlaybackRestarted ->
                let! _ = requireUuid eventType "queueItemId" root
                let! _ = requireUuid eventType "claimOwner" root
                let! claimAttempt = requireInt eventType "claimAttempt" root
                do! (claimAttempt > 0) |> Result.requireTrue (InvalidPayload(eventType, "claimAttempt must be positive."))
                let! commandGeneration = requireInt64 eventType "commandGeneration" root
                do! (commandGeneration > 0L) |> Result.requireTrue (InvalidPayload(eventType, "commandGeneration must be positive."))
                return ()
            | TrackForcePlayed ->
                let! _ = requireUuid eventType "queueItemId" root
                let! _ = requireUuid eventType "trackId" root
                do! requireNullableUuid eventType "interruptedQueueItemId" root
                return ()
            | AdminTrackQueued ->
                let! _ = requireUuid eventType "queueItemId" root
                let! _ = requireUuid eventType "trackId" root
                return ()
            | TrackMetadataChanged ->
                let! _ = requireUuid eventType "trackId" root
                do! requireArray eventType "changedFields" root
                return ()
            | TrackMaterialized ->
                let! _ = requireUuid eventType "trackId" root
                let! _ = requireUuid eventType "trackFileId" root
                return ()
            | PlaylistChanged ->
                let! _ = requireUuid eventType "playlistId" root
                return ()
            | PlaylistTrackQueued ->
                let! _ = requireUuid eventType "playlistId" root
                do! requireNullableUuid eventType "playlistItemId" root
                let! _ = requireUuid eventType "trackId" root
                let! _ = requireUuid eventType "queueItemId" root
                let! source = requireString eventType "source" root
                do! (source = "playlist" || source = "jingle") |> Result.requireTrue (InvalidPayload(eventType, "source must be playlist or jingle."))
                let! reason = requireString eventType "reason" root
                do!
                    [ "general"; "oncePerSongs"; "oncePerMinutes"; "oncePerHour"; "interrupt" ]
                    |> List.contains reason
                    |> Result.requireTrue (InvalidPayload(eventType, "reason is invalid."))
                return ()
            | _ -> return! Error(UnknownEventType(DomainEventType.toString domainEventType))
        }


module private PlaybackQueueAgent =
    let handle (workflow: IPlaybackQueueWorkflow) envelope cancellationToken =
        workflow.HandleAsync envelope cancellationToken

module private LibraryScanAgent =
    let handle (workflow: ILibraryScanWorkflow) envelope cancellationToken =
        workflow.HandleAsync envelope cancellationToken

type private AgentWorkItem(envelope: DomainEventEnvelope, cancellationToken: CancellationToken) =
    let completion =
        TaskCompletionSource<Result<unit, BackgroundWorkerError>>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable state = 0

    member _.Envelope = envelope
    member _.CancellationToken = cancellationToken
    member _.Task = completion.Task
    member _.IsQueued = Volatile.Read(&state) = 0

    member _.TryStart() =
        Interlocked.CompareExchange(&state, 1, 0) = 0

    member _.TryCancel() =
        if Interlocked.CompareExchange(&state, 2, 0) = 0 then
            completion.TrySetCanceled(cancellationToken) |> ignore

    member _.Complete(result: Result<unit, BackgroundWorkerError>) =
        if Interlocked.Exchange(&state, 2) = 1 then
            completion.TrySetResult(result) |> ignore

    member _.CompleteCanceled() =
        if Interlocked.Exchange(&state, 2) = 1 then
            completion.TrySetCanceled(cancellationToken) |> ignore

type DomainEventDispatcher
    (
        dataSource: NpgsqlDataSource,
        timeProvider: TimeProvider,
        streamNodeState: StreamNodeHeartbeatState,
        playbackWorkflow: IPlaybackQueueWorkflow,
        libraryScanWorkflow: ILibraryScanWorkflow,
        applicationLifetime: IHostApplicationLifetime,
        logger: ILogger<DomainEventDispatcher>
    ) =
    let stoppingToken = applicationLifetime.ApplicationStopping

    let startAgent
        (operation: string)
        (handler: DomainEventEnvelope -> CancellationToken -> Task<Result<unit, BackgroundWorkerError>>)
        =
        MailboxProcessor.Start(
            (fun (inbox: MailboxProcessor<AgentWorkItem>) ->
                let rec loop () =
                    async {
                        try
                            let! work = inbox.Receive()

                            if work.TryStart() then
                                try
                                    let! result = handler work.Envelope work.CancellationToken |> Async.AwaitTask
                                    work.Complete(result)
                                with
                                | :? OperationCanceledException when work.CancellationToken.IsCancellationRequested ->
                                    work.CompleteCanceled()
                                | ex ->
                                    BackgroundWorkerLog.backgroundAgentFailed
                                        logger
                                        ex
                                        operation
                                        work.Envelope.EventId
                                        (DomainEventType.toString work.Envelope.EventType)

                                    work.Complete(Error(UnexpectedException(operation, ex.Message)))

                            return! loop ()
                        with
                        | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> return ()
                    }

                loop ()),
            cancellationToken = stoppingToken
        )

    let playbackQueueAgent = startAgent "PlaybackQueueAgent" (PlaybackQueueAgent.handle playbackWorkflow)
    let libraryScanAgent = startAgent "LibraryScanAgent" (LibraryScanAgent.handle libraryScanWorkflow)
    let streamNodeAgent =
        startAgent
            "StreamNodeAgent"
            (fun envelope cancellationToken ->
                match envelope.EventType with
                | StreamNodeFailureDetected ->
                    StreamNodeAgent.recordFailure dataSource timeProvider streamNodeState envelope cancellationToken
                | StreamNodeHeartbeatReceived ->
                    StreamNodeAgent.recordHeartbeat dataSource timeProvider streamNodeState envelope cancellationToken
                | _ -> Task.FromResult(Ok()))

    let dispatchToAgent (agent: MailboxProcessor<AgentWorkItem>) (envelope: DomainEventEnvelope) (cancellationToken: CancellationToken) =
        task {
            use linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken)
            let work = new AgentWorkItem(envelope, linkedCancellation.Token)
            use registration = linkedCancellation.Token.Register(fun () -> work.TryCancel())

            if work.IsQueued then
                agent.Post(work)

            return! work.Task
        }

    interface IDomainEventDispatcher with
        member _.DispatchAsync envelope cancellationToken =
            match envelope.EventType with
            | PlaybackQueueItemClaimed
            | PlaybackStarted
            | PlaybackEnded -> dispatchToAgent playbackQueueAgent envelope cancellationToken
            | LibraryScanRequested
            | TrackDiscovered -> dispatchToAgent libraryScanAgent envelope cancellationToken
            | StreamNodeHeartbeatReceived
            | StreamNodeFailureDetected -> dispatchToAgent streamNodeAgent envelope cancellationToken
            | AdminGoalChanged
            | SocialLinkChanged
            | BannerChanged
            | PlaybackReordered
            | PlaybackSkipped
            | PlaybackRestarted
            | TrackForcePlayed
            | AdminTrackQueued
            | TrackMetadataChanged
            | TrackMaterialized
            | PlaylistChanged
            | PlaylistTrackQueued ->
                Task.FromResult(PayloadValidation.validateProjectionPayload envelope.EventType envelope.PayloadJson)
            | _ -> Task.FromResult(Error(UnknownEventType(DomainEventType.toString envelope.EventType)))

type DomainEventPublisher(dataSource: NpgsqlDataSource) =
    interface IDomainEventPublisher with
        member _.PublishDurableAsync envelope cancellationToken =
            OutboxEventRepository.append dataSource (OutboxMapping.toOutboxEvent envelope) cancellationToken
            |> TaskResult.mapError RepositoryError



type private ScanMetadata =
    { Title: string
      Artist: string
      Album: string option
      DurationMs: int option
      MetadataSource: string
      Cover: ExtractedCover option }
type LibraryScanHostedService
    (
        dataSource: NpgsqlDataSource,
        timeProvider: TimeProvider,
        options: Web10Options,
        s3ObjectStorage: IS3ObjectStorage,
        logger: ILogger<LibraryScanHostedService>
    ) as this =
    inherit BackgroundService()

    let claimOwner = Uuid.CreateVersion7().ToGuidBigEndian()
    let claimLease = TimeSpan.FromMinutes(5.0)

    let supportedExtensions =
        Set.ofList [ ".mp3"; ".flac"; ".wav"; ".ogg"; ".m4a"; ".aac"; ".opus" ]

    let configuredDefaultBackend () =
        { Id = None
          Name = "configured-default"
          Type =
            match options.Storage.Type with
            | Local -> "Local"
            | S3 -> "S3"
          LocalRoot = if String.IsNullOrWhiteSpace options.Storage.LocalRoot then None else Some options.Storage.LocalRoot
          S3Bucket = if String.IsNullOrWhiteSpace options.Storage.S3Bucket then None else Some options.Storage.S3Bucket }

    let contentTypeFor extension =
        match extension with
        | ".mp3" -> Some "audio/mpeg"
        | ".flac" -> Some "audio/flac"
        | ".wav" -> Some "audio/wav"
        | ".ogg" -> Some "audio/ogg"
        | ".m4a" -> Some "audio/mp4"
        | ".aac" -> Some "audio/aac"
        | ".opus" -> Some "audio/opus"
        | _ -> None

    let metadataFromPath (path: string) =
        let stem = Path.GetFileNameWithoutExtension(path)
        let separatorIndex = stem.IndexOf(" - ", StringComparison.Ordinal)

        if separatorIndex > 0 && separatorIndex + 3 < stem.Length then
            let artist = stem.Substring(0, separatorIndex).Trim()
            let title = stem.Substring(separatorIndex + 3).Trim()
            (if String.IsNullOrWhiteSpace artist then "Unknown Artist" else artist),
            (if String.IsNullOrWhiteSpace title then stem else title)
        else
            "Unknown Artist", stem

    let createLibraryEnvelope eventType payloadJson =
        DomainEventEnvelope.create timeProvider eventType "Web10.Radio.API.LibraryScan" None None payloadJson
        |> Result.mapError DomainEventError

    let appendEnvelope connection transaction envelope cancellationToken =
        OutboxEventRepository.appendInTransaction connection transaction (OutboxMapping.toOutboxEvent envelope) cancellationToken
        |> TaskResult.mapError RepositoryError


    let requireTransition operation id wasUpdated =
        wasUpdated |> Result.requireTrue (StateTransitionRejected(operation, id))

    let resolveStorageBackend (job: LibraryScanJobRecord) cancellationToken =
        taskResult {
            match job.StorageBackendId with
            | None -> return Some(configuredDefaultBackend())
            | Some storageBackendId ->
                return!
                    LibraryScanRepository.getStorageBackend dataSource storageBackendId cancellationToken
                    |> TaskResult.mapError RepositoryError
        }

    let renewClaim (job: LibraryScanJobRecord) cancellationToken =
        taskResult {
            let nowUtc = timeProvider.GetUtcNow()
            let! renewed =
                LibraryScanRepository.renewJobLease
                    dataSource
                    job.Id
                    job.ClaimOwner
                    job.ClaimAttempt
                    (nowUtc + claimLease)
                    nowUtc
                    cancellationToken
                |> TaskResult.mapError RepositoryError

            do! requireTransition "renew library scan lease" job.Id renewed
        }

    let fallbackMetadataFromPath (storagePath: string) =
        let stem = Path.GetFileNameWithoutExtension(storagePath)
        let separatorIndex = stem.IndexOf(" - ", StringComparison.Ordinal)

        if separatorIndex > 0 && separatorIndex + 3 < stem.Length then
            let artist = stem.Substring(0, separatorIndex).Trim()
            let title = stem.Substring(separatorIndex + 3).Trim()
            (if String.IsNullOrWhiteSpace artist then "Unknown Artist" else artist),
            (if String.IsNullOrWhiteSpace title then stem else title)
        else
            "Unknown Artist", stem

    let metadataForPath (storagePath: string) (mediaPath: string) : ScanMetadata =
        let fallbackArtist, fallbackTitle = fallbackMetadataFromPath storagePath

        match TrackMetadata.read mediaPath with
        | Ok metadata ->
            let hasEmbeddedText = metadata.Title.IsSome || metadata.Artist.IsSome || metadata.Album.IsSome
            { Title = metadata.Title |> Option.defaultValue fallbackTitle
              Artist = metadata.Artist |> Option.defaultValue fallbackArtist
              Album = metadata.Album
              DurationMs = metadata.DurationMs
              MetadataSource = if hasEmbeddedText then "Embedded" else "Filename"
              Cover = metadata.Cover }
        | Error error ->
            logger.LogWarning("ATL metadata parse failed for {StoragePath}: {Error}", storagePath, error)
            { Title = fallbackTitle
              Artist = fallbackArtist
              Album = None
              DurationMs = None
              MetadataSource = "Filename"
              Cover = None }

    let materializeCover (trackId: Guid) (cover: ExtractedCover option) : Task<DiscoveredCover option> =
        task {
            match cover with
            | None -> return None
            | Some cover ->
                let directory = Path.Combine(options.Storage.CacheRoot, "covers", trackId.ToString("D"))
                Directory.CreateDirectory(directory) |> ignore
                let temporaryDirectory = Path.Combine(options.Storage.CacheRoot, "tmp")
                Directory.CreateDirectory(temporaryDirectory) |> ignore
                let temporary = Path.Combine(temporaryDirectory, sprintf "%s.%s.tmp" (Uuid.CreateVersion7().ToGuidBigEndian().ToString("N")) (cover.Extension.TrimStart([| '.' |])))
                let finalPath = Path.Combine(directory, cover.Sha256 + cover.Extension)

                try
                    File.WriteAllBytes(temporary, cover.Bytes)
                    File.Move(temporary, finalPath, true)
                    return
                        Some
                            { CachePath = finalPath
                              ContentType = cover.ContentType
                              SizeBytes = int64 cover.Bytes.LongLength
                              Sha256 = cover.Sha256 }
                with ex ->
                    logger.LogWarning(ex, "Cover materialization failed for {TrackId}", trackId)

                    try
                        if File.Exists temporary then File.Delete temporary
                    with _ -> ()

                    return None
        }

    let tryGetExistingTrackFile (backend: StorageBackendRecord) storagePath cancellationToken =
        LibraryScanRepository.tryGetActiveTrackFileState dataSource backend.Id storagePath cancellationToken
        |> TaskResult.mapError RepositoryError
    let insertDiscoveredFile
        (job: LibraryScanJobRecord)
        (backend: StorageBackendRecord)
        storagePath
        mediaPath
        cachePath
        isCached
        sizeBytes
        emitMaterialized
        (existing: ActiveTrackFileState option)
        cancellationToken =
        taskResult {
            let nowUtc = timeProvider.GetUtcNow()
            let metadata = metadataForPath storagePath mediaPath
            let candidateTrackId = existing |> Option.map _.TrackId |> Option.defaultValue (Uuid.CreateVersion7().ToGuidBigEndian())
            let candidateTrackFileId = existing |> Option.map _.TrackFileId |> Option.defaultValue (Uuid.CreateVersion7().ToGuidBigEndian())
            let! cover = materializeCover candidateTrackId metadata.Cover

            let discovered =
                { TrackId = candidateTrackId
                  TrackFileId = candidateTrackFileId
                  StorageBackendId = backend.Id
                  StoragePath = storagePath
                  CachePath = cachePath
                  IsCached = isCached
                  Title = metadata.Title
                  Artist = metadata.Artist
                  Album = metadata.Album
                  DurationMs = metadata.DurationMs
                  MetadataSource = metadata.MetadataSource
                  Cover = cover
                  ContentType = contentTypeFor (Path.GetExtension(storagePath).ToLowerInvariant())
                  SizeBytes = sizeBytes
                  DiscoveredAtUtc = nowUtc }

            do!
                DatabaseSession.withTransactionResult
                    dataSource
                    (fun connection transaction cancellationToken ->
                        taskResult {
                            let! discovery =
                                LibraryScanRepository.insertDiscoveredTrackInTransaction connection transaction discovered cancellationToken
                                |> TaskResult.mapError RepositoryError

                            match discovery with
                            | DiscoveredTrackResult.Created identity ->
                                let! incremented =
                                    LibraryScanRepository.incrementDiscoveredCountInTransaction
                                        connection
                                        transaction
                                        job.Id
                                        job.ClaimOwner
                                        job.ClaimAttempt
                                        nowUtc
                                        cancellationToken
                                    |> TaskResult.mapError RepositoryError

                                do! requireTransition "increment library scan discovered count" job.Id incremented
                                let payload =
                                    JsonPayload.objectWithStrings
                                        [ "trackId", string identity.TrackId
                                          "trackFileId", string identity.TrackFileId
                                          "storagePath", discovered.StoragePath ]
                                let! envelope = createLibraryEnvelope TrackDiscovered payload
                                do! appendEnvelope connection transaction envelope cancellationToken
                            | DiscoveredTrackResult.Updated identity when emitMaterialized ->
                                let payload =
                                    JsonPayload.objectWithStrings
                                        [ "trackId", string identity.TrackId
                                          "trackFileId", string identity.TrackFileId
                                          "storagePath", discovered.StoragePath ]
                                let! envelope = createLibraryEnvelope TrackMaterialized payload
                                do! appendEnvelope connection transaction envelope cancellationToken
                            | DiscoveredTrackResult.Updated _ -> ()
                        })
                    cancellationToken
        }

    let processLocalBackend (job: LibraryScanJobRecord) (backend: StorageBackendRecord) localRoot cancellationToken =
        taskResult {
            do!
                Directory.Exists localRoot
                |> Result.requireTrue (UnexpectedException("LibraryScanHostedService", "local storage root not found"))

            let localRootFull = Path.GetFullPath localRoot
            let cacheRootFull = Path.GetFullPath options.Storage.CacheRoot
            let cachePrefix = cacheRootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + string Path.DirectorySeparatorChar

            let files =
                Directory.EnumerateFiles(localRootFull, "*", SearchOption.AllDirectories)
                |> Seq.filter (fun path -> supportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                |> Seq.filter (fun path ->
                    let full = Path.GetFullPath path
                    not (String.Equals(full, cacheRootFull, StringComparison.OrdinalIgnoreCase)
                         || full.StartsWith(cachePrefix, StringComparison.OrdinalIgnoreCase)))

            for filePath in files do
                do! renewClaim job cancellationToken
                let fileInfo = FileInfo(filePath)
                let! existing = tryGetExistingTrackFile backend filePath cancellationToken
                do! insertDiscoveredFile job backend filePath filePath (Some filePath) true (Some fileInfo.Length) false existing cancellationToken
        }

    let canonicalAudioPath (backend: StorageBackendRecord) (key: string) =
        let backendBytes = backend.Id |> Option.defaultValue Guid.Empty |> fun value -> value.ToByteArray()
        Array.Reverse(backendBytes, 0, 4)
        Array.Reverse(backendBytes, 4, 2)
        Array.Reverse(backendBytes, 6, 2)
        let keyBytes = Encoding.UTF8.GetBytes key
        let material = Array.zeroCreate<byte> (backendBytes.Length + keyBytes.Length)
        Buffer.BlockCopy(backendBytes, 0, material, 0, backendBytes.Length)
        Buffer.BlockCopy(keyBytes, 0, material, backendBytes.Length, keyBytes.Length)
        let digestBytes: byte array = SHA256.HashData(material)
        let digest = Convert.ToHexString(digestBytes).ToLowerInvariant()
        let extension = Path.GetExtension(key).ToLowerInvariant()
        Path.Combine(options.Storage.CacheRoot, "audio", digest + extension)

    let processS3Backend (job: LibraryScanJobRecord) (backend: StorageBackendRecord) bucketName cancellationToken =
        taskResult {
            let scope =
                match backend.Id with
                | None -> S3ClientScope.ConfiguredDefault
                | Some _ -> S3ClientScope.AwsDefaultChain

            let budgetAware = backend.Id.IsNone
            let mutable available = Int64.MaxValue
            if budgetAware then
                let candidateId = Uuid.CreateVersion7().ToGuidBigEndian()
                let! settings =
                    StorageSettingsRepository.getOrCreate dataSource candidateId StorageCachePolicy.defaultCacheMaxBytes StorageCachePolicy.presignTtlSeconds (timeProvider.GetUtcNow()) cancellationToken
                    |> TaskResult.mapError RepositoryError
                let! currentBytes =
                    StorageSettingsRepository.totalS3CacheBytes dataSource cancellationToken
                    |> TaskResult.mapError RepositoryError
                available <- max 0L (settings.S3CacheMaxBytes - currentBytes)

            let visitPage (items: System.Collections.Generic.IReadOnlyList<S3ObjectDescriptor>) (pageCancellationToken: CancellationToken) =
                task {
                    let! pageResult =
                        taskResult {
                            do! renewClaim job pageCancellationToken

                            for item in items do
                                if supportedExtensions.Contains(Path.GetExtension(item.Key).ToLowerInvariant()) && S3KeyValidation.isCanonical item.Key then
                                    let temporaryDirectory = Path.Combine(options.Storage.CacheRoot, "tmp")
                                    Directory.CreateDirectory(temporaryDirectory) |> ignore
                                    let temporary = Path.Combine(temporaryDirectory, sprintf "%s%s.tmp" (Uuid.CreateVersion7().ToGuidBigEndian().ToString("N")) (Path.GetExtension(item.Key).ToLowerInvariant()))
                                    let audioPath = canonicalAudioPath backend item.Key
                                    Directory.CreateDirectory(Path.GetDirectoryName(audioPath)) |> ignore

                                    try
                                        do! s3ObjectStorage.DownloadToFileAsync(scope, bucketName, item.Key, temporary, pageCancellationToken)
                                        let! existing = tryGetExistingTrackFile backend item.Key pageCancellationToken
                                        let keepCached = (not budgetAware) || available >= item.SizeBytes
                                        if keepCached then
                                            File.Move(temporary, audioPath, true)
                                            let emitMaterialized = existing |> Option.exists (fun value -> not value.IsCached)
                                            do!
                                                insertDiscoveredFile
                                                    job
                                                    backend
                                                    item.Key
                                                    audioPath
                                                    (Some audioPath)
                                                    true
                                                    (Some item.SizeBytes)
                                                    emitMaterialized
                                                    existing
                                                    pageCancellationToken
                                            if budgetAware then available <- available - item.SizeBytes
                                        else
                                            do!
                                                insertDiscoveredFile
                                                    job
                                                    backend
                                                    item.Key
                                                    temporary
                                                    None
                                                    false
                                                    (Some item.SizeBytes)
                                                    false
                                                    existing
                                                    pageCancellationToken
                                            try
                                                if File.Exists temporary then File.Delete temporary
                                            with _ -> ()
                                    with ex ->
                                        logger.LogWarning(ex, "S3 object {ObjectKey} could not be materialized; retrying on next scan", item.Key)

                                        try
                                            if File.Exists temporary then File.Delete temporary
                                        with _ -> ()
                        }

                    match pageResult with
                    | Ok () -> return ()
                    | Error error -> return raise (InvalidOperationException(BackgroundWorkerError.toMessage error))
                }
                :> Task

            let mutable continuationToken: string option = None
            let mutable hasMore = true
            while hasMore do
                let! page =
                    s3ObjectStorage.ListPageAsync(
                        scope,
                        bucketName,
                        "",
                        None,
                        1000,
                        continuationToken,
                        cancellationToken
                    )
                let objects = page.Objects |> List.toArray :> System.Collections.Generic.IReadOnlyList<S3ObjectDescriptor>
                do! visitPage objects cancellationToken
                continuationToken <- page.NextContinuationToken
                hasMore <- continuationToken.IsSome
        }


    let completeClaim (job: LibraryScanJobRecord) cancellationToken =
        taskResult {
            let! completed =
                LibraryScanRepository.completeJob
                    dataSource
                    job.Id
                    job.ClaimOwner
                    job.ClaimAttempt
                    (timeProvider.GetUtcNow())
                    cancellationToken
                |> TaskResult.mapError RepositoryError

            do! requireTransition "complete library scan" job.Id completed
        }

    let failClaim (job: LibraryScanJobRecord) reason cancellationToken =
        LibraryScanRepository.failJob
            dataSource
            job.Id
            job.ClaimOwner
            job.ClaimAttempt
            (timeProvider.GetUtcNow())
            reason
            cancellationToken
        |> TaskResult.mapError RepositoryError

    let processClaimedJob (job: LibraryScanJobRecord) onStorageResolved cancellationToken =
        taskResult {
            let! backend = resolveStorageBackend job cancellationToken
            let! backend =
                backend
                |> Result.requireSome (UnexpectedException("LibraryScanHostedService", "storage backend not found"))

            match backend.Type with
            | "Local" ->
                onStorageResolved "local"
                let! localRoot =
                    backend.LocalRoot
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Result.requireSome (UnexpectedException("LibraryScanHostedService", "local storage root not found"))

                do! processLocalBackend job backend localRoot cancellationToken
            | "S3" ->
                onStorageResolved "s3"
                let! bucketName =
                    backend.S3Bucket
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Result.requireSome (UnexpectedException("LibraryScanHostedService", "S3 bucket is required"))

                do! processS3Backend job backend bucketName cancellationToken
            | value ->
                return!
                    Error(UnexpectedException("LibraryScanHostedService", sprintf "unsupported storage backend type: %s" value))

            do! completeClaim job cancellationToken
        }

    let runClaimedJob (job: LibraryScanJobRecord) cancellationToken =
        task {
            use attempt = FlowTelemetry.startRoot FlowTelemetry.LibraryScan
            let mutable metricTags = []

            let recordStorage storage =
                FlowTelemetry.addTag "storage" storage attempt
                metricTags <- [ FlowTelemetry.storage storage ]

            FlowTelemetry.addTag "library_scan.job_id" (string job.Id) attempt
            FlowTelemetry.addTag "claim.attempt" (string job.ClaimAttempt) attempt

            try
                let! processingResult =
                    task {
                        try
                            return! processClaimedJob job recordStorage cancellationToken
                        with
                        | :? OperationCanceledException as ex ->
                            return Error(UnexpectedException("LibraryScanHostedService", ex.Message))
                        | ex -> return Error(UnexpectedException("LibraryScanHostedService", ex.Message))
                    }

                match processingResult with
                | Ok () ->
                    FlowTelemetry.finish "completed" metricTags attempt |> ignore
                    return Ok true
                | Error processingError ->
                    let reason = BackgroundWorkerError.toMessage processingError
                    let! failureResult = failClaim job reason CancellationToken.None

                    match failureResult with
                    | Ok _ ->
                        FlowTelemetry.finish "failed" metricTags attempt |> ignore
                        return Error processingError
                    | Error failureError ->
                        FlowTelemetry.finish "failed" metricTags attempt |> ignore
                        return Error failureError
            with ex ->
                FlowTelemetry.finishError "failed" metricTags ex attempt |> ignore
                return raise ex
        }

    let claimAndProcess jobId cancellationToken =
        taskResult {
            let nowUtc = timeProvider.GetUtcNow()
            let leaseExpiresAtUtc = nowUtc + claimLease
            let! job =
                match jobId with
                | None ->
                    LibraryScanRepository.claimNextJob
                        dataSource
                        claimOwner
                        nowUtc
                        leaseExpiresAtUtc
                        cancellationToken
                | Some id ->
                    LibraryScanRepository.claimJobById
                        dataSource
                        id
                        claimOwner
                        nowUtc
                        leaseExpiresAtUtc
                        cancellationToken
                |> TaskResult.mapError RepositoryError

            match job with
            | None -> return false
            | Some claimed -> return! runClaimedJob claimed cancellationToken
        }

    member _.ProcessOneJobAsync(cancellationToken: CancellationToken) =
        claimAndProcess None cancellationToken

    member _.ProcessJobAsync(jobId: Guid, cancellationToken: CancellationToken) =
        claimAndProcess (Some jobId) cancellationToken

    member _.HandleEventAsync(envelope: DomainEventEnvelope, cancellationToken: CancellationToken) =
        taskResult {
            let eventType = DomainEventType.toString envelope.EventType
            let! root = JsonPayload.parseObject eventType envelope.PayloadJson

            match envelope.EventType with
            | LibraryScanRequested ->
                let! jobId = PayloadValidation.requireUuid eventType "libraryScanJobId" root
                let! _ = this.ProcessJobAsync(jobId, cancellationToken)
                return ()
            | TrackDiscovered ->
                let! _ = PayloadValidation.requireUuid eventType "trackId" root
                let! _ = PayloadValidation.requireUuid eventType "trackFileId" root
                let! _ = PayloadValidation.requireString eventType "storagePath" root
                return ()
            | _ -> return! Error(InvalidPayload(eventType, "event is not handled by LibraryScanAgent."))
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                while not stoppingToken.IsCancellationRequested do
                    let! result = this.ProcessOneJobAsync(stoppingToken)

                    match result with
                    | Ok true -> ()
                    | Ok false -> do! Task.Delay(TimeSpan.FromSeconds(5.0), stoppingToken)
                    | Error error ->
                        BackgroundWorkerLog.operationFailed logger "Library scan worker" error
                        do! Task.Delay(TimeSpan.FromSeconds(5.0), stoppingToken)
            with
            | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
        }
        :> Task

    interface ILibraryScanWorkflow with
        member _.HandleAsync envelope cancellationToken = this.HandleEventAsync(envelope, cancellationToken)

type PlaybackCompletionReporter
    (
        dataSource: NpgsqlDataSource,
        timeProvider: TimeProvider
    ) =
    let leaseDuration = TimeSpan.FromSeconds(30.0)

    let validateIdentity queueItemId claimOwner claimAttempt =
        result {
            do!
                (queueItemId <> Guid.Empty)
                |> Result.requireTrue (InvalidPayload("PlaybackEnded", "queueItemId must be a UUID."))

            do!
                (claimOwner <> Guid.Empty)
                |> Result.requireTrue (InvalidPayload("PlaybackEnded", "claimOwner must be a UUID."))

            do!
                (claimAttempt > 0)
                |> Result.requireTrue (InvalidPayload("PlaybackEnded", "claimAttempt must be positive."))
        }

    interface IPlaybackCompletionReporter with
        member _.RenewLeaseAsync queueItemId claimOwner claimAttempt cancellationToken =
            taskResult {
                do! validateIdentity queueItemId claimOwner claimAttempt
                let nowUtc = timeProvider.GetUtcNow()

                return!
                    PlaybackQueueRepository.renewPlayingLease
                        dataSource
                        queueItemId
                        claimOwner
                        claimAttempt
                        nowUtc
                        (nowUtc + leaseDuration)
                        cancellationToken
                    |> TaskResult.mapError RepositoryError
            }

        member _.ReportAsync queueItemId claimOwner claimAttempt outcome cancellationToken =
            taskResult {
                do! validateIdentity queueItemId claimOwner claimAttempt

                let! status, failureReason =
                    match outcome with
                    | Succeeded -> Ok("played", None)
                    | Failed reason when not (String.IsNullOrWhiteSpace reason) ->
                        Ok("failed", Some(reason.Trim()))
                    | Failed _ ->
                        Error(InvalidPayload("PlaybackEnded", "failure reason is required."))

                let payloadJson =
                    JsonPayload.objectWithStringsAndRaw
                        ([ "queueItemId", string queueItemId
                           "claimOwner", string claimOwner
                           "status", status ]
                         @ (failureReason
                            |> Option.map (fun reason -> [ "failureReason", reason ])
                            |> Option.defaultValue []))
                        [ "claimAttempt", string claimAttempt ]

                let! envelope =
                    DomainEventEnvelope.create
                        timeProvider
                        PlaybackEnded
                        "Web10.Radio.StreamNode"
                        None
                        None
                        payloadJson
                    |> Result.mapError DomainEventError

                return!
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction cancellationToken ->
                            taskResult {
                                let finishedAtUtc = timeProvider.GetUtcNow()
                                let! active =
                                    PlaybackQueueRepository.lockOwnedPlayingClaimInTransaction
                                        connection
                                        transaction
                                        queueItemId
                                        claimOwner
                                        claimAttempt
                                        finishedAtUtc
                                        cancellationToken
                                    |> TaskResult.mapError RepositoryError

                                if not active then
                                    return false
                                else
                                    let! transitioned =
                                        match outcome with
                                        | Succeeded ->
                                            PlaybackQueueRepository.markPlayedInTransaction
                                                connection
                                                transaction
                                                queueItemId
                                                claimOwner
                                                claimAttempt
                                                finishedAtUtc
                                                cancellationToken
                                        | Failed _ ->
                                            PlaybackQueueRepository.markFailedInTransaction
                                                connection
                                                transaction
                                                queueItemId
                                                claimOwner
                                                claimAttempt
                                                finishedAtUtc
                                                failureReason.Value
                                                cancellationToken
                                        |> TaskResult.mapError RepositoryError

                                    do!
                                        transitioned
                                        |> Result.requireTrue
                                            (StateTransitionRejected("complete active playback claim", queueItemId))

                                    do!
                                        OutboxEventRepository.appendInTransaction
                                            connection
                                            transaction
                                            (OutboxMapping.toOutboxEvent envelope)
                                            cancellationToken
                                        |> TaskResult.mapError RepositoryError

                                    return true
                            })
                        cancellationToken
            }

type PlaybackProgramHostedService
    (
        dataSource: NpgsqlDataSource,
        timeProvider: TimeProvider,
        storageOptions: StorageOptions,
        logger: ILogger<PlaybackProgramHostedService>
    ) as this =
    inherit BackgroundService()

    let claimOwner = Uuid.CreateVersion7().ToGuidBigEndian()
    let claimLease = TimeSpan.FromSeconds(30.0)
    let defaultStorageIsS3 = storageOptions.Type = StorageType.S3

    let createPlaybackEnvelope eventType payloadJson =
        DomainEventEnvelope.create timeProvider eventType "Web10.Radio.API.PlaybackProgram" None None payloadJson
        |> Result.mapError DomainEventError

    let appendEnvelope connection transaction envelope cancellationToken =
        OutboxEventRepository.appendInTransaction connection transaction (OutboxMapping.toOutboxEvent envelope) cancellationToken
        |> TaskResult.mapError RepositoryError

    let claimIdentityPayload queueItemId owner attempt extraStrings =
        JsonPayload.objectWithStringsAndRaw
            (("queueItemId", string queueItemId) :: ("claimOwner", string owner) :: extraStrings)
            [ "claimAttempt", string attempt ]

    let claimedPayload (claimed: ClaimedPlaybackQueueItem) =
        claimIdentityPayload claimed.QueueItemId claimed.ClaimOwner claimed.ClaimAttempt []
    let appendPlaylistQueuedEvent (item: PlaybackQueueItem) cancellationToken =
        taskResult {
            let! playlistId = item.PlaylistId |> Result.requireSome (InvalidPayload("PlaylistTrackQueued", "playlistId is required."))
            let! trackId = item.TrackId |> Result.requireSome (InvalidPayload("PlaylistTrackQueued", "trackId is required."))
            let payload =
                JsonSerializer.Serialize(
                    {| playlistId = playlistId.ToString("D")
                       playlistItemId = item.PlaylistItemId |> Option.map (fun value -> value.ToString("D"))
                       trackId = trackId.ToString("D")
                       queueItemId = item.QueueItemId.ToString("D")
                       source = item.Source
                       reason = if item.Source = "jingle" then "interrupt" else "general" |},
                    DomainJson.options
                )
            let! envelope = createPlaybackEnvelope PlaylistTrackQueued payload
            let! _ =
                DatabaseSession.withTransactionResult
                    dataSource
                    (fun connection transaction token -> appendEnvelope connection transaction envelope token)
                    cancellationToken
            return ()
        }

    let claimNextQueueItem cancellationToken =
        task {
            use attempt = FlowTelemetry.startRoot FlowTelemetry.QueueClaim

            try
                let nowUtc = timeProvider.GetUtcNow()

                let! result =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction cancellationToken ->
                            taskResult {
                                let! claimed =
                                    PlaybackQueueRepository.claimNextDetailedInTransaction
                                        connection
                                        transaction
                                        claimOwner
                                        nowUtc
                                        (nowUtc + claimLease)
                                        cancellationToken
                                    |> TaskResult.mapError RepositoryError

                                match claimed with
                                | None -> return None
                                | Some claimed ->
                                    let! envelope = claimed |> claimedPayload |> createPlaybackEnvelope PlaybackQueueItemClaimed
                                    do! appendEnvelope connection transaction envelope cancellationToken
                                    return Some claimed
                            })
                        cancellationToken

                match result with
                | Ok None ->
                    FlowTelemetry.finish "empty" [] attempt |> ignore
                    return Ok false
                | Ok(Some claimed) ->
                    FlowTelemetry.addTag "queue.item_id" (string claimed.QueueItemId) attempt
                    FlowTelemetry.addTag "claim.attempt" (string claimed.ClaimAttempt) attempt
                    FlowTelemetry.finish "claimed" [] attempt |> ignore
                    return Ok true
                | Error error ->
                    FlowTelemetry.finish "error" [] attempt |> ignore
                    return Error error
            with ex ->
                FlowTelemetry.finishError "error" [] ex attempt |> ignore
                return raise ex
        }

    let playbackStartedPayload queueItemId owner attempt trackId cachePath =
        claimIdentityPayload
            queueItemId
            owner
            attempt
            [ "trackId", string trackId
              "cachePath", cachePath ]

    let playbackEndedPayload queueItemId owner attempt status failureReason =
        let fields =
            [ "status", status ]
            @ (failureReason |> Option.map (fun reason -> [ "failureReason", reason ]) |> Option.defaultValue [])

        claimIdentityPayload queueItemId owner attempt fields

    let streamFailurePayload queueItemId reason =
        JsonPayload.objectWithStrings
            [ "queueItemId", string queueItemId
              "reason", reason ]

    let parseClaimIdentity eventType root =
        result {
            let! queueItemId = PayloadValidation.requireUuid eventType "queueItemId" root
            let! owner = PayloadValidation.requireUuid eventType "claimOwner" root
            let! attempt =
                JsonPayload.tryGetInt "claimAttempt" root
                |> Result.requireSome (InvalidPayload(eventType, "claimAttempt is required."))

            do! (attempt > 0) |> Result.requireTrue (InvalidPayload(eventType, "claimAttempt must be positive."))
            return queueItemId, owner, attempt
        }

    let appendFailureEvents connection transaction queueItemId owner attempt failureReason cancellationToken =
        taskResult {
            let! playbackEndedEnvelope =
                playbackEndedPayload queueItemId owner attempt "failed" (Some failureReason)
                |> createPlaybackEnvelope PlaybackEnded

            let! streamFailureEnvelope =
                streamFailurePayload queueItemId failureReason
                |> createPlaybackEnvelope StreamNodeFailureDetected

            do! appendEnvelope connection transaction playbackEndedEnvelope cancellationToken
            do! appendEnvelope connection transaction streamFailureEnvelope cancellationToken
        }

    let failOwnedClaim connection transaction queueItemId owner attempt failureReason cancellationToken =
        taskResult {
            let! failed =
                PlaybackQueueRepository.markFailedInTransaction
                    connection
                    transaction
                    queueItemId
                    owner
                    attempt
                    (timeProvider.GetUtcNow())
                    failureReason
                    cancellationToken
                |> TaskResult.mapError RepositoryError

            if failed then
                do! appendFailureEvents connection transaction queueItemId owner attempt failureReason cancellationToken
        }

    let promoteOwnedClaim connection transaction queueItemId owner attempt (trackId: Guid option) cancellationToken =
        taskResult {
            let nowUtc = timeProvider.GetUtcNow()
            match trackId with
            | None ->
                do!
                    failOwnedClaim
                        connection
                        transaction
                        queueItemId
                        owner
                        attempt
                        "playback queue item has no track"
                        cancellationToken
            | Some trackId ->
                let! cachePath =
                    PlaybackQueueRepository.findCachedTrackFileInTransaction
                        connection
                        transaction
                        trackId
                        defaultStorageIsS3
                        cancellationToken
                    |> TaskResult.mapError RepositoryError

                match cachePath with
                | None ->
                    do!
                        failOwnedClaim
                            connection
                            transaction
                            queueItemId
                            owner
                            attempt
                            "cache path unavailable"
                            cancellationToken
                | Some cachePath ->
                    let! playing =
                        PlaybackQueueRepository.markPlayingInTransaction
                            connection
                            transaction
                            queueItemId
                            owner
                            attempt
                            nowUtc
                            (nowUtc + claimLease)
                            cancellationToken
                        |> TaskResult.mapError RepositoryError

                    if playing then
                        let! envelope =
                            playbackStartedPayload queueItemId owner attempt trackId cachePath
                            |> createPlaybackEnvelope PlaybackStarted

                        do! appendEnvelope connection transaction envelope cancellationToken
        }

    let promoteOnDeck cancellationToken =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                taskResult {
                    let! onDeck =
                        PlaybackQueueRepository.tryGetPromotableOnDeckInTransaction
                            connection
                            transaction
                            cancellationToken
                        |> TaskResult.mapError RepositoryError

                    match onDeck with
                    | None -> return false
                    | Some claim ->
                        do!
                            promoteOwnedClaim
                                connection
                                transaction
                                claim.QueueItemId
                                claim.ClaimOwner
                                claim.ClaimAttempt
                                claim.TrackId
                                cancellationToken

                        return true
                })
            cancellationToken


    let handleEndedEvent queueItemId owner attempt status failureReason cancellationToken =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction cancellationToken ->
                taskResult {
                    match status with
                    | "played" ->
                        let! _ =
                            PlaybackQueueRepository.markPlayedInTransaction
                                connection
                                transaction
                                queueItemId
                                owner
                                attempt
                                (timeProvider.GetUtcNow())
                                cancellationToken
                            |> TaskResult.mapError RepositoryError

                        return ()
                    | "failed" ->
                        let! reason =
                            failureReason
                            |> Result.requireSome (InvalidPayload("PlaybackEnded", "failureReason is required."))

                        let! _ =
                            PlaybackQueueRepository.markFailedInTransaction
                                connection
                                transaction
                                queueItemId
                                owner
                                attempt
                                (timeProvider.GetUtcNow())
                                reason
                                cancellationToken
                            |> TaskResult.mapError RepositoryError

                        return ()
                    | value ->
                        return!
                            Error(InvalidPayload("PlaybackEnded", sprintf "status must be played or failed, got %s." value))
                })
            cancellationToken

    member _.ProcessOneQueueItemAsync(cancellationToken: CancellationToken) : Task<Result<bool, BackgroundWorkerError>> =
        taskResult {
            let! controlState =
                StreamNodeControlRepository.getOrCreate
                    dataSource
                    (Uuid.CreateVersion7().ToGuidBigEndian())
                    (timeProvider.GetUtcNow())
                    cancellationToken
                |> TaskResult.mapError RepositoryError

            match controlState.DesiredState with
            | StreamNodeDesiredState.Paused
            | StreamNodeDesiredState.Stopped -> return false
            | StreamNodeDesiredState.Running ->
                let! promoted = promoteOnDeck cancellationToken

                if promoted then
                    return true
                else

                let! initiallyClaimed = claimNextQueueItem cancellationToken

                if initiallyClaimed then
                    return true
                else
                    let! enqueued =
                        PlaybackQueueRepository.enqueueNextActivePlaylistItemIfIdle
                            dataSource
                            (Uuid.CreateVersion7().ToGuidBigEndian())
                            (timeProvider.GetUtcNow())
                            defaultStorageIsS3
                            cancellationToken
                        |> TaskResult.mapError RepositoryError

                    match enqueued with
                    | Some item -> do! appendPlaylistQueuedEvent item cancellationToken
                    | None -> ()

                    return! claimNextQueueItem cancellationToken
        }

    member _.HandleEventAsync(envelope: DomainEventEnvelope, cancellationToken: CancellationToken) =
        taskResult {
            let eventType = DomainEventType.toString envelope.EventType
            let! root = JsonPayload.parseObject eventType envelope.PayloadJson
            let! queueItemId, owner, attempt = parseClaimIdentity eventType root

            match envelope.EventType with
            | PlaybackQueueItemClaimed ->
                return ()
            | PlaybackStarted ->
                let! _ = PayloadValidation.requireUuid eventType "trackId" root
                let! _ = PayloadValidation.requireString eventType "cachePath" root
                return ()
            | PlaybackEnded ->
                let! status = PayloadValidation.requireString eventType "status" root
                let! failureReason =
                    match status with
                    | "played" -> Ok None
                    | "failed" ->
                        PayloadValidation.requireString eventType "failureReason" root
                        |> Result.map Some
                    | value ->
                        Error(InvalidPayload(eventType, sprintf "status must be played or failed, got %s." value))

                do! handleEndedEvent queueItemId owner attempt status failureReason cancellationToken
            | _ -> return! Error(InvalidPayload(eventType, "event is not handled by PlaybackQueueAgent."))
        }


    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                let! initialized =
                    AdminContentRepository.ensureAllTracksPlaylist
                        dataSource
                        (Uuid.CreateVersion7().ToGuidBigEndian())
                        (timeProvider.GetUtcNow())
                        stoppingToken
                match initialized with
                | Ok _ -> ()
                | Error error -> logger.LogError("Failed to initialize the built-in All tracks playlist: {Error}", error.ToString())
                while not stoppingToken.IsCancellationRequested do
                    let! result = this.ProcessOneQueueItemAsync(stoppingToken)

                    match result with
                    | Ok true -> ()
                    | Ok false -> do! Task.Delay(TimeSpan.FromSeconds(2.0), stoppingToken)
                    | Error error ->
                        BackgroundWorkerLog.operationFailed logger "Playback program worker" error
                        do! Task.Delay(TimeSpan.FromSeconds(2.0), stoppingToken)
            with
            | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
        }
        :> Task

    interface IPlaybackQueueWorkflow with
        member _.HandleAsync envelope cancellationToken = this.HandleEventAsync(envelope, cancellationToken)

type CacheEvictionHostedService
    (
        dataSource: NpgsqlDataSource,
        timeProvider: TimeProvider,
        options: Web10Options,
        logger: ILogger<CacheEvictionHostedService>
    ) as this =
    inherit BackgroundService()

    let evictionInterval = TimeSpan.FromSeconds 60.0

    member private _.RunPassAsync(cancellationToken: CancellationToken) =
        task {
            let candidateId = Uuid.CreateVersion7().ToGuidBigEndian()
            let! settingsResult =
                StorageSettingsRepository.getOrCreate
                    dataSource
                    candidateId
                    StorageCachePolicy.defaultCacheMaxBytes
                    StorageCachePolicy.presignTtlSeconds
                    (timeProvider.GetUtcNow())
                    cancellationToken

            match settingsResult with
            | Error error -> BackgroundWorkerLog.operationFailed logger "Cache eviction settings" (RepositoryError error)
            | Ok settings ->
                let budget = settings.S3CacheMaxBytes
                let! totalResult = StorageSettingsRepository.totalS3CacheBytes dataSource cancellationToken
                match totalResult with
                | Error error -> BackgroundWorkerLog.operationFailed logger "Cache size" (RepositoryError error)
                | Ok total when total <= budget -> ()
                | Ok total ->
                    let! candidatesResult = StorageSettingsRepository.listEvictionCandidates dataSource cancellationToken
                    match candidatesResult with
                    | Error error -> BackgroundWorkerLog.operationFailed logger "Cache eviction candidates" (RepositoryError error)
                    | Ok candidates ->
                        let mutable remaining = total
                        let mutable pending = candidates
                        let mutable evicted = 0
                        while remaining > budget && not (List.isEmpty pending) do
                            let candidate = List.head pending
                            pending <- List.tail pending
                            let _ = (try File.Delete(candidate.CachePath) with _ -> ())
                            let! markResult = StorageSettingsRepository.markCacheEvicted dataSource candidate.TrackFileId (timeProvider.GetUtcNow()) cancellationToken
                            match markResult with
                            | Error error -> BackgroundWorkerLog.operationFailed logger "Cache eviction mark" (RepositoryError error)
                            | Ok _ -> evicted <- evicted + 1
                            remaining <- remaining - candidate.SizeBytes
                        if evicted > 0 then
                            logger.LogInformation("Cache eviction removed {Evicted} S3 cache copies to enforce the {Budget}-byte budget.", evicted, budget)
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                while not stoppingToken.IsCancellationRequested do
                    try
                        if options.Storage.Type = StorageType.S3 then do! this.RunPassAsync(stoppingToken)
                    with
                    | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
                    | ex -> logger.LogError(ex, "Cache eviction pass failed.")
                    do! Task.Delay(evictionInterval, stoppingToken)
            with :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
        }
        :> Task

module BackgroundWorkerComposition =
    let addBackgroundWorkers (options: Web10Options) (services: IServiceCollection) : IServiceCollection =
        services.AddSingleton<Web10Options>(options) |> ignore
        services.AddSingleton<LibraryScanHostedService>() |> ignore
        services.AddSingleton<ILibraryScanWorkflow>(fun provider ->
            provider.GetRequiredService<LibraryScanHostedService>() :> ILibraryScanWorkflow)
        |> ignore
        services.AddSingleton<PlaybackProgramHostedService>() |> ignore
        services.AddSingleton<IPlaybackQueueWorkflow>(fun provider ->
            provider.GetRequiredService<PlaybackProgramHostedService>() :> IPlaybackQueueWorkflow)
        |> ignore
        services.AddSingleton<IDomainEventDispatcher, DomainEventDispatcher>() |> ignore
        services.AddSingleton<IDomainEventPublisher, DomainEventPublisher>() |> ignore
        services.AddSingleton<IPlaybackCompletionReporter, PlaybackCompletionReporter>() |> ignore
        services.AddHostedService(fun provider ->
            OutboxRelayHostedService(
                OutboxAudience.Api,
                provider.GetRequiredService<NpgsqlDataSource>(),
                provider.GetRequiredService<IDomainEventDispatcher>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<OutboxRelayHostedService>>()
            ))
        |> ignore
        services.AddHostedService(fun provider -> provider.GetRequiredService<LibraryScanHostedService>()) |> ignore
        services.AddHostedService(fun provider -> provider.GetRequiredService<PlaybackProgramHostedService>()) |> ignore
        services.AddHostedService(fun provider ->
            CacheEvictionHostedService(
                provider.GetRequiredService<NpgsqlDataSource>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<Web10Options>(),
                provider.GetRequiredService<ILogger<CacheEvictionHostedService>>()
            ))
        |> ignore
        services
