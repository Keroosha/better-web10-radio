namespace Web10.Radio.StreamNode

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type ProcessKind =
    | Xvfb
    | Chromium
    | Unclutter
    | Liquidsoap

[<CLIMutable>]
type RestartDecision =
    { Allowed: bool
      Attempt: int
      Delay: TimeSpan }

type RestartBudget(?maxRestarts: int, ?window: TimeSpan) =
    let maxRestarts = defaultArg maxRestarts 5
    let window = defaultArg window (TimeSpan.FromSeconds 300.0)
    let mutable restarts: int64 list = []

    member _.Record(timeProvider: TimeProvider) =
        let now = timeProvider.GetTimestamp()
        restarts <- restarts |> List.filter (fun timestamp -> timeProvider.GetElapsedTime(timestamp, now) < window)
        let attempt = restarts.Length + 1
        if attempt > maxRestarts then
            { Allowed = false; Attempt = attempt; Delay = TimeSpan.Zero }
        else
            restarts <- now :: restarts
            { Allowed = true; Attempt = attempt; Delay = TimeSpan.FromSeconds(float (pown 2 (attempt - 1))) }

    member _.Reset() = restarts <- []

    member _.Count = restarts.Length

type IManagedProcess =
    abstract ProcessId: int
    abstract IsAlive: bool
    abstract SendTerminate: unit -> unit
    abstract Kill: unit -> unit
    abstract WaitForExitAsync: CancellationToken -> Task

type IProcessFactory =
    abstract Start: ProcessKind * string list * IReadOnlyDictionary<string, string> * string * bool -> IManagedProcess

type private ManagedProcess(process: Process) =
    let terminate () =
        try
            if not process.HasExited then
                // Sending TERM through the process group preserves child ownership.
                use signal = new Process()
                signal.StartInfo <-
                    ProcessStartInfo(
                        FileName = "/bin/kill",
                        Arguments = sprintf "-TERM -%d" process.Id,
                        UseShellExecute = false,
                        CreateNoWindow = true)
                signal.Start() |> ignore
                signal.WaitForExit(1000) |> ignore
        with _ -> ()

    interface IManagedProcess with
        member _.ProcessId = process.Id
        member _.IsAlive =
            try not process.HasExited with _ -> false
        member _.SendTerminate() = terminate ()
        member _.Kill() =
            try
                if not process.HasExited then process.Kill(true)
            with _ -> ()
        member _.WaitForExitAsync(token) = process.WaitForExitAsync(token) :> Task

[<Sealed>]
type SystemProcessFactory() =
    interface IProcessFactory with
        member _.Start(kind, command, environment, workingDirectory, streamOutput) =
            if List.isEmpty command then invalidArg (nameof command) "A process command is required."
            let info = ProcessStartInfo()
            info.FileName <- List.head command
            for argument in List.tail command do info.ArgumentList.Add(argument)
            info.WorkingDirectory <- workingDirectory
            info.UseShellExecute <- false
            info.CreateNoWindow <- true
            if not streamOutput then
                info.RedirectStandardOutput <- true
                info.RedirectStandardError <- true
            for pair in environment do info.Environment[pair.Key] <- pair.Value
            let process = new Process()
            process.StartInfo <- info
            if not (process.Start()) then invalidOp (sprintf "Unable to start %A." kind)
            ManagedProcess(process) :> IManagedProcess

module ProcessSupervisor =
    let isAlive (process: IManagedProcess option) = process |> Option.exists (fun value -> value.IsAlive)

    let stopAsync (timeProvider: TimeProvider) (grace: TimeSpan) (process: IManagedProcess option) (token: CancellationToken) =
        task {
            match process with
            | None -> ()
            | Some value when not value.IsAlive -> ()
            | Some value ->
                value.SendTerminate()
                let deadline = timeProvider.GetTimestamp()
                let mutable exited = not value.IsAlive
                while not exited && timeProvider.GetElapsedTime(deadline) < grace && not token.IsCancellationRequested do
                    do! Monotonic.delay timeProvider (TimeSpan.FromMilliseconds 50.0) token
                    exited <- not value.IsAlive
                if not exited then value.Kill()
        }

    let stopManyAsync timeProvider grace (processes: IManagedProcess option list) token =
        task {
            for process in processes do
                do! stopAsync timeProvider grace process token
        }
