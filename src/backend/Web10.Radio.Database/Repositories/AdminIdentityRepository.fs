namespace Web10.Radio.Database.Repositories

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Web10.Radio.Database

type AdminUser =
    { Id: Guid
      Username: string
      NormalizedUsername: string
      PasswordHash: string
      CreatedAtUtc: DateTimeOffset
      UpdatedAtUtc: DateTimeOffset }

type AdminUserToUpsert =
    { Id: Guid
      Username: string
      NormalizedUsername: string
      PasswordHash: string
      UpdatedAtUtc: DateTimeOffset }

[<Sealed>]
type AdminSession(
    id: Guid,
    userId: Guid,
    tokenHash: byte array,
    csrfToken: string,
    expiresAtUtc: DateTimeOffset,
    revokedAtUtc: DateTimeOffset option,
    createdAtUtc: DateTimeOffset,
    updatedAtUtc: DateTimeOffset
) =
    member _.Id = id
    member _.UserId = userId
    member _.TokenHash = tokenHash
    member _.CsrfToken = csrfToken
    member _.ExpiresAtUtc = expiresAtUtc
    member _.RevokedAtUtc = revokedAtUtc
    member _.CreatedAtUtc = createdAtUtc
    member _.UpdatedAtUtc = updatedAtUtc

type AdminSessionToCreate =
    { Id: Guid
      UserId: Guid
      TokenHash: byte array
      CsrfToken: string
      ExpiresAtUtc: DateTimeOffset
      CreatedAtUtc: DateTimeOffset }

[<RequireQualifiedAccess>]
type AdminSessionCreateOutcome =
    | Created of AdminSession
    | NotFound
    | Conflict

[<RequireQualifiedAccess>]
type AdminSessionRevokeOutcome =
    | Revoked
    | NotFound

module AdminIdentityRepository =
    let private databaseError operation (ex: exn) = DatabaseError(operation, ex.Message)

    let private isUniqueViolation (ex: PostgresException) = ex.SqlState = PostgresErrorCodes.UniqueViolation
    let private isForeignKeyViolation (ex: PostgresException) = ex.SqlState = PostgresErrorCodes.ForeignKeyViolation

    let private readUser (reader: NpgsqlDataReader) =
        { Id = reader.GetGuid(0)
          Username = reader.GetString(1)
          NormalizedUsername = reader.GetString(2)
          PasswordHash = reader.GetString(3)
          CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(4)
          UpdatedAtUtc = reader.GetFieldValue<DateTimeOffset>(5) }

    let private readSession (reader: NpgsqlDataReader) =
        AdminSession(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetFieldValue<byte array>(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            (if reader.IsDBNull(5) then None else Some(reader.GetFieldValue<DateTimeOffset>(5))),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7)
        )

    let upsertActiveUser
        (dataSource: NpgsqlDataSource)
        (user: AdminUserToUpsert)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminUser, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command =
                    new NpgsqlCommand(
                        """INSERT INTO "AdminUsers" ("Id", "Username", "NormalizedUsername", "PasswordHash", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @Username, @NormalizedUsername, @PasswordHash, false, @UpdatedAtUtc, @UpdatedAtUtc)
ON CONFLICT ("NormalizedUsername") WHERE "IsDeleted" = false
DO UPDATE SET "Username" = EXCLUDED."Username", "PasswordHash" = EXCLUDED."PasswordHash", "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc"
RETURNING "Id", "Username", "NormalizedUsername", "PasswordHash", "CreatedAtUtc", "UpdatedAtUtc";""",
                        connection
                    )
                command.Parameters.AddWithValue("Id", user.Id) |> ignore
                command.Parameters.AddWithValue("Username", user.Username) |> ignore
                command.Parameters.AddWithValue("NormalizedUsername", user.NormalizedUsername) |> ignore
                command.Parameters.AddWithValue("PasswordHash", user.PasswordHash) |> ignore
                command.Parameters.AddWithValue("UpdatedAtUtc", user.UpdatedAtUtc) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)
                return if found then Ok(readUser reader) else Error(DatabaseError("AdminIdentityRepository.upsertActiveUser", "The upsert did not return a user."))
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminIdentityRepository.upsertActiveUser" ex)
        }

    let lookupActiveUserByNormalizedUsername
        (dataSource: NpgsqlDataSource)
        (normalizedUsername: string)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminUser option, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("""SELECT "Id", "Username", "NormalizedUsername", "PasswordHash", "CreatedAtUtc", "UpdatedAtUtc"
FROM "AdminUsers"
WHERE "NormalizedUsername" = @NormalizedUsername AND "IsDeleted" = false;""", connection)
                command.Parameters.AddWithValue("NormalizedUsername", normalizedUsername) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)
                return Ok(if found then Some(readUser reader) else None)
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminIdentityRepository.lookupActiveUserByNormalizedUsername" ex)
        }

    let updatePasswordHash
        (dataSource: NpgsqlDataSource)
        (userId: Guid)
        (passwordHash: string)
        (updatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminUser option, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("""UPDATE "AdminUsers"
SET "PasswordHash" = @PasswordHash, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "Id" = @Id AND "IsDeleted" = false
RETURNING "Id", "Username", "NormalizedUsername", "PasswordHash", "CreatedAtUtc", "UpdatedAtUtc";""", connection)
                command.Parameters.AddWithValue("Id", userId) |> ignore
                command.Parameters.AddWithValue("PasswordHash", passwordHash) |> ignore
                command.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)
                return Ok(if found then Some(readUser reader) else None)
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminIdentityRepository.updatePasswordHash" ex)
        }

    let softDeleteOtherActiveUsers
        (dataSource: NpgsqlDataSource)
        (retainedUserId: Guid)
        (updatedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, RepositoryError>> =
        DatabaseSession.withTransactionResult
            dataSource
            (fun connection transaction token ->
                task {
                    try
                        use revokeCommand = new NpgsqlCommand("""UPDATE "AdminSessions" AS session
SET "RevokedAtUtc" = @UpdatedAtUtc, "UpdatedAtUtc" = @UpdatedAtUtc
FROM "AdminUsers" AS user_account
WHERE session."UserId" = user_account."Id"
  AND user_account."IsDeleted" = false
  AND user_account."Id" <> @RetainedUserId
  AND session."IsDeleted" = false
  AND session."RevokedAtUtc" IS NULL;""", connection, transaction)
                        revokeCommand.Parameters.AddWithValue("RetainedUserId", retainedUserId) |> ignore
                        revokeCommand.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                        let! _ = revokeCommand.ExecuteNonQueryAsync(token)
                        use deleteCommand = new NpgsqlCommand("""UPDATE "AdminUsers"
SET "IsDeleted" = true, "UpdatedAtUtc" = @UpdatedAtUtc
WHERE "IsDeleted" = false AND "Id" <> @RetainedUserId;""", connection, transaction)
                        deleteCommand.Parameters.AddWithValue("RetainedUserId", retainedUserId) |> ignore
                        deleteCommand.Parameters.AddWithValue("UpdatedAtUtc", updatedAtUtc) |> ignore
                        let! _ = deleteCommand.ExecuteNonQueryAsync(token)
                        return Ok()
                    with
                    | :? OperationCanceledException as ex when token.IsCancellationRequested -> return raise ex
                    | ex -> return Error(databaseError "AdminIdentityRepository.softDeleteOtherActiveUsers" ex)
                })
            cancellationToken

    let createSession
        (dataSource: NpgsqlDataSource)
        (session: AdminSessionToCreate)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminSessionCreateOutcome, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("""INSERT INTO "AdminSessions" ("Id", "UserId", "TokenHash", "CsrfToken", "ExpiresAtUtc", "RevokedAtUtc", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc")
VALUES (@Id, @UserId, @TokenHash, @CsrfToken, @ExpiresAtUtc, NULL, false, @CreatedAtUtc, @CreatedAtUtc)
RETURNING "Id", "UserId", "TokenHash", "CsrfToken", "ExpiresAtUtc", "RevokedAtUtc", "CreatedAtUtc", "UpdatedAtUtc";""", connection)
                command.Parameters.AddWithValue("Id", session.Id) |> ignore
                command.Parameters.AddWithValue("UserId", session.UserId) |> ignore
                command.Parameters.AddWithValue("TokenHash", session.TokenHash) |> ignore
                command.Parameters.AddWithValue("CsrfToken", session.CsrfToken) |> ignore
                command.Parameters.AddWithValue("ExpiresAtUtc", session.ExpiresAtUtc) |> ignore
                command.Parameters.AddWithValue("CreatedAtUtc", session.CreatedAtUtc) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)
                return if found then Ok(AdminSessionCreateOutcome.Created(readSession reader)) else Error(DatabaseError("AdminIdentityRepository.createSession", "The insert did not return a session."))
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | :? PostgresException as ex when isUniqueViolation ex -> return Ok AdminSessionCreateOutcome.Conflict
            | :? PostgresException as ex when isForeignKeyViolation ex -> return Ok AdminSessionCreateOutcome.NotFound
            | ex -> return Error(databaseError "AdminIdentityRepository.createSession" ex)
        }

    let lookupActiveSessionByTokenHash
        (dataSource: NpgsqlDataSource)
        (tokenHash: byte array)
        (nowUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminSession option, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("""SELECT session."Id", session."UserId", session."TokenHash", session."CsrfToken", session."ExpiresAtUtc", session."RevokedAtUtc", session."CreatedAtUtc", session."UpdatedAtUtc"
FROM "AdminSessions" AS session
INNER JOIN "AdminUsers" AS user_account ON user_account."Id" = session."UserId" AND user_account."IsDeleted" = false
WHERE session."TokenHash" = @TokenHash
  AND session."IsDeleted" = false
  AND session."RevokedAtUtc" IS NULL
  AND session."ExpiresAtUtc" > @NowUtc;""", connection)
                command.Parameters.AddWithValue("TokenHash", tokenHash) |> ignore
                command.Parameters.AddWithValue("NowUtc", nowUtc) |> ignore
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)
                return Ok(if found then Some(readSession reader) else None)
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminIdentityRepository.lookupActiveSessionByTokenHash" ex)
        }

    let revokeSession
        (dataSource: NpgsqlDataSource)
        (sessionId: Guid)
        (revokedAtUtc: DateTimeOffset)
        (cancellationToken: CancellationToken)
        : Task<Result<AdminSessionRevokeOutcome, RepositoryError>> =
        task {
            try
                use! connection = dataSource.OpenConnectionAsync(cancellationToken)
                use command = new NpgsqlCommand("""UPDATE "AdminSessions"
SET "RevokedAtUtc" = @RevokedAtUtc, "UpdatedAtUtc" = @RevokedAtUtc
WHERE "Id" = @Id AND "IsDeleted" = false AND "RevokedAtUtc" IS NULL;""", connection)
                command.Parameters.AddWithValue("Id", sessionId) |> ignore
                command.Parameters.AddWithValue("RevokedAtUtc", revokedAtUtc) |> ignore
                let! affected = command.ExecuteNonQueryAsync(cancellationToken)
                return Ok(if affected = 1 then AdminSessionRevokeOutcome.Revoked else AdminSessionRevokeOutcome.NotFound)
            with
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex -> return Error(databaseError "AdminIdentityRepository.revokeSession" ex)
        }
