namespace Web10.Radio.Telegram

open System
open System.Reflection
open System.Runtime.Serialization
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.FSharp.Reflection
open Funogram.Telegram.Bot
open Funogram.Telegram.Types

type TelegramAdapterSnapshot =
    { IsConfigured: bool
      ChannelIdOrUsername: string
      LastUpdateId: int64 option
      LastError: string option }

type ITelegramAdapterState =
    abstract member Snapshot: unit -> TelegramAdapterSnapshot
    abstract member RecordUpdate: updateId: int64 -> unit
    abstract member RecordError: message: string -> unit

type TelegramAdapterState(options: TelegramOptions) =
    let gate = obj ()
    let mutable lastUpdateId: int64 option = None
    let mutable lastError: string option = None

    interface ITelegramAdapterState with
        member _.Snapshot() =
            lock gate (fun () ->
                { IsConfigured = true
                  ChannelIdOrUsername = options.ChannelIdOrUsername
                  LastUpdateId = lastUpdateId
                  LastError = lastError })

        member _.RecordUpdate(updateId) =
            lock gate (fun () ->
                match lastUpdateId with
                | Some current when current > updateId -> ()
                | Some current when current = updateId -> lastError <- None
                | _ ->
                    lastUpdateId <- Some updateId
                    lastError <- None)

        member _.RecordError(message) =
            let normalized =
                if String.IsNullOrWhiteSpace message then
                    "Telegram update processing failed."
                else
                    message

            lock gate (fun () -> lastError <- Some normalized)

type private UnixTimestampDateTimeConverter() =
    inherit JsonConverter<DateTime>()

    override _.Read(reader: byref<Utf8JsonReader>, _, _) =
        DateTime.UnixEpoch.AddSeconds(float (reader.GetInt64()))

    override _.Write(writer, value, _) =
        writer.WriteNumberValue(DateTimeOffset(value.ToUniversalTime()).ToUnixTimeSeconds())

[<RequireQualifiedAccess>]
module private TelegramUnionJson =
    let private dataMemberName (attributes: obj array) =
        attributes
        |> Array.tryPick (function
            | :? DataMemberAttribute as attribute when not (String.IsNullOrWhiteSpace attribute.Name) -> Some attribute.Name
            | _ -> None)

    let caseName (caseInfo: UnionCaseInfo) =
        caseInfo.GetCustomAttributes()
        |> dataMemberName
        |> Option.defaultWith (fun () -> JsonNamingPolicy.SnakeCaseLower.ConvertName(caseInfo.Name))

    let propertyName (propertyInfo: PropertyInfo) =
        propertyInfo.GetCustomAttributes(typeof<DataMemberAttribute>, true)
        |> dataMemberName
        |> Option.defaultWith (fun () -> JsonNamingPolicy.SnakeCaseLower.ConvertName(propertyInfo.Name))

    let isArrayLike (valueType: Type) =
        valueType.IsArray
        || (valueType.IsGenericType && valueType.GetGenericTypeDefinition() = typedefof<list<_>>)

    let isScalarToken tokenType =
        match tokenType with
        | JsonValueKind.String
        | JsonValueKind.Number
        | JsonValueKind.True
        | JsonValueKind.False -> true
        | _ -> false

type private TelegramUnionJsonConverter<'T>() =
    inherit JsonConverter<'T>()

    let unionType = typeof<'T>
    let cases = FSharpType.GetUnionCases(unionType, true)

    let chooseObjectCase (element: JsonElement) =
        let incomingNames =
            element.EnumerateObject()
            |> Seq.map (fun property -> property.Name)
            |> Set.ofSeq

        cases
        |> Array.choose (fun caseInfo ->
            let fields = caseInfo.GetFields()

            if fields.Length <> 1 then
                None
            else
                let knownNames =
                    fields[0].PropertyType.GetProperties(BindingFlags.Instance ||| BindingFlags.Public)
                    |> Array.map TelegramUnionJson.propertyName
                    |> Set.ofArray

                let score = incomingNames |> Seq.sumBy (fun name -> if knownNames.Contains name then 1 else 0)
                let hasOnlyKnownProperties = incomingNames |> Set.forall knownNames.Contains
                Some(caseInfo, hasOnlyKnownProperties, score))
        |> Array.sortByDescending (fun (_, exact, score) -> exact, score)
        |> Array.tryHead
        |> Option.map (fun (caseInfo, _, _) -> caseInfo)

    let chooseCase (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.String ->
            let value = element.GetString()

            cases
            |> Array.tryFind (fun caseInfo ->
                let fields = caseInfo.GetFields()
                (fields.Length = 0 && String.Equals(TelegramUnionJson.caseName caseInfo, value, StringComparison.OrdinalIgnoreCase))
                || (fields.Length = 1 && fields[0].PropertyType = typeof<string>))
        | JsonValueKind.Array ->
            cases
            |> Array.tryFind (fun caseInfo ->
                let fields = caseInfo.GetFields()
                fields.Length = 1 && TelegramUnionJson.isArrayLike fields[0].PropertyType)
        | JsonValueKind.Object -> chooseObjectCase element
        | tokenType when TelegramUnionJson.isScalarToken tokenType ->
            cases |> Array.tryFind (fun caseInfo -> caseInfo.GetFields().Length = 1)
        | _ -> None

    override _.Read(reader: byref<Utf8JsonReader>, _, serializerOptions) =
        use document = JsonDocument.ParseValue(&reader)
        let element = document.RootElement

        match chooseCase element with
        | None -> raise (JsonException(sprintf "Unable to match JSON to Telegram union %s." unionType.FullName))
        | Some caseInfo ->
            let fields = caseInfo.GetFields()

            let values =
                if fields.Length = 0 then
                    [||]
                elif fields.Length = 1 then
                    [| JsonSerializer.Deserialize(element, fields[0].PropertyType, serializerOptions) |]
                else
                    raise (JsonException(sprintf "Telegram union %s has an unsupported multi-field case." unionType.FullName))

            FSharpValue.MakeUnion(caseInfo, values, true) :?> 'T

    override _.Write(_, _, _) =
        raise (NotSupportedException("Telegram update serialization is not supported by this converter."))

type private TelegramUnionJsonConverterFactory() =
    inherit JsonConverterFactory()

    let isBuiltInFSharpUnion (valueType: Type) =
        if not valueType.IsGenericType then
            false
        else
            let definition = valueType.GetGenericTypeDefinition()
            definition = typedefof<option<_>> || definition = typedefof<list<_>> || definition = typedefof<voption<_>>

    override _.CanConvert(valueType) =
        FSharpType.IsUnion(valueType, true) && not (isBuiltInFSharpUnion valueType)

    override _.CreateConverter(valueType, _) =
        let converterType = typedefof<TelegramUnionJsonConverter<_>>.MakeGenericType(valueType)
        Activator.CreateInstance(converterType) :?> JsonConverter

[<RequireQualifiedAccess>]
module TelegramUpdateJson =
    let private serializerOptions =
        let options =
            JsonSerializerOptions(
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = false
            )

        options.Converters.Add(UnixTimestampDateTimeConverter())
        options.Converters.Add(TelegramUnionJsonConverterFactory())
        options

    let tryParse (buffer: byte array) (count: int) : Result<Update, string> =
        if isNull buffer || count <= 0 || count > buffer.Length then
            Error "JSON body must be a non-empty object."
        else
            try
                let json = ReadOnlyMemory<byte>(buffer, 0, count)
                use document = JsonDocument.Parse(json)
                let root = document.RootElement
                let mutable updateIdElement = Unchecked.defaultof<JsonElement>
                let mutable updateId = 0L

                if root.ValueKind <> JsonValueKind.Object then
                    Error "JSON body must be an object."
                elif not (root.TryGetProperty("update_id", &updateIdElement)) then
                    Error "update_id is required."
                elif updateIdElement.ValueKind <> JsonValueKind.Number || not (updateIdElement.TryGetInt64(&updateId)) then
                    Error "update_id must be an integer."
                else
                    let update = JsonSerializer.Deserialize<Update>(json.Span, serializerOptions)

                    if isNull (box update) then
                        Error "JSON body must contain a Telegram update."
                    else
                        Ok update
            with
            | :? JsonException -> Error "JSON body must be a valid Telegram update."
            | :? NotSupportedException -> Error "JSON body contains an unsupported Telegram update shape."

module FunogramConfig =
    let create (options: TelegramOptions) =
        { Config.defaultConfig with Token = options.BotToken }
