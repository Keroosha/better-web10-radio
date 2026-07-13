namespace Web10.Radio.API

open System
open System.Collections.Concurrent
open System.Globalization
open System.IO
open System.Net
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.StaticFiles
open Npgsql
open Web10.Radio.Application
open Web10.Radio.Database
open Web10.Radio.Database.Repositories

[<RequireQualifiedAccess>]
type StorageBackendKey =
    | Default
    | Additional of Guid

[<RequireQualifiedAccess>]
type StorageContentError =
    | RequestInvalid
    | BackendNotFound
    | FileNotFound
    | FileExists
    | UploadTooLarge
    | RangeNotSatisfiable
    | ReadFailed
    | UploadFailed
    | DeleteFailed
    | ImpactChanged
    | RepositoryFailed

type StorageContentBackend =
    { Key: StorageBackendKey
      Id: Guid option
      Type: StorageType
      LocalRoot: string option
      S3Bucket: string option
      Scope: S3ClientScope }

type StorageContentEntry =
    { Path: string
      Name: string
      Kind: string
      SizeBytes: int64 option
      LastModifiedUtc: DateTimeOffset option
      ContentType: string option
      ETag: string option }

type StorageContentPage =
    { Path: string
      Items: StorageContentEntry list
      NextCursor: string option }

type StoragePhysicalDescriptor =
    { Path: string
      Kind: StorageSelectionKind
      SizeBytes: int64
      LastModifiedUtc: DateTimeOffset option
      ETag: string option }

type StorageDeleteReport =
    { Impact: StorageImpactRecord
      Descriptors: StoragePhysicalDescriptor list
      ImpactToken: string }

type StorageReadHandle(stream: Stream, contentLength: int64, contentType: string option, lastModifiedUtc: DateTimeOffset option, etag: string option, contentRange: string option, dispose: unit -> unit) =
    member _.Stream = stream
    member _.ContentLength = contentLength
    member _.ContentType = contentType
    member _.LastModifiedUtc = lastModifiedUtc
    member _.ETag = etag
    member _.ContentRange = contentRange
    interface IDisposable with member _.Dispose() = dispose()

[<RequireQualifiedAccess>]
module StoragePath =
    let canonical (path: string) =
        if isNull path then Error ()
        elif path = "" then Ok ""
        elif Encoding.UTF8.GetByteCount(path) > 1024 then Error ()
        elif path.StartsWith("/", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal) then Error ()
        elif path.Contains("\\", StringComparison.Ordinal) || path.Contains("\u0000", StringComparison.Ordinal) then Error ()
        elif path.Split('/', StringSplitOptions.None) |> Array.exists (fun part -> String.IsNullOrEmpty part || part = "." || part = "..") then Error ()
        else Ok path

    let nonRoot path = canonical path |> Result.bind (fun value -> if value = "" then Error () else Ok value)

    let localAbsolute root relative =
        match canonical relative with
        | Error () -> Error ()
        | Ok relative ->
            try
                let root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                let full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))
                let boundary = root + string Path.DirectorySeparatorChar
                if full <> root && not (full.StartsWith(boundary, StringComparison.Ordinal)) then Error ()
                else
                    let mutable current = root
                    for segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries) do
                        current <- Path.Combine(current, segment)
                        if Directory.Exists current && (File.GetAttributes(current) &&& FileAttributes.ReparsePoint) = FileAttributes.ReparsePoint then raise (UnauthorizedAccessException())
                    Ok full
            with _ -> Error ()

    let wireFromLocal root full =
        let root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        let full = Path.GetFullPath(full)
        if full = root then "" else full.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/')

[<Sealed>]
type private CoordinatorLease(semaphore: SemaphoreSlim) =
    interface IAsyncDisposable with
        member _.DisposeAsync() = semaphore.Release() |> ignore; ValueTask()

[<Sealed>]
type StorageOperationCoordinator() =
    let semaphores = ConcurrentDictionary<StorageBackendKey, SemaphoreSlim>()
    member _.AcquireAsync(key: StorageBackendKey, cancellationToken: CancellationToken) : ValueTask<IAsyncDisposable> =
        let semaphore = semaphores.GetOrAdd(key, fun _ -> new SemaphoreSlim(1, 1))
        let waitTask = task {
            do! semaphore.WaitAsync(cancellationToken)
            return (CoordinatorLease(semaphore) :> IAsyncDisposable)
        }
        ValueTask<IAsyncDisposable>(waitTask)

[<Sealed>]
type private LimitedStream(inner: FileStream, start: int64, length: int64) =
    inherit Stream()
    let mutable remaining = length
    do inner.Seek(start, SeekOrigin.Begin) |> ignore
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = length
    override _.Position with get() = length - remaining and set _ = raise (NotSupportedException())
    override _.Flush() = ()
    override _.FlushAsync(_) = Task.CompletedTask
    override _.Read(buffer, offset, count) =
        let requested = int (min remaining (int64 count))
        if requested = 0 then 0
        else
            let read = inner.Read(buffer, offset, requested)
            remaining <- remaining - int64 read
            read
    override _.ReadAsync(buffer, offset, count, cancellationToken) =
        task {
            let requested = int (min remaining (int64 count))
            if requested = 0 then return 0
            else
                let! read = inner.ReadAsync(buffer, offset, requested, cancellationToken)
                remaining <- remaining - int64 read
                return read
        }
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength(_) = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())
    override _.Dispose(disposing) = if disposing then inner.Dispose(); base.Dispose(disposing)

[<Sealed>]
type private CountingStream(inner: Stream, maximum: int64, declaredLength: int64 option) =
    inherit Stream()
    let mutable count = 0L
    member _.Count = count
    member private _.Check(value: int) = count <- count + int64 value; if count > maximum then raise (InvalidDataException())
    override _.CanRead = inner.CanRead
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = declaredLength |> Option.defaultWith (fun () -> inner.Length)
    override _.Position with get() = inner.Position and set _ = raise (NotSupportedException())
    override _.Flush() = ()
    override _.FlushAsync(_) = Task.CompletedTask
    override this.Read(buffer, offset, count) =
        let read = inner.Read(buffer, offset, count)
        this.Check(read)
        read
    override this.ReadAsync(buffer, offset, count, cancellationToken) =
        task {
            let! read = inner.ReadAsync(buffer, offset, count, cancellationToken)
            this.Check(read)
            return read
        }
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength(_) = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())
    override _.Dispose(disposing) = if disposing then inner.Dispose(); base.Dispose(disposing)

[<Sealed>]
type StorageContentService(options: StorageOptions, dataSource: NpgsqlDataSource, s3: IS3ObjectStorage, clock: TimeProvider, coordinator: StorageOperationCoordinator) =
    let contentProvider = FileExtensionContentTypeProvider()
    let contentType path = match contentProvider.TryGetContentType(path) with | true, value -> Some value | _ -> Some "application/octet-stream"
    let errorMap _ = StorageContentError.RepositoryFailed

    let resolveBackend backendId cancellationToken =
        task {
            match backendId with
            | None ->
                let value : StorageContentBackend =
                    { Key = StorageBackendKey.Default
                      Id = None
                      Type = options.Type
                      LocalRoot = (if options.Type = Local then Some options.LocalRoot else None)
                      S3Bucket = (if options.Type = S3 then Some options.S3Bucket else None)
                      Scope = S3ClientScope.ConfiguredDefault }
                return Ok value
            | Some id ->
                let! result = LibraryScanRepository.getStorageBackendForManagement dataSource id cancellationToken
                match result with
                | Error _ -> return Error StorageContentError.RepositoryFailed
                | Ok None -> return Error StorageContentError.BackendNotFound
                | Ok(Some record) ->
                    let typ = if String.Equals(record.Type, "Local", StringComparison.OrdinalIgnoreCase) then Local else S3
                    return Ok { Key = StorageBackendKey.Additional id; Id = Some id; Type = typ; LocalRoot = record.LocalRoot; S3Bucket = record.S3Bucket; Scope = S3ClientScope.AwsDefaultChain }
        }

    let parseLocalCursor (cursor: string option) : Result<int option, unit> =
        match cursor with
        | None -> Ok None
        | Some value when value.Length > 2048 -> Error ()
        | Some value ->
            try
                let padded = value.Replace('-', '+').Replace('_', '/') + String.replicate ((4 - value.Length % 4) % 4) "="
                Ok(Some(Int32.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded)), CultureInfo.InvariantCulture)))
            with _ -> Error ()

    let makeCursor index = Convert.ToBase64String(Encoding.UTF8.GetBytes(string index)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    let parseRange (length: int64) (value: string) : Result<string, StorageContentError> =
        if not (value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) then Error StorageContentError.RangeNotSatisfiable
        else
            let spec = value.Substring(6)
            if spec.Contains(',', StringComparison.Ordinal) then Error StorageContentError.RangeNotSatisfiable
            else
                let parts = spec.Split('-', 2)
                if parts.Length <> 2 then Error StorageContentError.RangeNotSatisfiable
                else
                    try
                        let first, last =
                            if String.IsNullOrWhiteSpace parts[0] then
                                let suffix = Int64.Parse(parts[1], CultureInfo.InvariantCulture)
                                if suffix <= 0L then raise (FormatException())
                                max 0L (length - suffix), length - 1L
                            else
                                let start = Int64.Parse(parts[0], CultureInfo.InvariantCulture)
                                let finish = if String.IsNullOrWhiteSpace parts[1] then length - 1L else Int64.Parse(parts[1], CultureInfo.InvariantCulture)
                                start, min finish (length - 1L)
                        if first < 0L || first >= length || last < first then Error StorageContentError.RangeNotSatisfiable
                        else Ok(sprintf "%d-%d" first last)
                    with _ -> Error StorageContentError.RangeNotSatisfiable

    member private _.ListLocal(root, path, limit, cursor) =
        match StoragePath.localAbsolute root path with
        | Error () -> Error StorageContentError.RequestInvalid
        | Ok full when not (Directory.Exists full) -> Error StorageContentError.FileNotFound
        | Ok full ->
            try
                let folders =
                    Directory.EnumerateDirectories(full)
                    |> Seq.choose (fun candidate -> if (File.GetAttributes(candidate) &&& FileAttributes.ReparsePoint) = FileAttributes.ReparsePoint then None else Some candidate)
                    |> Seq.map (fun candidate -> { Path = StoragePath.wireFromLocal root candidate; Name = Path.GetFileName(candidate); Kind = "folder"; SizeBytes = None; LastModifiedUtc = Some(DateTimeOffset(Directory.GetLastWriteTimeUtc(candidate))); ContentType = None; ETag = None } : StorageContentEntry)
                let files =
                    Directory.EnumerateFiles(full)
                    |> Seq.choose (fun candidate -> if (File.GetAttributes(candidate) &&& FileAttributes.ReparsePoint) = FileAttributes.ReparsePoint then None else Some candidate)
                    |> Seq.map (fun candidate -> let info = FileInfo(candidate) in { Path = StoragePath.wireFromLocal root candidate; Name = info.Name; Kind = "file"; SizeBytes = Some info.Length; LastModifiedUtc = Some(DateTimeOffset(info.LastWriteTimeUtc)); ContentType = contentType info.Name; ETag = None } : StorageContentEntry)
                let all = Seq.append folders files |> Seq.sortBy (fun value -> (if value.Kind = "folder" then 0 else 1), value.Name) |> Seq.toList
                let start = cursor |> Option.defaultValue 0
                let page = all |> List.skip (min start all.Length) |> List.truncate (limit + 1)
                let pageResult : StorageContentPage = { Path = path; Items = page |> List.truncate limit; NextCursor = if page.Length > limit then Some(makeCursor (start + limit)) else None }
                Ok pageResult
            with _ -> Error StorageContentError.ReadFailed

    member this.ListAsync(backendId: Guid option, path: string, limit: int, cursor: string option, cancellationToken: CancellationToken) : Task<Result<StorageContentPage, StorageContentError>> =
        task {
            if limit < 1 || limit > 200 then
                return Error StorageContentError.RequestInvalid
            else
                match StoragePath.canonical path with
                | Error () -> return Error StorageContentError.RequestInvalid
                | Ok path ->
                    let! resolved = resolveBackend backendId cancellationToken
                    match resolved with
                    | Error error -> return Error error
                    | Ok backend ->
                        match backend.Type with
                        | Local ->
                            match parseLocalCursor cursor with
                            | Error () -> return Error StorageContentError.RequestInvalid
                            | Ok localCursor -> return this.ListLocal(backend.LocalRoot.Value, path, limit, localCursor)
                        | S3 ->
                            if cursor |> Option.exists (fun value -> value.Length > 2048) then
                                return Error StorageContentError.RequestInvalid
                            else
                                try
                                    let prefix = if path = "" then "" else path + "/"
                                    let! page = s3.ListPageAsync(backend.Scope, backend.S3Bucket.Value, prefix, Some "/", limit + 1, cursor, cancellationToken)
                                    let rows = ResizeArray<StorageContentEntry>()
                                    for item in page.Objects do
                                        if item.Key <> prefix then
                                            let relative = item.Key.Substring(prefix.Length)
                                            if S3KeyValidation.isFolderMarker item.Key && relative.EndsWith("/", StringComparison.Ordinal) && not (relative.Substring(0, relative.Length - 1).Contains("/", StringComparison.Ordinal)) then
                                                let folder = item.Key.TrimEnd('/')
                                                rows.Add({ Path = folder; Name = Path.GetFileName(folder); Kind = "folder"; SizeBytes = None; LastModifiedUtc = item.LastModifiedUtc; ContentType = None; ETag = item.ETag })
                                            elif not (relative.Contains("/", StringComparison.Ordinal)) then
                                                rows.Add({ Path = item.Key; Name = relative; Kind = "file"; SizeBytes = Some item.SizeBytes; LastModifiedUtc = item.LastModifiedUtc; ContentType = contentType relative; ETag = item.ETag })
                                    for common in page.CommonPrefixes do
                                        let folder = common.TrimEnd('/')
                                        rows.Add({ Path = folder; Name = Path.GetFileName(folder); Kind = "folder"; SizeBytes = None; LastModifiedUtc = None; ContentType = None; ETag = None })
                                    let items = rows |> Seq.distinctBy (fun value -> value.Path, value.Kind) |> Seq.sortBy (fun value -> (if value.Kind = "folder" then 0 else 1), value.Name) |> Seq.toList
                                    return Ok { Path = path; Items = items |> List.truncate limit; NextCursor = page.NextContinuationToken }
                                with _ -> return Error StorageContentError.ReadFailed
        }

    member _.OpenReadAsync(backendId: Guid option, path: string, byteRange: string option, cancellationToken: CancellationToken) : Task<Result<StorageReadHandle, StorageContentError>> =
        task {
            match StoragePath.nonRoot path with
            | Error () -> return Error StorageContentError.RequestInvalid
            | Ok path ->
                let! resolved = resolveBackend backendId cancellationToken
                match resolved with
                | Error error -> return Error error
                | Ok backend ->
                    match backend.Type with
                    | Local ->
                        match StoragePath.localAbsolute backend.LocalRoot.Value path with
                        | Error () -> return Error StorageContentError.RequestInvalid
                        | Ok full when not (File.Exists full) -> return Error StorageContentError.FileNotFound
                        | Ok full ->
                            try
                                let info = FileInfo(full)
                                let range =
                                    match byteRange with
                                    | None -> Ok None
                                    | Some value ->
                                        let value = if value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) then value.Substring(6) else "!"
                                        if value.Contains(',', StringComparison.Ordinal) then Error ()
                                        else
                                            let parts = value.Split('-', 2)
                                            try
                                                let first = Int64.Parse(parts[0], CultureInfo.InvariantCulture)
                                                let last = if parts.Length = 1 || parts[1] = "" then info.Length - 1L else Int64.Parse(parts[1], CultureInfo.InvariantCulture)
                                                if first < 0L || first >= info.Length || last < first then Error () else Ok(Some(first, min last (info.Length - 1L)))
                                            with _ -> Error ()
                                match range with
                                | Error () -> return Error StorageContentError.RangeNotSatisfiable
                                | Ok None ->
                                    let stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous ||| FileOptions.SequentialScan)
                                    return Ok(StorageReadHandle(stream, info.Length, contentType info.Name, Some(DateTimeOffset(info.LastWriteTimeUtc)), None, None, fun () -> stream.Dispose()))
                                | Ok(Some(first, last)) ->
                                    let stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous ||| FileOptions.SequentialScan)
                                    let limited = new LimitedStream(stream, first, last - first + 1L)
                                    return Ok(StorageReadHandle(limited, last - first + 1L, contentType info.Name, Some(DateTimeOffset(info.LastWriteTimeUtc)), None, Some(sprintf "bytes %d-%d/%d" first last info.Length), fun () -> limited.Dispose()))
                            with _ -> return Error StorageContentError.ReadFailed
                    | S3 ->
                        try
                            let! metadata = s3.GetMetadataAsync(backend.Scope, backend.S3Bucket.Value, path, cancellationToken)
                            let normalized =
                                match byteRange with
                                | None -> Ok None
                                | Some value -> parseRange metadata.ContentLength value |> Result.map Some
                            match normalized with
                            | Error error -> return Error error
                            | Ok range ->
                                let! handle = s3.OpenReadAsync(backend.Scope, backend.S3Bucket.Value, path, range, cancellationToken)
                                return Ok(StorageReadHandle(handle.Stream, handle.ContentLength, handle.ContentType |> Option.orElse (contentType path), handle.LastModifiedUtc, handle.ETag, handle.ContentRange, fun () -> (handle :> IDisposable).Dispose()))
                        with
                        | :? Amazon.S3.AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.NotFound -> return Error StorageContentError.FileNotFound
                        | :? Amazon.S3.AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable -> return Error StorageContentError.RangeNotSatisfiable
                        | :? ArgumentException -> return Error StorageContentError.RangeNotSatisfiable
                        | _ -> return Error StorageContentError.ReadFailed
        }

    member _.UploadAsync(backendId: Guid option, path: string, source: Stream, contentTypeValue: string option, declaredLength: int64 option, cancellationToken: CancellationToken) : Task<Result<StorageContentEntry, StorageContentError>> =
        task {
            match StoragePath.nonRoot path with
            | Error () -> return Error StorageContentError.RequestInvalid
            | Ok path ->
                let! resolved = resolveBackend backendId cancellationToken
                match resolved with
                | Error error -> return Error error
                | Ok backend ->
                    let! lease = backend.Key |> fun key -> coordinator.AcquireAsync(key, cancellationToken)
                    use lease = lease
                    match backend.Type with
                    | Local ->
                        match StoragePath.localAbsolute backend.LocalRoot.Value path with
                        | Error () -> return Error StorageContentError.RequestInvalid
                        | Ok full ->
                            let parent = Path.GetDirectoryName(full)
                            Directory.CreateDirectory(parent) |> ignore
                            let temp = Path.Combine(parent, ".web10-" + Guid.NewGuid().ToString("N") + ".tmp")
                            let cleanup () = if File.Exists(temp) then File.Delete(temp)
                            try
                                use output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous ||| FileOptions.SequentialScan)
                                let buffer = Array.zeroCreate<byte> (128 * 1024)
                                let rec copy total =
                                    task {
                                        let! read = source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                                        if read = 0 then return total
                                        elif total + int64 read > options.MaxUploadBytes then return -1L
                                        else
                                            do! output.WriteAsync(buffer, 0, read, cancellationToken)
                                            return! copy (total + int64 read)
                                    }
                                let! total = copy 0L
                                if total < 0L then
                                    cleanup ()
                                    return Error StorageContentError.UploadTooLarge
                                else
                                    do! output.FlushAsync(cancellationToken)
                                    if File.Exists(full) then
                                        cleanup ()
                                        return Error StorageContentError.FileExists
                                    else
                                        let moved =
                                            try
                                                File.Move(temp, full, false)
                                                true
                                            with :? IOException -> false
                                        if not moved then
                                            return Error StorageContentError.FileExists
                                        else
                                            let info = FileInfo(full)
                                            return Ok { Path = path; Name = info.Name; Kind = "file"; SizeBytes = Some info.Length; LastModifiedUtc = Some(DateTimeOffset(info.LastWriteTimeUtc)); ContentType = contentTypeValue |> Option.orElse (contentType path); ETag = None }
                            with
                            | :? InvalidDataException ->
                                cleanup ()
                                return Error StorageContentError.UploadTooLarge
                            | :? OperationCanceledException ->
                                cleanup ()
                                return raise (OperationCanceledException())
                            | _ ->
                                cleanup ()
                                return Error StorageContentError.UploadFailed
                    | S3 ->
                        try
                            use counted = new CountingStream(source, options.MaxUploadBytes, declaredLength)
                            do! s3.UploadAsync(backend.Scope, backend.S3Bucket.Value, path, counted, contentTypeValue |> Option.orElse (contentType path), true, cancellationToken)
                            let! metadata = s3.GetMetadataAsync(backend.Scope, backend.S3Bucket.Value, path, cancellationToken)
                            return Ok { Path = path; Name = Path.GetFileName(path); Kind = "file"; SizeBytes = Some metadata.ContentLength; LastModifiedUtc = metadata.LastModifiedUtc; ContentType = metadata.ContentType |> Option.orElse (contentType path); ETag = metadata.ETag }
                        with
                        | :? Amazon.S3.AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.PreconditionFailed -> return Error StorageContentError.FileExists
                        | :? InvalidDataException -> return Error StorageContentError.UploadTooLarge
                        | _ -> return Error StorageContentError.UploadFailed
        }

    member private _.EnumerateDescriptors(backend: StorageContentBackend, selections: StorageSelection list, cancellationToken: CancellationToken) : Task<StoragePhysicalDescriptor list> =
        task {
            match backend.Type with
            | Local ->
                let result = ResizeArray<StoragePhysicalDescriptor>()
                let rec visit full =
                    task {
                        if File.Exists full then
                            let info = FileInfo(full)
                            result.Add({ Path = StoragePath.wireFromLocal backend.LocalRoot.Value full; Kind = StorageSelectionKind.File; SizeBytes = info.Length; LastModifiedUtc = Some(DateTimeOffset(info.LastWriteTimeUtc)); ETag = None })
                        elif Directory.Exists full then
                            if full <> backend.LocalRoot.Value then
                                let info = DirectoryInfo(full)
                                result.Add({ Path = StoragePath.wireFromLocal backend.LocalRoot.Value full; Kind = StorageSelectionKind.Folder; SizeBytes = 0L; LastModifiedUtc = Some(DateTimeOffset(info.LastWriteTimeUtc)); ETag = None })
                            for child in Directory.EnumerateFileSystemEntries(full) do
                                if (File.GetAttributes(child) &&& FileAttributes.ReparsePoint) = FileAttributes.Normal then
                                    do! visit child
                    }
                for selection in selections do
                    match StoragePath.localAbsolute backend.LocalRoot.Value selection.PhysicalPath with
                    | Ok path -> do! visit path
                    | Error () -> ()
                return result |> Seq.distinctBy (fun value -> value.Path, value.Kind) |> Seq.toList
            | S3 ->
                let result = ResizeArray<StoragePhysicalDescriptor>()
                let rec listPrefix prefix token =
                    task {
                        let! page = s3.ListPageAsync(backend.Scope, backend.S3Bucket.Value, prefix, None, 1000, token, cancellationToken)
                        for item in page.Objects do
                            let kind = if S3KeyValidation.isFolderMarker item.Key then StorageSelectionKind.Folder else StorageSelectionKind.File
                            let descriptor : StoragePhysicalDescriptor = { Path = item.Key; Kind = kind; SizeBytes = item.SizeBytes; LastModifiedUtc = item.LastModifiedUtc; ETag = item.ETag }
                            result.Add(descriptor)
                        match page.NextContinuationToken with
                        | Some next -> return! listPrefix prefix (Some next)
                        | None -> return ()
                    }
                for selection in selections do
                    match selection.Kind with
                    | StorageSelectionKind.File ->
                        try
                            let! metadata = s3.GetMetadataAsync(backend.Scope, backend.S3Bucket.Value, selection.PhysicalPath, cancellationToken)
                            result.Add({ Path = selection.PhysicalPath; Kind = StorageSelectionKind.File; SizeBytes = metadata.ContentLength; LastModifiedUtc = metadata.LastModifiedUtc; ETag = metadata.ETag })
                        with _ -> ()
                    | StorageSelectionKind.Folder ->
                        let markerPath = selection.PhysicalPath + "/"
                        try
                            let! metadata = s3.GetMetadataAsync(backend.Scope, backend.S3Bucket.Value, markerPath, cancellationToken)
                            result.Add({ Path = markerPath; Kind = StorageSelectionKind.Folder; SizeBytes = metadata.ContentLength; LastModifiedUtc = metadata.LastModifiedUtc; ETag = metadata.ETag })
                        with _ -> ()
                        do! listPrefix markerPath None
                return result |> Seq.distinctBy (fun value -> value.Path, value.Kind) |> Seq.toList
        }

    member private _.ComputeToken(backend: StorageContentBackend, selections: StorageSelection list, descriptors: StoragePhysicalDescriptor list, impact: StorageImpactRecord) =
        use sha = SHA256.Create()
        let text = StringBuilder()
        text.Append(match backend.Key with | StorageBackendKey.Default -> "default" | StorageBackendKey.Additional id -> id.ToString("D")) |> ignore
        for selection in selections |> List.sortBy (fun value -> value.PhysicalPath) do text.Append("|S|").Append(selection.PhysicalPath).Append(selection.Kind.ToString()) |> ignore
        for descriptor in descriptors |> List.sortBy _.Path do text.Append("|D|").Append(descriptor.Path).Append(descriptor.Kind.ToString()).Append(descriptor.SizeBytes).Append(descriptor.ETag |> Option.defaultValue "") |> ignore
        for file in impact.TrackFiles |> List.sortBy _.TrackFileId do text.Append("|F|").Append(file.TrackFileId).Append(file.UpdatedAtUtc.ToString("O")) |> ignore
        for track in impact.TracksToDelete |> List.sortBy _.TrackId do text.Append("|T|").Append(track.TrackId) |> ignore
        sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    member this.PreviewDeleteAsync(backendId: Guid option, selections: StorageSelection list, cancellationToken: CancellationToken) : Task<Result<StorageDeleteReport, StorageContentError>> =
        task {
            let! resolved = resolveBackend backendId cancellationToken
            match resolved with
            | Error error -> return Error error
            | Ok backend ->
                let! descriptors = this.EnumerateDescriptors(backend, selections, cancellationToken)
                let! impactResult = DatabaseSession.withTransactionResult dataSource (fun connection transaction token -> StorageContentRepository.loadImpactInTransaction connection transaction backend.Id selections token) cancellationToken
                match impactResult with
                | Error _ -> return Error StorageContentError.RepositoryFailed
                | Ok impact -> return Ok { Impact = impact; Descriptors = descriptors; ImpactToken = this.ComputeToken(backend, selections, descriptors, impact) }
        }

    member this.DeleteAsync(backendId: Guid option, selections: StorageSelection list, expectedToken: string, cancellationToken: CancellationToken) : Task<Result<StorageDeleteReport * StorageDeleteMutation, StorageContentError>> =
        task {
            let! resolved = resolveBackend backendId cancellationToken
            match resolved with
            | Error error -> return Error error
            | Ok backend ->
                let! lease = coordinator.AcquireAsync(backend.Key, cancellationToken)
                use lease = lease
                let! descriptors = this.EnumerateDescriptors(backend, selections, cancellationToken)
                let! result =
                    DatabaseSession.withTransactionResult
                        dataSource
                        (fun connection transaction token ->
                            task {
                                let! impactResult = StorageContentRepository.loadImpactInTransaction connection transaction backend.Id selections token
                                match impactResult with
                                | Error error -> return Error error
                                | Ok impact ->
                                    let tokenValue = this.ComputeToken(backend, selections, descriptors, impact)
                                    if tokenValue <> expectedToken then
                                        return Error(RepositoryError.DatabaseError("StorageContentRepository.impact", "impact changed"))
                                    else
                                        let append connection transaction command token =
                                            let payload = System.Text.Json.JsonSerializer.Serialize({| queueItemId = command.Fence.QueueItemId.ToString("D"); claimOwner = command.Fence.ClaimOwner.ToString("D"); claimAttempt = command.Fence.ClaimAttempt; commandGeneration = command.Generation |}, DomainJson.options)
                                            match DomainEventEnvelope.create clock DomainEventType.PlaybackSkipped "Web10.Radio.API.Admin" None None payload with
                                            | Error _ -> Task.FromResult(Error(RepositoryError.DatabaseError("StorageContentRepository.delete", "invalid event")))
                                            | Ok envelope -> OutboxEventRepository.appendInTransaction connection transaction (OutboxMapping.toOutboxEvent envelope) token
                                        let! mutation = StorageContentRepository.applyDeletionInTransaction connection transaction impact (clock.GetUtcNow()) PlaybackQueueRepository.skipCurrentTrackInTransaction append token
                                        return mutation |> Result.map (fun value -> impact, value)
                            })
                        cancellationToken
                match result with
                | Error(DatabaseError(_, message)) when message = "impact changed" -> return Error StorageContentError.ImpactChanged
                | Error _ -> return Error StorageContentError.RepositoryFailed
                | Ok(impact, mutation) ->
                        match backend.Type with
                        | Local ->
                            for descriptor in descriptors |> List.sortByDescending _.Path.Length do
                                match StoragePath.localAbsolute backend.LocalRoot.Value descriptor.Path with
                                | Ok path when descriptor.Kind = StorageSelectionKind.File && File.Exists path -> File.Delete path
                                | Ok path when descriptor.Kind = StorageSelectionKind.Folder && Directory.Exists path ->
                                    try Directory.Delete(path, false) with _ -> ()
                                | _ -> ()
                            return Ok({ Impact = impact; Descriptors = descriptors; ImpactToken = expectedToken }, mutation)
                        | S3 ->
                            let deleteDescriptors values =
                                let rec deleteBatches batches =
                                    task {
                                        match batches with
                                        | [] -> return true
                                        | batch :: rest ->
                                            let! failures = s3.DeleteManyAsync(backend.Scope, backend.S3Bucket.Value, batch |> List.map (fun value -> value.Path, value.ETag |> Option.defaultValue ""), cancellationToken)
                                            if not failures.IsEmpty then return false
                                            else return! deleteBatches rest
                                    }
                                deleteBatches (values |> List.chunkBySize 1000)
                            let fileDescriptors = descriptors |> List.filter (fun value -> value.Kind <> StorageSelectionKind.Folder)
                            let markerDescriptors = descriptors |> List.filter (fun value -> value.Kind = StorageSelectionKind.Folder)
                            let! filesDeleted = deleteDescriptors fileDescriptors
                            let! markersDeleted = if filesDeleted then deleteDescriptors markerDescriptors else Task.FromResult false
                            let deleted = filesDeleted && markersDeleted
                            if not deleted then return Error StorageContentError.DeleteFailed
                            else return Ok({ Impact = impact; Descriptors = descriptors; ImpactToken = expectedToken }, mutation)
        }
