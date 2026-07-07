namespace Web10.Radio.API

open System
open Microsoft.Extensions.DependencyInjection

type IClock =
    abstract member UtcNow: DateTimeOffset

type SystemClock() =
    interface IClock with
        member _.UtcNow = DateTimeOffset.UtcNow

module ApplicationComposition =
    let addApplicationServices (services: IServiceCollection) : IServiceCollection =
        services.AddSingleton<IClock, SystemClock>()
