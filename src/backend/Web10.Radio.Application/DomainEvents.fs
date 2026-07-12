namespace Web10.Radio.Application

open System
open System.IO
open System.Text
open System.Text.Json
open Dodo.Primitives
open FsToolkit.ErrorHandling
open Web10.Radio.Database.Repositories

/// Durable events exchanged by the API, Telegram service, and stream workers.
type DomainEventType =
    | TrackRequested
    | TrackRequestMatched
    | SayMessageSubmitted
    | TelegramCommandReceived
    | TelegramCallbackReceived
    | SayMessageModerated
    | DonationInvoiceCreated
    | DonationPaid
    | PaymentRefunded
    | LibraryScanRequested
    | TrackDiscovered
    | PlaybackQueueItemClaimed
    | PlaybackStarted
    | PlaybackEnded
    | StreamNodeHeartbeatReceived
    | StreamNodeFailureDetected
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
    | PlaylistTrackQueued

[<RequireQualifiedAccess>]
module DomainEventType =
    let all =
        [ TrackRequested
          TrackRequestMatched
          SayMessageSubmitted
          TelegramCommandReceived
          TelegramCallbackReceived
          SayMessageModerated
          DonationInvoiceCreated
          DonationPaid
          PaymentRefunded
          LibraryScanRequested
          TrackDiscovered
          PlaybackQueueItemClaimed
          PlaybackStarted
          PlaybackEnded
          StreamNodeHeartbeatReceived
          StreamNodeFailureDetected
          AdminGoalChanged
          SocialLinkChanged
          BannerChanged
          PlaybackReordered
          PlaybackSkipped
          PlaybackRestarted
          TrackForcePlayed
          AdminTrackQueued
          TrackMetadataChanged
          TrackMaterialized
          PlaylistChanged
          PlaylistTrackQueued ]

    let toString eventType =
        match eventType with
        | TrackRequested -> "TrackRequested"
        | TrackRequestMatched -> "TrackRequestMatched"
        | SayMessageSubmitted -> "SayMessageSubmitted"
        | TelegramCommandReceived -> "TelegramCommandReceived"
        | TelegramCallbackReceived -> "TelegramCallbackReceived"
        | SayMessageModerated -> "SayMessageModerated"
        | DonationInvoiceCreated -> "DonationInvoiceCreated"
        | DonationPaid -> "DonationPaid"
        | PaymentRefunded -> "PaymentRefunded"
        | LibraryScanRequested -> "LibraryScanRequested"
        | TrackDiscovered -> "TrackDiscovered"
        | PlaybackQueueItemClaimed -> "PlaybackQueueItemClaimed"
        | PlaybackStarted -> "PlaybackStarted"
        | PlaybackEnded -> "PlaybackEnded"
        | StreamNodeHeartbeatReceived -> "StreamNodeHeartbeatReceived"
        | StreamNodeFailureDetected -> "StreamNodeFailureDetected"
        | AdminGoalChanged -> "AdminGoalChanged"
        | SocialLinkChanged -> "SocialLinkChanged"
        | BannerChanged -> "BannerChanged"
        | PlaybackReordered -> "PlaybackReordered"
        | PlaybackSkipped -> "PlaybackSkipped"
        | PlaybackRestarted -> "PlaybackRestarted"
        | TrackForcePlayed -> "TrackForcePlayed"
        | AdminTrackQueued -> "AdminTrackQueued"
        | TrackMetadataChanged -> "TrackMetadataChanged"
        | TrackMaterialized -> "TrackMaterialized"
        | PlaylistChanged -> "PlaylistChanged"
        | PlaylistTrackQueued -> "PlaylistTrackQueued"

    let tryParse value =
        match value with
        | "TrackRequested" -> Some TrackRequested
        | "TrackRequestMatched" -> Some TrackRequestMatched
        | "SayMessageSubmitted" -> Some SayMessageSubmitted
        | "TelegramCommandReceived" -> Some TelegramCommandReceived
        | "TelegramCallbackReceived" -> Some TelegramCallbackReceived
        | "SayMessageModerated" -> Some SayMessageModerated
        | "DonationInvoiceCreated" -> Some DonationInvoiceCreated
        | "DonationPaid" -> Some DonationPaid
        | "PaymentRefunded" -> Some PaymentRefunded
        | "LibraryScanRequested" -> Some LibraryScanRequested
        | "TrackDiscovered" -> Some TrackDiscovered
        | "PlaybackQueueItemClaimed" -> Some PlaybackQueueItemClaimed
        | "PlaybackStarted" -> Some PlaybackStarted
        | "PlaybackEnded" -> Some PlaybackEnded
        | "StreamNodeHeartbeatReceived" -> Some StreamNodeHeartbeatReceived
        | "StreamNodeFailureDetected" -> Some StreamNodeFailureDetected
        | "AdminGoalChanged" -> Some AdminGoalChanged
        | "SocialLinkChanged" -> Some SocialLinkChanged
        | "BannerChanged" -> Some BannerChanged
        | "PlaybackReordered" -> Some PlaybackReordered
        | "PlaybackSkipped" -> Some PlaybackSkipped
        | "PlaybackRestarted" -> Some PlaybackRestarted
        | "TrackForcePlayed" -> Some TrackForcePlayed
        | "AdminTrackQueued" -> Some AdminTrackQueued
        | "TrackMetadataChanged" -> Some TrackMetadataChanged
        | "TrackMaterialized" -> Some TrackMaterialized
        | "PlaylistChanged" -> Some PlaylistChanged
        | "PlaylistTrackQueued" -> Some PlaylistTrackQueued
        | _ -> None

/// The event audience is intentionally exhaustive: adding a new event requires
/// choosing which process is allowed to claim and dispatch it.
[<RequireQualifiedAccess>]
module DomainEventAudience =
    let forType eventType =
        match eventType with
        | TrackRequested
        | TrackRequestMatched
        | SayMessageSubmitted
        | SayMessageModerated
        | TelegramCommandReceived
        | TelegramCallbackReceived
        | DonationInvoiceCreated
        | DonationPaid
        | PaymentRefunded -> OutboxAudience.Telegram
        | LibraryScanRequested
        | TrackDiscovered
        | PlaybackQueueItemClaimed
        | PlaybackStarted
        | PlaybackEnded
        | StreamNodeHeartbeatReceived
        | StreamNodeFailureDetected
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
        | PlaylistTrackQueued -> OutboxAudience.Api

[<RequireQualifiedAccess>]
module DomainJson =
    /// The canonical serializer for event payloads crossing process boundaries.
    let options = JsonSerializerOptions(JsonSerializerDefaults.Web)

    do
        options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        options.DictionaryKeyPolicy <- JsonNamingPolicy.CamelCase


type DomainEventError =
    | ProducerRequired
    | PayloadMustBeJsonObject
    | PayloadJsonInvalid of message: string

[<RequireQualifiedAccess>]
module DomainEventError =
    let toMessage error =
        match error with
        | ProducerRequired -> "Domain event producer is required."
        | PayloadMustBeJsonObject -> "Domain event payload must be a JSON object."
        | PayloadJsonInvalid message -> sprintf "Domain event payload is invalid JSON: %s" message

type DomainEventEnvelope =
    { EventId: Guid
      EventType: DomainEventType
      OccurredAtUtc: DateTimeOffset
      Producer: string
      CorrelationId: Guid
      CausationId: Guid option
      PayloadJson: string }

[<RequireQualifiedAccess>]
module DomainEventEnvelope =
    let private payloadJsonObject (payloadJson: string) : Result<string, DomainEventError> =
        if String.IsNullOrWhiteSpace payloadJson then
            Error PayloadMustBeJsonObject
        else
            let parsed =
                try
                    JsonDocument.Parse(payloadJson) |> Ok
                with
                | :? JsonException as ex -> Error ex

            parsed
            |> Result.mapError (fun ex -> PayloadJsonInvalid ex.Message)
            |> Result.bind (fun document ->
                use document = document

                if document.RootElement.ValueKind = JsonValueKind.Object then
                    Ok(document.RootElement.GetRawText())
                else
                    Error PayloadMustBeJsonObject)

    /// Creates an envelope using the platform time abstraction and UUIDv7.
    /// Event and implicit correlation identifiers are generated only after the
    /// producer/payload validations succeed, preserving the old failure behavior.
    let create
        (timeProvider: TimeProvider)
        (eventType: DomainEventType)
        (producer: string)
        (correlationId: Guid option)
        (causationId: Guid option)
        (payloadJson: string)
        : Result<DomainEventEnvelope, DomainEventError> =
        result {
            do! (not (String.IsNullOrWhiteSpace producer)) |> Result.requireTrue ProducerRequired
            let! payloadJson = payloadJsonObject payloadJson
            let correlationId = correlationId |> Option.defaultWith (fun () -> Uuid.CreateVersion7().ToGuidBigEndian())

            return
                { EventId = Uuid.CreateVersion7().ToGuidBigEndian()
                  EventType = eventType
                  OccurredAtUtc = timeProvider.GetUtcNow()
                  Producer = producer
                  CorrelationId = correlationId
                  CausationId = causationId
                  PayloadJson = payloadJson }
        }

    let toJson (envelope: DomainEventEnvelope) =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))
        writer.WriteStartObject()
        writer.WriteString("eventId", envelope.EventId)
        writer.WriteString("eventType", DomainEventType.toString envelope.EventType)
        writer.WriteString("occurredAtUtc", envelope.OccurredAtUtc)
        writer.WriteString("producer", envelope.Producer)
        writer.WriteString("correlationId", envelope.CorrelationId)

        match envelope.CausationId with
        | Some causationId -> writer.WriteString("causationId", causationId)
        | None -> writer.WriteNull("causationId")

        writer.WritePropertyName("payload")
        writer.WriteRawValue(envelope.PayloadJson)
        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(buffer.ToArray())

module OutboxMapping =
    let toOutboxEvent (envelope: DomainEventEnvelope) : OutboxEventToAppend =
        { Id = envelope.EventId
          EventType = DomainEventType.toString envelope.EventType
          Audience = DomainEventAudience.forType envelope.EventType
          OccurredAtUtc = envelope.OccurredAtUtc
          Producer = envelope.Producer
          CorrelationId = Some envelope.CorrelationId
          CausationId = envelope.CausationId
          PayloadJson = envelope.PayloadJson }
