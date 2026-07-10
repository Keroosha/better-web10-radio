namespace Web10.Radio.Tests

open FluentMigrator
open NUnit.Framework
open Web10.Radio.Database.Migrations

module MigrationMetadataTests =
    [<Test>]
    let ``migration versions use YYYYMMDDmmss int64 format`` () =
        let migrations =
            typeof<CreateInitialSchema>.Assembly.GetTypes()
            |> Array.choose (fun migrationType ->
                migrationType.GetCustomAttributes(typeof<MigrationAttribute>, false)
                |> Array.tryHead
                |> Option.map (fun attribute -> attribute :?> MigrationAttribute))

        Assert.That(migrations, Is.Not.Empty)

        let versions = migrations |> Array.map (fun migration -> migration.Version) |> Array.sort

        versions
        |> Array.iter (fun version -> Assert.That(version.ToString(), Does.Match("^\\d{12}$")))

        Assert.That(versions, Is.EqualTo(([| 202607080001L; 202607100001L; 202607100002L |] : int64 array) :> obj), "Durable workflow migrations must remain discoverable and ordered after the initial schema.")
