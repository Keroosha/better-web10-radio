namespace Web10.Radio.Application

open System
open Microsoft.Extensions.DependencyInjection

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
        services.AddSingleton<TimeProvider>(TimeProvider.System) |> ignore
        services
