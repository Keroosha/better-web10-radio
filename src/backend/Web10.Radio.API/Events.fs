namespace Web10.Radio.API

open System
open System.IO
open System.Text
open System.Text.Json
open FsToolkit.ErrorHandling

type DomainEventType =
    | TrackRequested
    | TrackRequestMatched
    | SayMessageSubmitted
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

module DomainEventType =
    let all =
        [ TrackRequested
          TrackRequestMatched
          SayMessageSubmitted
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
          SocialLinkChanged ]

    let toString eventType =
        match eventType with
        | TrackRequested -> "TrackRequested"
        | TrackRequestMatched -> "TrackRequestMatched"
        | SayMessageSubmitted -> "SayMessageSubmitted"
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

    let tryParse value =
        match value with
        | "TrackRequested" -> Some TrackRequested
        | "TrackRequestMatched" -> Some TrackRequestMatched
        | "SayMessageSubmitted" -> Some SayMessageSubmitted
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
        | _ -> None

type DomainEventError =
    | ProducerRequired
    | PayloadMustBeJsonObject
    | PayloadJsonInvalid of message: string

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

    let create
        (idGenerator: IIdGenerator)
        (clock: IClock)
        (eventType: DomainEventType)
        (producer: string)
        (correlationId: Guid option)
        (causationId: Guid option)
        (payloadJson: string)
        : Result<DomainEventEnvelope, DomainEventError> =
        result {
            do! (not (String.IsNullOrWhiteSpace producer)) |> Result.requireTrue ProducerRequired
            let! payloadJson = payloadJsonObject payloadJson
            let correlationId = correlationId |> Option.defaultWith idGenerator.NewId

            return
                { EventId = idGenerator.NewId()
                  EventType = eventType
                  OccurredAtUtc = clock.UtcNow
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
