namespace Web10.Radio.Application

open System
open System.Threading
open System.Threading.Tasks
open Dodo.Primitives
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql
open Web10.Radio.Database.Repositories

/// Generic relay shared by each process. The audience is supplied by the
/// composition root, so a service can never claim the other process's events.
type OutboxRelayHostedService
    (
        audience: OutboxAudience,
        dataSource: NpgsqlDataSource,
        dispatcher: IDomainEventDispatcher,
        timeProvider: TimeProvider,
        logger: ILogger<OutboxRelayHostedService>
    ) as this =
    inherit BackgroundService()

    let claimOwner = Uuid.CreateVersion7().ToGuidBigEndian()

    let toEnvelope eventType (record: OutboxEventRecord) =
        { EventId = record.Id
          EventType = eventType
          OccurredAtUtc = record.OccurredAtUtc
          Producer = record.Producer
          CorrelationId = record.CorrelationId |> Option.defaultWith (fun () -> Uuid.CreateVersion7().ToGuidBigEndian())
          CausationId = record.CausationId
          PayloadJson = record.PayloadJson }

    let bestEffortMarkFailed (record: OutboxEventRecord) nextAttemptAtUtc failedAtUtc cancellationToken =
        task {
            let! result =
                OutboxEventRepository.markFailed
                    dataSource
                    record.Id
                    record.ClaimOwner
                    record.ClaimAttempt
                    nextAttemptAtUtc
                    failedAtUtc
                    cancellationToken

            match result with
            | Ok true -> ()
            | Ok false ->
                logger.LogWarning(
                    "Outbox failure fence rejected for event {EventId}, claim owner {ClaimOwner}, attempt {ClaimAttempt}.",
                    record.Id,
                    record.ClaimOwner,
                    record.ClaimAttempt
                )
            | Error error ->
                logger.LogError(
                    "Outbox failure marking failed for event {EventId}, claim owner {ClaimOwner}, attempt {ClaimAttempt}: {Error}.",
                    record.Id,
                    record.ClaimOwner,
                    record.ClaimAttempt,
                    RepositoryError.toMessage error
                )
        }

    member _.ProcessDueEventsOnceAsync(cancellationToken: CancellationToken) : Task<Result<int, BackgroundWorkerError>> =
        task {
            let nowUtc = timeProvider.GetUtcNow()
            let! claimResult =
                OutboxEventRepository.tryClaimDueOrdered dataSource audience claimOwner nowUtc 1 cancellationToken

            match claimResult with
            | Error repositoryError -> return Error(RepositoryError repositoryError)
            | Ok None -> return Ok 0
            | Ok(Some acquiredLease) ->
                use lease = acquiredLease

                match lease.Records with
                | [] -> return Ok 0
                | record :: _ ->
                    match DomainEventType.tryParse record.EventType with
                    | None ->
                        let failedAtUtc = timeProvider.GetUtcNow()
                        do! bestEffortMarkFailed record (failedAtUtc.AddHours(1.0)) failedAtUtc cancellationToken
                        return Error(UnknownEventType record.EventType)
                    | Some eventType ->
                        let envelope = toEnvelope eventType record
                        let! dispatchResult = dispatcher.DispatchAsync envelope cancellationToken

                        match dispatchResult with
                        | Error error ->
                            let failedAtUtc = timeProvider.GetUtcNow()
                            do! bestEffortMarkFailed record (failedAtUtc.AddSeconds(2.0)) failedAtUtc cancellationToken
                            return Error error
                        | Ok () ->
                            let! markResult =
                                OutboxEventRepository.markProcessed
                                    dataSource
                                    record.Id
                                    record.ClaimOwner
                                    record.ClaimAttempt
                                    (timeProvider.GetUtcNow())
                                    cancellationToken

                            match markResult with
                            | Error repositoryError -> return Error(RepositoryError repositoryError)
                            | Ok false ->
                                return Error(StateTransitionRejected("mark fenced outbox event processed", record.Id))
                            | Ok true -> return Ok 1
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                while not stoppingToken.IsCancellationRequested do
                    let! result = this.ProcessDueEventsOnceAsync(stoppingToken)

                    match result with
                    | Ok 0 -> do! Task.Delay(TimeSpan.FromSeconds(1.0), stoppingToken)
                    | Ok _ -> ()
                    | Error error ->
                        logger.LogError("Outbox relay failed: {Error}", BackgroundWorkerError.toMessage error)
                        do! Task.Delay(TimeSpan.FromSeconds(1.0), stoppingToken)
            with
            | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
        }
        :> Task
