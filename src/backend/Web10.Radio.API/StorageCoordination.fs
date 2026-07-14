namespace Web10.Radio.API

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type StorageBackendKey =
    | Default
    | Additional of Guid

[<Sealed>]
type private CoordinatorLease(semaphore: SemaphoreSlim) =
    interface IAsyncDisposable with
        member _.DisposeAsync() =
            semaphore.Release() |> ignore
            ValueTask()

[<Sealed>]
type StorageOperationCoordinator() =
    let semaphores = ConcurrentDictionary<StorageBackendKey, SemaphoreSlim>()

    member _.AcquireAsync(key: StorageBackendKey, cancellationToken: CancellationToken) : ValueTask<IAsyncDisposable> =
        let semaphore = semaphores.GetOrAdd(key, fun _ -> new SemaphoreSlim(1, 1))
        let waitTask = task {
            do! semaphore.WaitAsync(cancellationToken)
            return (CoordinatorLease(semaphore) :> IAsyncDisposable)
        }
        ValueTask<IAsyncDisposable>(waitTask)
