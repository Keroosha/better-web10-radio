namespace Web10.Radio.API

open System
open System.Security.Claims
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Npgsql
open Web10.Radio.Database
open Dodo.Primitives
open Web10.Radio.Database.Repositories


[<RequireQualifiedAccess>]
module private AdminIdentityLog =
    let private currentTraceId () =
        let current = System.Diagnostics.Activity.Current

        if isNull current then
            String.Empty
        else
            let traceId = current.TraceId.ToString()
            if String.IsNullOrWhiteSpace traceId then String.Empty else traceId

    let private bootstrapFailedMessage =
        LoggerMessage.Define<string>(
            LogLevel.Critical,
            EventId(3050, "AdminIdentityBootstrapFailed"),
            "Admin identity bootstrap failed traceId {traceId}."
        )

    let bootstrapFailed (logger: ILogger) =
        bootstrapFailedMessage.Invoke(logger, currentTraceId (), null)
[<RequireQualifiedAccess>]
module AdminSessionAuthentication =
    [<Literal>]
    let SchemeName = "Web10AdminSession"

    [<Literal>]
    let PolicyName = "Web10Admin"

    [<Literal>]
    let CookieName = "web10_admin_session"

    let SessionLifetime = TimeSpan.FromHours(8.0)

    let fixedTimeEqualsUtf8 (expected: string) (actual: string) =
        let hash (value: string) =
            value
            |> fun candidate -> if isNull candidate then String.Empty else candidate
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData

        CryptographicOperations.FixedTimeEquals(hash expected, hash actual)

type AdminLogin =
    { Username: string
      SessionToken: string
      CsrfToken: string
      ExpiresAtUtc: DateTimeOffset
      DevelopmentFixturesEnabled: bool }

type AdminActiveSession =
    { Username: string
      Session: AdminSession
      DevelopmentFixturesEnabled: bool }

[<RequireQualifiedAccess>]
type AdminLoginOutcome =
    | Authenticated of AdminLogin
    | InvalidCredentials

[<RequireQualifiedAccess>]
module private AdminIdentity =
    let normalizeUsername (username: string) =
        if isNull username then String.Empty else username.Trim().ToUpperInvariant()

type AdminIdentityService
    (
        dataSource: NpgsqlDataSource,
        timeProvider: TimeProvider,
        passwordHasher: IPasswordHasher<AdminUser>,
        adminOptions: AdminOptions,
        developmentFixturesEnabled: bool
    ) =
    let normalizeUsername = AdminIdentity.normalizeUsername

    let randomBase64Url byteCount =
        RandomNumberGenerator.GetBytes(byteCount)
        |> Convert.ToBase64String
        |> fun value -> value.TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let activeSessionTokenHash (sessionToken: string) =
        sessionToken
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData

    let configuredUser nowUtc =
        { Id = Uuid.CreateVersion7().ToGuidBigEndian()
          Username = adminOptions.Username
          NormalizedUsername = normalizeUsername adminOptions.Username
          PasswordHash = String.Empty
          CreatedAtUtc = nowUtc
          UpdatedAtUtc = nowUtc }

    let rec createSession (user: AdminUser) sessionToken csrfToken (nowUtc: DateTimeOffset) remaining cancellationToken =
        task {
            let session =
                { Id = Uuid.CreateVersion7().ToGuidBigEndian()
                  UserId = user.Id
                  TokenHash = activeSessionTokenHash sessionToken
                  CsrfToken = csrfToken
                  ExpiresAtUtc = nowUtc.Add(AdminSessionAuthentication.SessionLifetime)
                  CreatedAtUtc = nowUtc }

            let! outcome = AdminIdentityRepository.createSession dataSource session cancellationToken

            match outcome with
            | Error error -> return Error error
            | Ok(AdminSessionCreateOutcome.Created created) -> return Ok(created, sessionToken)
            | Ok AdminSessionCreateOutcome.NotFound ->
                return Error(DatabaseError("AdminIdentityService.createSession", "The authenticated administrator no longer exists."))
            | Ok AdminSessionCreateOutcome.Conflict when remaining > 1 ->
                return! createSession user (randomBase64Url 32) (randomBase64Url 32) nowUtc (remaining - 1) cancellationToken
            | Ok AdminSessionCreateOutcome.Conflict ->
                return Error(DatabaseError("AdminIdentityService.createSession", "Unable to allocate an administrator session."))
        }

    let createLogin (user: AdminUser) cancellationToken =
        task {
            let sessionToken = randomBase64Url 32
            let csrfToken = randomBase64Url 32
            let nowUtc = timeProvider.GetUtcNow()
            let! sessionResult = createSession user sessionToken csrfToken nowUtc 3 cancellationToken

            return
                sessionResult
                |> Result.map (fun (session, createdSessionToken) ->
                    AdminLoginOutcome.Authenticated
                        { Username = user.Username
                          SessionToken = createdSessionToken
                          CsrfToken = session.CsrfToken
                          ExpiresAtUtc = session.ExpiresAtUtc
                          DevelopmentFixturesEnabled = developmentFixturesEnabled })
        }


    member _.LoginAsync (username: string) (password: string) (cancellationToken: CancellationToken) : Task<Result<AdminLoginOutcome, RepositoryError>> =
        task {
            let normalizedUsername = normalizeUsername username
            let suppliedPassword = if isNull password then String.Empty else password
            let! userResult = AdminIdentityRepository.lookupActiveUserByNormalizedUsername dataSource normalizedUsername cancellationToken

            match userResult with
            | Error error -> return Error error
            | Ok None -> return Ok AdminLoginOutcome.InvalidCredentials
            | Ok(Some user) ->
                match passwordHasher.VerifyHashedPassword(user, user.PasswordHash, suppliedPassword) with
                | PasswordVerificationResult.Failed -> return Ok AdminLoginOutcome.InvalidCredentials
                | PasswordVerificationResult.Success -> return! createLogin user cancellationToken
                | PasswordVerificationResult.SuccessRehashNeeded ->
                    let updatedHash = passwordHasher.HashPassword(user, suppliedPassword)
                    let! updatedUserResult =
                        AdminIdentityRepository.updatePasswordHash dataSource user.Id updatedHash (timeProvider.GetUtcNow()) cancellationToken

                    match updatedUserResult with
                    | Error error -> return Error error
                    | Ok None -> return Ok AdminLoginOutcome.InvalidCredentials
                    | Ok(Some updatedUser) -> return! createLogin updatedUser cancellationToken
                | _ -> return Ok AdminLoginOutcome.InvalidCredentials
        }
    member _.TryGetActiveSessionAsync (sessionToken: string) (cancellationToken: CancellationToken) : Task<Result<AdminActiveSession option, RepositoryError>> =
        task {
            if String.IsNullOrEmpty sessionToken then
                return Ok None
            else
                let! sessionResult =
                    AdminIdentityRepository.lookupActiveSessionByTokenHash
                        dataSource
                        (activeSessionTokenHash sessionToken)
                        (timeProvider.GetUtcNow())
                        cancellationToken

                return
                    sessionResult
                    |> Result.map (Option.map (fun session ->
                        { Username = adminOptions.Username
                          Session = session
                          DevelopmentFixturesEnabled = developmentFixturesEnabled }))
        }

    member _.RevokeSessionAsync (sessionId: Guid) (cancellationToken: CancellationToken) =
        AdminIdentityRepository.revokeSession dataSource sessionId (timeProvider.GetUtcNow()) cancellationToken

    member _.CsrfMatches (session: AdminSession) (suppliedToken: string) =
        AdminSessionAuthentication.fixedTimeEqualsUtf8 session.CsrfToken suppliedToken

    member _.BootstrapConfiguredAdminAsync (cancellationToken: CancellationToken) : Task<Result<unit, RepositoryError>> =
        task {
            let normalizedUsername = normalizeUsername adminOptions.Username
            let nowUtc = timeProvider.GetUtcNow()
            let! existingResult =
                AdminIdentityRepository.lookupActiveUserByNormalizedUsername dataSource normalizedUsername cancellationToken

            match existingResult with
            | Error error -> return Error error
            | Ok existing ->
                let hashUser = existing |> Option.defaultWith (fun () -> configuredUser nowUtc)
                let passwordHash = passwordHasher.HashPassword(hashUser, adminOptions.Password)
                let upsert =
                    { Id = hashUser.Id
                      Username = adminOptions.Username
                      NormalizedUsername = normalizedUsername
                      PasswordHash = passwordHash
                      UpdatedAtUtc = nowUtc }

                let! userResult = AdminIdentityRepository.upsertActiveUser dataSource upsert cancellationToken

                match userResult with
                | Error error -> return Error error
                | Ok user ->
                    let! softDeleteResult =
                        AdminIdentityRepository.softDeleteOtherActiveUsers dataSource user.Id nowUtc cancellationToken

                    return softDeleteResult
        }

type AdminSessionAuthenticationHandler
    (
        options: IOptionsMonitor<AuthenticationSchemeOptions>,
        loggerFactory: ILoggerFactory,
        encoder: UrlEncoder,
        identityService: AdminIdentityService
    ) =
    inherit AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)

    override this.HandleAuthenticateAsync() =
        let request = this.Request
        let requestAborted = this.Context.RequestAborted
        let schemeName = this.Scheme.Name

        task {
            let mutable sessionToken = String.Empty

            if not (request.Cookies.TryGetValue(AdminSessionAuthentication.CookieName, &sessionToken))
               || String.IsNullOrEmpty sessionToken then
                return AuthenticateResult.NoResult()
            else
                let! sessionResult = identityService.TryGetActiveSessionAsync sessionToken requestAborted

                match sessionResult with
                | Ok None -> return AuthenticateResult.NoResult()
                | Ok(Some activeSession) ->
                    let identity =
                        ClaimsIdentity(
                            [| Claim(ClaimTypes.NameIdentifier, activeSession.Session.UserId.ToString("D"))
                               Claim(ClaimTypes.Name, activeSession.Username) |],
                            schemeName
                        )

                    return AuthenticateResult.Success(AuthenticationTicket(ClaimsPrincipal(identity), schemeName))
                | Error _ -> return AuthenticateResult.Fail("Admin session validation failed.")
        }

    override this.HandleChallengeAsync(_properties) =
        let context = this.Context

        task {
            do!
                ApiProblems.write
                    context
                    StatusCodes.Status401Unauthorized
                    "admin.auth.required"
                    "Admin authentication required"
                    "An active administrator session is required."
        }
        :> Task

type AdminBootstrapHostedService(identityService: AdminIdentityService, logger: ILogger<AdminBootstrapHostedService>) =
    interface IHostedService with
        member _.StartAsync(cancellationToken: CancellationToken) =
            task {
                let! result = identityService.BootstrapConfiguredAdminAsync cancellationToken

                match result with
                | Ok () -> return ()
                | Error _ ->
                    AdminIdentityLog.bootstrapFailed logger
                    return raise (InvalidOperationException("Admin identity bootstrap failed."))
            }
            :> Task

        member _.StopAsync(_cancellationToken: CancellationToken) = Task.CompletedTask

[<RequireQualifiedAccess>]
module AdminIdentityComposition =
    let addAdminIdentityServices
        (adminOptions: AdminOptions)
        (developmentFixturesEnabled: bool)
        (services: IServiceCollection)
        : IServiceCollection =
        services.AddSingleton<AdminOptions>(adminOptions) |> ignore
        services.AddSingleton<IPasswordHasher<AdminUser>, PasswordHasher<AdminUser>>() |> ignore
        services.AddSingleton<AdminIdentityService>(fun provider ->
            new AdminIdentityService(
                provider.GetRequiredService<NpgsqlDataSource>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<IPasswordHasher<AdminUser>>(),
                adminOptions,
                developmentFixturesEnabled
            ))
        |> ignore
        services.AddHostedService<AdminBootstrapHostedService>() |> ignore
        services
