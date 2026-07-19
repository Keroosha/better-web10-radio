namespace Web10.Radio.Tests

open System
open System.IO
open System.Text.RegularExpressions
open NUnit.Framework

module AuditRegressionTests =
    let private repositoryRoot =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

    let private readRelativePath relativePath =
        let fullPath = Path.Combine(repositoryRoot, relativePath)
        fullPath, File.ReadAllText(fullPath)

    let private productionFSharpFiles () =
        [ "src/backend/Web10.Radio.API"
          "src/backend/Web10.Radio.Database"
          "src/backend/Web10.Radio.Telegram"
          "src/backend/Web10.Radio.Migrator" ]
        |> List.collect (fun relativeDirectory ->
            let fullDirectory = Path.Combine(repositoryRoot, relativeDirectory)

            Directory.EnumerateFiles(fullDirectory, "*.fs", SearchOption.AllDirectories)
            |> Seq.filter (fun path -> not (path.Contains(string Path.DirectorySeparatorChar + "Migrations" + string Path.DirectorySeparatorChar)))
            |> Seq.map (fun path -> Path.GetRelativePath(repositoryRoot, path), File.ReadAllText(path))
            |> List.ofSeq)

    let private assertNoMarker (marker: string) (sources: (string * string) list) =
        let violations =
            sources
            |> List.choose (fun (path, source) ->
                if source.Contains(marker, StringComparison.OrdinalIgnoreCase) then Some path else None)

        Assert.That(violations, Is.Empty, sprintf "Production sources must not contain %s. Violations: %s" marker (String.Join(", ", violations)))
    let private hardDeletePattern = Regex(@"\bDELETE\s+FROM\b", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    let private findHardDeleteViolations (sources: (string * string) list) =
        sources
        |> List.choose (fun (path, source) ->
            if hardDeletePattern.IsMatch(source) then Some path else None)

    let private assertNoHardDeletes (sources: (string * string) list) =
        let violations = findHardDeleteViolations sources

        Assert.That(
            violations,
            Is.Empty,
            sprintf "Production sources must not contain hard-delete SQL matching %s. Violations: %s" (hardDeletePattern.ToString()) (String.Join(", ", violations))
        )

    [<Test>]
    let ``hard-delete policy recognizes case-insensitive whitespace-separated SQL and reports its source path`` () =
        let sourcePath = "src/backend/Web10.Radio.Database/Repository.fs"
        let source = "let statement = \"delete \n\tFROM Tracks\""

        let violations = findHardDeleteViolations [ sourcePath, source ]

        Assert.That((violations = [ sourcePath ]), Is.True, "The source policy must report the violating path.")

    [<Test>]
    let ``application source retains ADO.NET soft-delete policy`` () =
        let productionSources = productionFSharpFiles ()

        assertNoHardDeletes productionSources

        [ "Dapper"
          "EntityFramework"
          "Microsoft.EntityFrameworkCore" ]
        |> List.iter (fun marker -> assertNoMarker marker productionSources)

    [<Test>]
    let ``runtime image definitions retain supported chiseled and Debian bases`` () =
        let apiDockerfilePath, apiDockerfile = readRelativePath "src/backend/Dockerfile"
        let migratorDockerfilePath, migratorDockerfile = readRelativePath "src/backend/Dockerfile.migrator"
        let streamNodeDockerfilePath, streamNodeDockerfile = readRelativePath "src/stream-node/Dockerfile"
        let xrayDockerfilePath, xrayDockerfile = readRelativePath "deploy/Dockerfile.xray"
        let composePath, compose = readRelativePath "compose.yaml"
        let imageDefinitions =
            [ apiDockerfilePath, apiDockerfile
              migratorDockerfilePath, migratorDockerfile
              streamNodeDockerfilePath, streamNodeDockerfile
              xrayDockerfilePath, xrayDockerfile
              composePath, compose ]

        Assert.That(apiDockerfile, Does.Contain("mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled"))
        Assert.That(migratorDockerfile, Does.Contain("mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled"))
        Assert.That(streamNodeDockerfile, Does.Contain("FROM debian:trixie-slim"))
        Assert.That(xrayDockerfile, Does.Contain("FROM debian:trixie-slim"))

        [ "alpine"
          "libmusl"
          "musl" ]
        |> List.iter (fun forbidden -> assertNoMarker forbidden imageDefinitions)
