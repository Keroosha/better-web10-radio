namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Web10.Radio.Database

type TrackSummary =
    { Id: Guid
      Title: string
      Artist: string }

module TrackRepository =
    [<Literal>]
    let private listActiveSql = """SELECT "Id", "Title", "Artist"
FROM "Tracks"
WHERE "IsDeleted" = false
ORDER BY "Artist" ASC, "Title" ASC;"""

    let listActive (dataSource: NpgsqlDataSource) (cancellationToken: CancellationToken) : Task<TrackSummary list> =
        DatabaseSession.withConnection
            dataSource
            (fun connection cancellationToken ->
                task {
                    use command = new NpgsqlCommand(listActiveSql, connection)
                    let! reader = command.ExecuteReaderAsync(cancellationToken)
                    use reader = reader
                    let tracks = ResizeArray<TrackSummary>()
                    let mutable keepReading = true

                    while keepReading do
                        let! hasRow = reader.ReadAsync(cancellationToken)

                        if hasRow then
                            tracks.Add
                                { Id = reader.GetGuid(0)
                                  Title = reader.GetString(1)
                                  Artist = reader.GetString(2) }
                        else
                            keepReading <- false

                    return List.ofSeq tracks
                })
            cancellationToken
