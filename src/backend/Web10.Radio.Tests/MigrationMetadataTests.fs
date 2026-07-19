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

        Assert.That(
            versions,
            Is.EqualTo(([| 202607080001L; 202607100001L; 202607100002L; 202607100003L; 202607100004L; 202607110001L; 202607110002L; 202607110003L; 202607110004L; 202607120001L; 202607120002L; 202607120003L; 202607130001L; 202607130002L; 202607130003L; 202607140001L |] : int64 array) :> obj),
            "The ordered schema sequence must include the 20260713 storage cache settings, super chat banner, FLAC CUE migrations, and 20260714 library scan reconciliation migration."
        )
