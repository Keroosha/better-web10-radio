#nowarn "3536"

namespace Web10.Radio.API

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Text
open System.Threading.Tasks
open System.Globalization
open Amazon
open Amazon.Runtime
open Amazon.S3
open Amazon.S3.Model
open Microsoft.Extensions.DependencyInjection

[<RequireQualifiedAccess>]
type S3ClientScope =
    | ConfiguredDefault
    | AwsDefaultChain

type S3ObjectDescriptor =
    { Key: string
      SizeBytes: int64
      LastModifiedUtc: DateTimeOffset option
      ETag: string option }

type S3ObjectPage =
    { Objects: S3ObjectDescriptor list
      CommonPrefixes: string list
      NextContinuationToken: string option }

type S3ObjectMetadata =
    { ContentLength: int64
      ContentType: string option
      LastModifiedUtc: DateTimeOffset option
      ETag: string option }

type S3ReadHandle(response: GetObjectResponse, contentRange: string option) =
    member _.Stream = response.ResponseStream
    member _.ContentLength = response.ContentLength
    member _.ContentType = if String.IsNullOrWhiteSpace response.Headers.ContentType then None else Some response.Headers.ContentType
    member _.LastModifiedUtc =
        if not response.LastModified.HasValue then None
        else Some(DateTime.SpecifyKind(response.LastModified.Value, DateTimeKind.Utc) |> DateTimeOffset)
    member _.ETag = if String.IsNullOrWhiteSpace response.ETag then None else Some response.ETag
    member _.ContentRange = contentRange
    interface IDisposable with
        member _.Dispose() = response.Dispose()
type S3DeleteFailure =
    { Key: string
      Code: string
      Message: string }

type IS3ObjectStorage =
    abstract member ListPageAsync:
        scope: S3ClientScope * bucketName: string * prefix: string * delimiter: string option * maxKeys: int * continuationToken: string option * cancellationToken: CancellationToken -> Task<S3ObjectPage>
    abstract member GetMetadataAsync:
        scope: S3ClientScope * bucketName: string * key: string * cancellationToken: CancellationToken -> Task<S3ObjectMetadata>
    abstract member OpenReadAsync:
        scope: S3ClientScope * bucketName: string * key: string * byteRange: string option * cancellationToken: CancellationToken -> Task<S3ReadHandle>
    abstract member UploadAsync:
        scope: S3ClientScope * bucketName: string * key: string * source: Stream * contentType: string option * ifNoneMatch: bool * cancellationToken: CancellationToken -> Task
    abstract member DeleteManyAsync:
        scope: S3ClientScope * bucketName: string * descriptors: (string * string) list * cancellationToken: CancellationToken -> Task<S3DeleteFailure list>
    abstract member ProbeBucketAsync: scope: S3ClientScope * bucketName: string * cancellationToken: CancellationToken -> Task
    abstract member DownloadToFileAsync:
        scope: S3ClientScope * bucketName: string * key: string * destination: string * cancellationToken: CancellationToken -> Task

module S3KeyValidation =
    let isCanonical (key: string) =
        not (String.IsNullOrWhiteSpace key)
        && Encoding.UTF8.GetByteCount(key) <= 1024
        && not (key.Contains("\u0000", StringComparison.Ordinal))
        && not (key.Contains("\\", StringComparison.Ordinal))
        && not (key.StartsWith("/", StringComparison.Ordinal))
        && not (key.EndsWith("/", StringComparison.Ordinal))
        && (key.Split('/', StringSplitOptions.None)
            |> Array.forall (fun segment -> not (String.IsNullOrEmpty segment) && segment <> "." && segment <> ".."))

    let requireCanonical key =
        if not (isCanonical key) then
            raise (ArgumentException("S3 object key is not canonical.", nameof key))

    let isFolderMarker (key: string) =
        not (String.IsNullOrWhiteSpace key)
        && key.EndsWith("/", StringComparison.Ordinal)
        && isCanonical (key.Substring(0, key.Length - 1))

type S3ObjectEnumerator(client: IAmazonS3) as this =
    let asDateTimeOffset (value: Nullable<DateTime>) =
        if not value.HasValue then None else Some(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) |> DateTimeOffset)

    let listPage bucketName prefix delimiter maxKeys continuationToken cancellationToken : Task<ListObjectsV2Response> =
        let request =
            ListObjectsV2Request(
                BucketName = bucketName,
                Prefix = prefix,
                Delimiter = (delimiter |> Option.defaultValue null),
                MaxKeys = Nullable maxKeys,
                ContinuationToken = (continuationToken |> Option.defaultValue null)
            )
        client.ListObjectsV2Async(request, cancellationToken)

    member _.ListPageAsync(bucketName: string, prefix: string, delimiter: string option, maxKeys: int, continuationToken: string option, cancellationToken: CancellationToken) =
        task {
            ArgumentException.ThrowIfNullOrWhiteSpace bucketName
            if isNull prefix then invalidArg (nameof prefix) "Prefix is required."
            if maxKeys < 1 || maxKeys > 1000 then invalidArg (nameof maxKeys) "maxKeys must be between 1 and 1000."
            cancellationToken.ThrowIfCancellationRequested()
            let! response: ListObjectsV2Response = listPage bucketName prefix delimiter maxKeys continuationToken cancellationToken
            let objects =
                if isNull response.S3Objects then []
                else
                    response.S3Objects
                    |> Seq.map (fun item ->
                        { Key = item.Key
                          SizeBytes = item.Size.GetValueOrDefault()
                          LastModifiedUtc = asDateTimeOffset item.LastModified
                          ETag = if String.IsNullOrWhiteSpace item.ETag then None else Some item.ETag })
                    |> Seq.toList
            let prefixes =
                if isNull response.CommonPrefixes then [] else response.CommonPrefixes |> Seq.toList
            return
                { Objects = objects
                  CommonPrefixes = prefixes
                  NextContinuationToken = if String.IsNullOrWhiteSpace response.NextContinuationToken then None else Some response.NextContinuationToken }
        }
        :> Task<S3ObjectPage>

    interface IS3ObjectStorage with
        member _.ListPageAsync(_, bucketName, prefix, delimiter, maxKeys, continuationToken, cancellationToken) =
            (this :> S3ObjectEnumerator).ListPageAsync(bucketName, prefix, delimiter, maxKeys, continuationToken, cancellationToken)

        member _.GetMetadataAsync(_, bucketName, key, cancellationToken) =
            task {
                ArgumentException.ThrowIfNullOrWhiteSpace bucketName
                if not (S3KeyValidation.isCanonical key || S3KeyValidation.isFolderMarker key) then invalidArg (nameof key) "S3 object key is not canonical."
                let request = GetObjectMetadataRequest(BucketName = bucketName, Key = key)
                let! response = client.GetObjectMetadataAsync(request, cancellationToken)
                return
                    { ContentLength = response.ContentLength
                      ContentType = if String.IsNullOrWhiteSpace response.Headers.ContentType then None else Some response.Headers.ContentType
                      LastModifiedUtc = asDateTimeOffset response.LastModified
                      ETag = if String.IsNullOrWhiteSpace response.ETag then None else Some response.ETag }
            }
            :> Task<S3ObjectMetadata>

        member _.OpenReadAsync(_, bucketName, key, byteRange, cancellationToken) =
            task {
                ArgumentException.ThrowIfNullOrWhiteSpace bucketName
                S3KeyValidation.requireCanonical key
                let request = GetObjectRequest(BucketName = bucketName, Key = key)
                match byteRange with
                | None -> ()
                | Some value ->
                    let separator = value.IndexOf('-')
                    if separator <= 0 || separator = value.Length - 1 then invalidArg (nameof byteRange) "Invalid byte range"
                    let start = Int64.Parse(value.Substring(0, separator), CultureInfo.InvariantCulture)
                    let finish = Int64.Parse(value.Substring(separator + 1), CultureInfo.InvariantCulture)
                    request.ByteRange <- ByteRange(start, finish)
                let! response = client.GetObjectAsync(request, cancellationToken)
                let contentRange = if String.IsNullOrWhiteSpace response.ContentRange then None else Some response.ContentRange
                return S3ReadHandle(response, contentRange)
            }
            :> Task<S3ReadHandle>

        member _.UploadAsync(_, bucketName, key, source, contentType, ifNoneMatch, cancellationToken) =
            task {
                ArgumentException.ThrowIfNullOrWhiteSpace bucketName
                ArgumentNullException.ThrowIfNull source
                S3KeyValidation.requireCanonical key
                let request = PutObjectRequest(BucketName = bucketName, Key = key, InputStream = source, AutoCloseStream = false)
                request.Headers.ContentLength <- source.Length
                ifNoneMatch |> ignore
                request.IfNoneMatch <- if ifNoneMatch then "*" else null
                request.ContentType <- contentType |> Option.defaultValue null
                do! client.PutObjectAsync(request, cancellationToken) :> Task
            }
            :> Task

        member _.DeleteManyAsync(_, bucketName, descriptors, cancellationToken) =
            task {
                ArgumentException.ThrowIfNullOrWhiteSpace bucketName
                if List.isEmpty descriptors then
                    return []
                else
                    let request = DeleteObjectsRequest(BucketName = bucketName)
                    request.Objects <- new List<KeyVersion>()
                    for key, etag in descriptors do
                        if String.IsNullOrWhiteSpace key || key.Contains("\u0000", StringComparison.Ordinal) then
                            invalidArg (nameof key) "S3 delete keys must be non-empty and NUL-free."
                        request.Objects.Add(KeyVersion(Key = key, ETag = etag))
                    let! response = client.DeleteObjectsAsync(request, cancellationToken)
                    return
                        if isNull response.DeleteErrors then []
                        else
                            response.DeleteErrors
                            |> Seq.map (fun failure ->
                                { Key = failure.Key
                                  Code = failure.Code
                                  Message = failure.Message })
                            |> Seq.toList
            }
            :> Task<S3DeleteFailure list>

        member _.ProbeBucketAsync(_, bucketName, cancellationToken) =
            task {
                let! _ = (this :> S3ObjectEnumerator).ListPageAsync(bucketName, "", None, 1, None, cancellationToken)
                return ()
            }
            :> Task

        member _.DownloadToFileAsync(scope, bucketName, key, destination, cancellationToken) =
            task {
                ArgumentException.ThrowIfNullOrWhiteSpace destination
                use! handle = (this :> IS3ObjectStorage).OpenReadAsync(scope, bucketName, key, None, cancellationToken)
                use output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous ||| FileOptions.SequentialScan)
                do! handle.Stream.CopyToAsync(output, 128 * 1024, cancellationToken)
                do! output.FlushAsync(cancellationToken)
            }
            :> Task


type private DeferredS3ObjectStorage(options: StorageOptions) =
    let configuredClient =
        lazy
            let config = AmazonS3Config(ForcePathStyle = options.S3ForcePathStyle)
            config.RequestChecksumCalculation <- RequestChecksumCalculation.WHEN_REQUIRED
            config.ResponseChecksumValidation <- ResponseChecksumValidation.WHEN_REQUIRED
            match options.S3ServiceUrl with
            | Some serviceUrl ->
                config.ServiceURL <- serviceUrl.AbsoluteUri.TrimEnd('/')
                config.AuthenticationRegion <- options.S3Region
            | None -> config.RegionEndpoint <- RegionEndpoint.GetBySystemName(options.S3Region)
            new AmazonS3Client(config) :> IAmazonS3
    let chainClient =
        lazy
            let config = AmazonS3Config()
            config.RequestChecksumCalculation <- RequestChecksumCalculation.WHEN_REQUIRED
            config.ResponseChecksumValidation <- ResponseChecksumValidation.WHEN_REQUIRED
            new AmazonS3Client(config) :> IAmazonS3
    let configuredImplementation = lazy (new S3ObjectEnumerator(configuredClient.Value) :> IS3ObjectStorage)
    let chainImplementation = lazy (new S3ObjectEnumerator(chainClient.Value) :> IS3ObjectStorage)
    let implementation scope =
        match scope with
        | S3ClientScope.ConfiguredDefault -> configuredImplementation.Value
        | S3ClientScope.AwsDefaultChain -> chainImplementation.Value

    interface IS3ObjectStorage with
        member _.ListPageAsync(scope, bucket, prefix, delimiter, maxKeys, token, ct) = (implementation scope).ListPageAsync(scope, bucket, prefix, delimiter, maxKeys, token, ct)
        member _.GetMetadataAsync(scope, bucket, key, ct) = (implementation scope).GetMetadataAsync(scope, bucket, key, ct)
        member _.OpenReadAsync(scope, bucket, key, range, ct) = (implementation scope).OpenReadAsync(scope, bucket, key, range, ct)
        member _.UploadAsync(scope, bucket, key, source, contentType, ifNoneMatch, ct) = (implementation scope).UploadAsync(scope, bucket, key, source, contentType, ifNoneMatch, ct)
        member _.DeleteManyAsync(scope, bucket, descriptors, ct) = (implementation scope).DeleteManyAsync(scope, bucket, descriptors, ct)
        member _.ProbeBucketAsync(scope, bucket, ct) = (implementation scope).ProbeBucketAsync(scope, bucket, ct)
        member _.DownloadToFileAsync(scope, bucket, key, destination, ct) = (implementation scope).DownloadToFileAsync(scope, bucket, key, destination, ct)

    interface IDisposable with
        member _.Dispose() =
            if configuredClient.IsValueCreated then configuredClient.Value.Dispose()
            if chainClient.IsValueCreated then chainClient.Value.Dispose()

[<RequireQualifiedAccess>]
module S3StorageComposition =
    let addS3ObjectStorage (options: StorageOptions) (services: IServiceCollection) =
        services.AddSingleton<IS3ObjectStorage>(fun _ -> new DeferredS3ObjectStorage(options) :> IS3ObjectStorage)
