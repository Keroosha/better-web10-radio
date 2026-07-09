namespace Web10.Radio.Tests

open NUnit.Framework
open Web10.Radio.Database.Repositories

module RepositoryErrorTests =
    [<Test>]
    let ``repository errors render stable public messages`` () =
        Assert.That(RepositoryError.toMessage (InvalidBatchSize 0), Is.EqualTo("Batch size must be positive. Actual: 0."))
        Assert.That(RepositoryError.toMessage (InvalidStreamNodeStatus "Paused"), Is.EqualTo("Invalid stream-node status: Paused."))
        Assert.That(
            RepositoryError.toMessage (DatabaseError("OutboxEventRepository.claimDue", "connection failed")),
            Is.EqualTo("Database operation failed: OutboxEventRepository.claimDue: connection failed")
        )
