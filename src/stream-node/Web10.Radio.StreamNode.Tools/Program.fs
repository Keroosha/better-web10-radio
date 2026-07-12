namespace Web10.Radio.StreamNode.Tools

open System
open System.Threading

module Program =
    let private usage () =
        printfn "Usage: Web10.Radio.StreamNode.Tools fake [options]"
        printfn "       Web10.Radio.StreamNode.Tools smoke-backend --mode <restart-live|reorder|skip|restart-current|play-now|expect-output-failure|recover> --base-url <url> --rtmp-stat-url <url> --username <name> --password <password> [--timeout-seconds <seconds>]"

    let private run command arguments =
        let parsed = Parsing.parseOptions arguments
        use cancellation = new CancellationTokenSource()
        Console.CancelKeyPress.Add(fun event ->
            event.Cancel <- true
            cancellation.Cancel())
        task {
            match command with
            | "fake" ->
                let settings = FakeMode.options parsed
                do! FakeMode.run Time.system settings cancellation.Token
                return 0
            | "smoke-backend" ->
                let settings = SmokeBackend.options parsed
                do! SmokeBackend.run Time.system settings cancellation.Token
                printfn "%s status=passed" settings.Mode
                return 0
            | _ ->
                usage ()
                return 2
        }

    [<EntryPoint>]
    let main argv =
        try
            if argv.Length = 0 || argv[0] = "--help" || argv[0] = "-h" then
                usage ()
                if argv.Length = 0 then 2 else 0
            else
                run argv[0] (argv |> Array.skip 1 |> Array.toList) |> fun pending -> pending.GetAwaiter().GetResult()
        with
        | :? ToolError as error ->
            eprintfn "%s status=%s" error.Operation error.Status
            1
        | :? OperationCanceledException ->
            eprintfn "main status=interrupted"
            130
        | _ ->
            eprintfn "main status=failed"
            1
