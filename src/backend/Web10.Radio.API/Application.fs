namespace Web10.Radio.API

open System
open Dodo.Primitives
open Microsoft.Extensions.DependencyInjection

type IClock =
    abstract member UtcNow: DateTimeOffset

type SystemClock() =
    interface IClock with
        member _.UtcNow = DateTimeOffset.UtcNow

type IIdGenerator =
    abstract member NewId: unit -> Guid

type UuidV7IdGenerator() =
    interface IIdGenerator with
        member _.NewId() =
            Uuid.CreateVersion7().ToGuidBigEndian()

type StreamNodeHeartbeatState() =
    let syncRoot = obj()
    let mutable lastHeartbeatUtc: DateTimeOffset option = None
    let mutable lastFailure: string option = None

    member _.LastHeartbeatUtc = lock syncRoot (fun () -> lastHeartbeatUtc)

    member _.LastFailure = lock syncRoot (fun () -> lastFailure)

    member _.RecordHeartbeat(heartbeatUtc: DateTimeOffset, failure: string option) =
        lock syncRoot (fun () ->
            lastHeartbeatUtc <- Some heartbeatUtc
            lastFailure <- failure)

module ApplicationComposition =
    let addApplicationServices (services: IServiceCollection) : IServiceCollection =
        services.AddSingleton<IClock, SystemClock>().AddSingleton<IIdGenerator, UuidV7IdGenerator>()
