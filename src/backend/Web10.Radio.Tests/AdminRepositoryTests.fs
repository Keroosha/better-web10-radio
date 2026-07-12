namespace Web10.Radio.Tests

open System
open NUnit.Framework
open Web10.Radio.Database.Repositories

[<TestFixture>]
type AdminRepositoryTests() =
    [<Test>]
    member _.``admin mutations distinguish applied not-found and conflict``() =
        let applied = AdminContentMutation.Applied 42
        let notFound = AdminContentMutation.NotFound
        let conflict = AdminContentMutation.Conflict
        match applied, notFound, conflict with
        | AdminContentMutation.Applied value, AdminContentMutation.NotFound, AdminContentMutation.Conflict ->
            Assert.That(value, Is.EqualTo(42))
        | _ -> Assert.Fail("Unexpected admin mutation cases.")

    [<Test>]
    member _.``playlist policy model carries scheduler and source semantics``() =
        let playlist =
            { Id = Guid.Parse("019f0000-0000-7000-8000-000000000001")
              Name = "All tracks"
              Description = Some "Managed library"
              IsActive = true
              Type = PlaylistType.General
              Source = PlaylistSource.AllStorage
              Order = PlaylistOrder.Shuffle
              Weight = 10
              IsJingle = false
              Interrupt = false
              AvoidDuplicates = true
              PlayEverySongs = None
              PlayEveryMinutes = Some 30
              PlayAtMinute = None
              IsSystem = true
              ItemCount = 0
              Schedules = [] }
        Assert.That(playlist.Source, Is.EqualTo(PlaylistSource.AllStorage))
        Assert.That(playlist.AvoidDuplicates, Is.True)
        Assert.That(playlist.PlayEveryMinutes, Is.EqualTo(Some 30))

    [<Test>]
    member _.``queue records retain priority and ownership metadata``() =
        let item =
            { Id = Guid.Parse("019f0000-0000-7000-8000-000000000002")
              TrackId = Guid.Parse("019f0000-0000-7000-8000-000000000003")
              Source = "admin"
              Status = "queued"
              Priority = 100L
              PlaylistId = None
              RequestedAtUtc = DateTimeOffset.UtcNow }
        Assert.That(item.Priority, Is.EqualTo(100L))
        Assert.That(item.Status, Is.EqualTo("queued"))
