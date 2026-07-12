#nowarn "3536"

namespace Web10.Radio.API

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open Amazon
open Amazon.S3
open Amazon.S3.Model
open Microsoft.Extensions.DependencyInjection

type S3ObjectDescriptor =
    { Key: string
      SizeBytes: int64 }

type IS3ObjectStorage =
    abstract member VisitPagesAsync:
        bucketName: string *
        visitPage: (IReadOnlyList<S3ObjectDescriptor> -> CancellationToken -> Task) *
        cancellationToken: CancellationToken -> Task

    abstract member ProbeBucketAsync: bucketName: string * cancellationToken: CancellationToken -> Task

    abstract member DownloadToFileAsync:
        bucketName: string * key: string * destination: string * cancellationToken: CancellationToken -> Task

module S3KeyValidation =
    let isCanonical (key: string) =
        not (String.IsNullOrWhiteSpace key)
        && not (key.Contains("\u0000", StringComparison.Ordinal))
        && not (key.Contains("\\", StringComparison.Ordinal))
        && (key.Split('/', StringSplitOptions.None)
            |> Array.forall (fun segment -> not (String.IsNullOrEmpty segment) && segment <> "." && segment <> ".."))

    let requireCanonical key =
        if not (isCanonical key) then
            raise (ArgumentException("S3 object key is not canonical.", nameof key))

type S3ObjectEnumerator(client: IAmazonS3) =
    let listPage bucketName maxKeys continuationToken cancellationToken =
        let request =
            ListObjectsV2Request(
                BucketName = bucketName,
                MaxKeys = Nullable maxKeys,
                ContinuationToken = continuationToken
            )

        client.ListObjectsV2Async(request, cancellationToken)

    interface IS3ObjectStorage with
        member _.VisitPagesAsync(bucketName, visitPage, cancellationToken) =
            task {
                ArgumentException.ThrowIfNullOrWhiteSpace bucketName
                ArgumentNullException.ThrowIfNull visitPage
                cancellationToken.ThrowIfCancellationRequested()

                let mutable continuationToken: string = null
                let mutable hasMore = true

                while hasMore do
                    cancellationToken.ThrowIfCancellationRequested()
                    let! response = listPage bucketName 1000 continuationToken cancellationToken

                    let page =
                        if isNull response.S3Objects then
                            Array.empty<S3ObjectDescriptor>
                        else
                            response.S3Objects
                            |> Seq.map (fun item ->
                                { Key = item.Key
                                  SizeBytes = item.Size.GetValueOrDefault() })
                            |> Seq.toArray

                    do! visitPage (page :> IReadOnlyList<S3ObjectDescriptor>) cancellationToken
                    hasMore <- response.IsTruncated.GetValueOrDefault()

                    if hasMore && String.IsNullOrWhiteSpace response.NextContinuationToken then
                        raise (AmazonS3Exception("S3 returned a truncated ListObjectsV2 page without a continuation token."))

                    continuationToken <- response.NextContinuationToken
            }
            :> Task

        member _.ProbeBucketAsync(bucketName, cancellationToken) =
            task {
                ArgumentException.ThrowIfNullOrWhiteSpace bucketName
                cancellationToken.ThrowIfCancellationRequested()
                let! _ = listPage bucketName 1 null cancellationToken
                return ()
            }
            :> Task

        member _.DownloadToFileAsync(bucketName, key, destination, cancellationToken) =
            task {
                ArgumentException.ThrowIfNullOrWhiteSpace bucketName
                S3KeyValidation.requireCanonical key
                ArgumentException.ThrowIfNullOrWhiteSpace destination
                cancellationToken.ThrowIfCancellationRequested()

                let request = GetObjectRequest(BucketName = bucketName, Key = key)
                use! response = client.GetObjectAsync(request, cancellationToken)
                use input = response.ResponseStream
                use output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous ||| FileOptions.SequentialScan)
                do! input.CopyToAsync(output, 128 * 1024, cancellationToken)
                do! output.FlushAsync(cancellationToken)
            }
            :> Task


type private DeferredS3ObjectStorage(options: StorageOptions) =
    let client =
        lazy
            if options.Type = S3 then
                let config = AmazonS3Config(ForcePathStyle = options.S3ForcePathStyle)

                match options.S3ServiceUrl with
                | Some serviceUrl ->
                    config.ServiceURL <- serviceUrl.AbsoluteUri.TrimEnd('/')
                    config.AuthenticationRegion <- options.S3Region
                | None -> config.RegionEndpoint <- RegionEndpoint.GetBySystemName(options.S3Region)

                new AmazonS3Client(config) :> IAmazonS3
            else
                new AmazonS3Client() :> IAmazonS3

    let implementation = lazy (new S3ObjectEnumerator(client.Value) :> IS3ObjectStorage)

    interface IS3ObjectStorage with
        member _.VisitPagesAsync(bucketName, visitPage, cancellationToken) =
            implementation.Value.VisitPagesAsync(bucketName, visitPage, cancellationToken)
        member _.ProbeBucketAsync(bucketName, cancellationToken) =
            implementation.Value.ProbeBucketAsync(bucketName, cancellationToken)
        member _.DownloadToFileAsync(bucketName, key, destination, cancellationToken) =
            implementation.Value.DownloadToFileAsync(bucketName, key, destination, cancellationToken)

    interface IDisposable with
        member _.Dispose() =
            if client.IsValueCreated then
                client.Value.Dispose()

[<RequireQualifiedAccess>]
module S3StorageComposition =
    let addS3ObjectStorage (options: StorageOptions) (services: IServiceCollection) =
        services.AddSingleton<IS3ObjectStorage>(fun _ -> new DeferredS3ObjectStorage(options) :> IS3ObjectStorage)
