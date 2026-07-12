namespace Web10.Radio.StreamNode

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks

module Program =
    let private healthCheck (url: string) =
        task {
            use client = new HttpClient()
            client.Timeout <- TimeSpan.FromSeconds 2.0
            try
                use! response = client.GetAsync(url)
                return if int response.StatusCode = 204 || int response.StatusCode = 200 then 0 else 1
            with _ -> return 1
        }

    let private loadConfig () =
        match Configuration.fromEnvironment () with
        | Ok value -> Ok value
        | Error error ->
            Console.Error.WriteLine(Configuration.describe error)
            Error error

    [<EntryPoint>]
    let main argv =
        let arguments = if isNull argv then [||] else argv
        let command, rest =
            match arguments with
            | [| "--health-check" |] -> "health-check", [||]
            | [| "--health-check"; url |] -> "health-check", [| url |]
            | [| command |] -> command, [||]
            | [| command; tail |] -> command, [| tail |]
            | _ -> "", [||]
        match command with
        | "health-check" ->
            let url = if rest.Length = 0 then "http://127.0.0.1:18080/healthz" else rest[0]
            healthCheck url |> fun task -> task.GetAwaiter().GetResult()
        | "validate-config" ->
            match loadConfig () with
            | Ok _ -> 0
            | Error _ -> 1
        | "run" ->
            match loadConfig () with
            | Error _ -> 1
            | Ok config ->
                use cancellation = new CancellationTokenSource()
                Console.CancelKeyPress.Add(fun event -> event.Cancel <- true; cancellation.Cancel())
                let runtime = RuntimeSupervisor(config)
                try
                    runtime.RunAsync(cancellation.Token).GetAwaiter().GetResult()
                    0
                with :? OperationCanceledException -> 0
        | _ ->
            Console.Error.WriteLine("usage: Web10.Radio.StreamNode run|validate-config|--health-check [url]")
            2
