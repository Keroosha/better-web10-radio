namespace Web10.Radio.Tests

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Diagnostics.HealthChecks
open NUnit.Framework
open Web10.Radio.API

module HealthOperabilityTests =
    type private IdentityProbe(result: Choice<bool, exn>) =
        interface ITelegramIdentityProbe with
            member _.IsAuthenticatedBotAsync(cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    match result with
                    | Choice1Of2 value -> return value
                    | Choice2Of2 error -> return raise error
                }

    type private BucketProbe(result: Choice<unit, exn>) =
        interface IS3ObjectEnumerator with
            member _.VisitPagesAsync(_, _, cancellationToken) =
                cancellationToken.ThrowIfCancellationRequested()
                Task.CompletedTask

            member _.ProbeBucketAsync(_, cancellationToken) =
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    match result with
                    | Choice1Of2 () -> return ()
                    | Choice2Of2 error -> return raise error
                }
                :> Task

    let private storageOptions storageType localRoot bucket =
        { Type = storageType
          LocalRoot = localRoot
          S3Bucket = bucket
          S3Region = "us-east-1"
          S3ServiceUrl = None
          S3ForcePathStyle = false }

    let private check (healthCheck: IHealthCheck) cancellationToken =
        healthCheck.CheckHealthAsync(Unchecked.defaultof<HealthCheckContext>, cancellationToken)

    [<Test>]
    let ``Telegram adapter health is healthy only for an authenticated bot identity`` () =
        task {
            let cases =
                [ "authenticated", IdentityProbe(Choice1Of2 true) :> ITelegramIdentityProbe, HealthStatus.Healthy
                  "rejected", IdentityProbe(Choice1Of2 false) :> ITelegramIdentityProbe, HealthStatus.Unhealthy
                  "probe exception", IdentityProbe(Choice2Of2(InvalidOperationException("network failure"))) :> ITelegramIdentityProbe, HealthStatus.Unhealthy ]

            for name, probe, expectedStatus in cases do
                let! result = check (TelegramAdapterHealthCheck(probe) :> IHealthCheck) CancellationToken.None
                Assert.That(result.Status, Is.EqualTo(expectedStatus), name)
        }

    [<Test>]
    let ``storage health verifies Local writeability and S3 bucket probing`` () =
        task {
            let temporaryDirectory = Directory.CreateTempSubdirectory("web10-radio-health-")

            try
                let localSuccess =
                    StorageHealthCheck(storageOptions Local temporaryDirectory.FullName "", BucketProbe(Choice1Of2 ()) :> IS3ObjectEnumerator)
                    :> IHealthCheck

                let! localSuccessResult = check localSuccess CancellationToken.None
                Assert.That(localSuccessResult.Status, Is.EqualTo(HealthStatus.Healthy))
                Assert.That(Directory.EnumerateFiles(temporaryDirectory.FullName, ".web10-readiness-*.tmp") |> Seq.isEmpty, Is.True, "The Local readiness probe must delete its write marker.")

                let missingLocal =
                    StorageHealthCheck(storageOptions Local (Path.Combine(temporaryDirectory.FullName, "missing")) "", BucketProbe(Choice1Of2 ()) :> IS3ObjectEnumerator)
                    :> IHealthCheck

                let! missingResult = check missingLocal CancellationToken.None
                Assert.That(missingResult.Status, Is.EqualTo(HealthStatus.Unhealthy))

                let unwritableLocal =
                    StorageHealthCheck(storageOptions Local "/proc" "", BucketProbe(Choice1Of2 ()) :> IS3ObjectEnumerator)
                    :> IHealthCheck

                let! unwritableResult = check unwritableLocal CancellationToken.None
                Assert.That(unwritableResult.Status, Is.EqualTo(HealthStatus.Unhealthy))

                let s3Success =
                    StorageHealthCheck(storageOptions S3 "" "radio-test-bucket", BucketProbe(Choice1Of2 ()) :> IS3ObjectEnumerator)
                    :> IHealthCheck

                let! s3SuccessResult = check s3Success CancellationToken.None
                Assert.That(s3SuccessResult.Status, Is.EqualTo(HealthStatus.Healthy))

                let s3Failure =
                    StorageHealthCheck(storageOptions S3 "" "radio-test-bucket", BucketProbe(Choice2Of2(InvalidOperationException("denied"))) :> IS3ObjectEnumerator)
                    :> IHealthCheck

                let! s3FailureResult = check s3Failure CancellationToken.None
                Assert.That(s3FailureResult.Status, Is.EqualTo(HealthStatus.Unhealthy))
            finally
                temporaryDirectory.Delete(true)
        }

    [<Test>]
    let ``health probes propagate caller cancellation instead of reporting a false healthy result`` () =
        task {
            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()
            let telegram = TelegramAdapterHealthCheck(IdentityProbe(Choice1Of2 true) :> ITelegramIdentityProbe) :> IHealthCheck

            try
                let! _ = check telegram cancellation.Token
                Assert.Fail("Expected Telegram health check cancellation to propagate.")
            with :? OperationCanceledException -> ()

            let storage =
                StorageHealthCheck(storageOptions S3 "" "radio-test-bucket", BucketProbe(Choice1Of2 ()) :> IS3ObjectEnumerator)
                :> IHealthCheck

            try
                let! _ = check storage cancellation.Token
                Assert.Fail("Expected S3 health check cancellation to propagate.")
            with :? OperationCanceledException -> ()
        }
