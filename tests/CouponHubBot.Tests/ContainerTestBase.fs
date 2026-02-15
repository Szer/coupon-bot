namespace CouponHubBot.Tests

open System
open System.IO
open System.Net.Http
open System.Net.Http.Json
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Dapper
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations
open Npgsql
open Testcontainers.PostgreSql
open Xunit

[<CLIMutable>]
type FakeCall =
    { Method: string
      Url: string
      Body: string
      Timestamp: DateTime }

[<CLIMutable>]
type ChatMemberMock =
    { userId: int64
      status: string }

[<CLIMutable>]
type FileMock =
    { fileId: string
      contentBase64: string }

[<CLIMutable>]
type AzureResponseMock =
    { status: int
      body: string }

[<AbstractClass>]
type CouponHubTestContainers(seedExpiringToday: bool, ocrEnabled: bool) =
    let solutionDir = CommonDirectoryPath.GetSolutionDirectory()
    let solutionDirPath = solutionDir.DirectoryPath
    let dbAlias = "coupon-db"
    let fakeAlias = "fake-tg-api"
    let fakeAzureAlias = "fake-azure-ocr"
    let secret = "OUR_SECRET"
    let botToken = "123:456"
    let communityChatId = -42L

    // Freeze application time inside the bot container so tests are deterministic.
    // Keep it early enough so hardcoded 2026-01-xx dates in tests are not "expired".
    let fixedUtcNow = DateTimeOffset.Parse("2026-01-01T00:00:00Z", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal ||| Globalization.DateTimeStyles.AdjustToUniversal)
    let fixedDate = fixedUtcNow.UtcDateTime.Date
    let fixedDateIso = fixedDate.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)

    let mutable botHttp: HttpClient = null
    let mutable fakeHttp: HttpClient = null
    let mutable fakeAzureHttp: HttpClient = null
    let mutable publicConnectionString: string = null
    let mutable adminConnectionString: string = null

    let network = NetworkBuilder().Build()

    let dbContainer =
        PostgreSqlBuilder()
            .WithImage("postgres:15.6")
            .WithNetwork(network)
            .WithNetworkAliases(dbAlias)
            .Build()

    let flywayContainer =
        ContainerBuilder()
            .WithImage("flyway/flyway")
            .WithNetwork(network)
            .WithBindMount(Path.Combine(solutionDirPath, "src", "migrations"), "/flyway/sql", AccessMode.ReadOnly)
            .WithEnvironment("FLYWAY_URL", $"jdbc:postgresql://{dbAlias}:5432/coupon_hub_bot")
            .WithEnvironment("FLYWAY_USER", "admin")
            .WithEnvironment("FLYWAY_PASSWORD", "admin")
            .WithCommand("migrate", "-schemas=public")
            .DependsOn(dbContainer)
            .WithWaitStrategy(
                Wait.ForUnixContainer().AddCustomWaitStrategy(
                    { new IWaitUntil with
                        member _.UntilAsync(container) =
                            task {
                                let! _ = container.GetExitCodeAsync()
                                return true
                            } }))
            .Build()

    let fakeImage =
        ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionDir, String.Empty)
            .WithDockerfile("./tests/FakeTgApi/Dockerfile")
            .WithName("coupon-hub-fake-tg-api-test")
            .WithDeleteIfExists(true)
            .WithCleanUp(true)
            // Force rebuild by adding unique build arg (prevents Docker layer caching)
            .WithBuildArgument("FORCE_REBUILD", DateTime.UtcNow.Ticks.ToString())
            .Build()

    let fakeContainer =
        ContainerBuilder()
            .WithImage(fakeImage)
            .WithNetwork(network)
            .WithNetworkAliases(fakeAlias)
            .WithPortBinding(8080, true)
            .WithEnvironment("ASPNETCORE_URLS", "http://*:8080")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(8080, fun x -> x.WithTimeout(TimeSpan.FromMinutes(1.0)) |> ignore))
            .Build()

    let fakeAzureImage =
        ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionDir, String.Empty)
            .WithDockerfile("./tests/FakeAzureOcrApi/Dockerfile")
            .WithName("coupon-hub-fake-azure-ocr-test")
            .WithDeleteIfExists(true)
            .WithCleanUp(true)
            .WithBuildArgument("FORCE_REBUILD", DateTime.UtcNow.Ticks.ToString())
            .Build()

    let fakeAzureContainer =
        ContainerBuilder()
            .WithImage(fakeAzureImage)
            .WithNetwork(network)
            .WithNetworkAliases(fakeAzureAlias)
            .WithPortBinding(8081, true)
            .WithEnvironment("ASPNETCORE_URLS", "http://*:8081")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(8081, fun x -> x.WithTimeout(TimeSpan.FromMinutes(1.0)) |> ignore))
            .Build()

    let botImage =
        ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionDir, String.Empty)
            .WithDockerfile("./Dockerfile")
            .WithName("coupon-hub-bot-test")
            .WithDeleteIfExists(true)
            .WithCleanUp(true)
            // Force rebuild by adding unique build arg (prevents Docker layer caching)
            .WithBuildArgument("FORCE_REBUILD", DateTime.UtcNow.Ticks.ToString())
            .Build()

    let botContainer =
        let b =
            ContainerBuilder()
                .WithImage(botImage)
                .WithNetwork(network)
                .WithPortBinding(80, true)
                .WithEnvironment("BOT_TELEGRAM_TOKEN", botToken)
                .WithEnvironment("BOT_AUTH_TOKEN", secret)
                .WithEnvironment("COMMUNITY_CHAT_ID", string communityChatId)
                .WithEnvironment("LOGS_CHAT_ID", "")
                .WithEnvironment("FEEDBACK_ADMINS", "900,901")
                .WithEnvironment("DATABASE_URL", $"Server={dbAlias};Database=coupon_hub_bot;Port=5432;User Id=coupon_hub_bot_service;Password=coupon_hub_bot_service;Include Error Detail=true;")
                .WithEnvironment("TELEGRAM_API_URL", $"http://{fakeAlias}:8080")
                .WithEnvironment("ASPNETCORE_HTTP_PORTS", "80")
                .WithEnvironment("OCR_ENABLED", if ocrEnabled then "true" else "false")
                .WithEnvironment("OCR_MAX_FILE_SIZE_BYTES", "52428800")
                .WithEnvironment("AZURE_OCR_ENDPOINT", if ocrEnabled then $"http://{fakeAzureAlias}:8081" else "")
                .WithEnvironment("AZURE_OCR_KEY", if ocrEnabled then "fake-key" else "")
                .WithEnvironment("REMINDER_RUN_ON_START", "false")
                .WithEnvironment("REMINDER_HOUR_UTC", "8")
                .WithEnvironment("TEST_MODE", "true")
                .WithEnvironment("MAX_TAKEN_COUPONS", "4")
                .WithEnvironment("BOT_FIXED_UTC_NOW", fixedUtcNow.ToString("o"))
                .DependsOn(flywayContainer)
                .DependsOn(fakeContainer)
        let b = if ocrEnabled then b.DependsOn(fakeAzureContainer) else b
        b
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(80, fun s -> s.WithTimeout(TimeSpan.FromMinutes(2.0)) |> ignore))
            .Build()

    let mutable testArtifactsDir: string = null

    let dumpContainerLogs (containerName: string) (container: DotNet.Testcontainers.Containers.IContainer) =
        task {
            try
                let! (stdout, stderr) = container.GetLogsAsync()
                let dir = testArtifactsDir
                if not (isNull dir) then
                    Directory.CreateDirectory(dir) |> ignore
                    let path = Path.Combine(dir, $"{containerName}.log")
                    let content =
                        $"=== STDOUT ===\n{stdout}\n=== STDERR ===\n{stderr}\n"
                    File.WriteAllText(path, content)
                return (stdout, stderr)
            with ex ->
                eprintfn $"Failed to get logs for {containerName}: {ex.Message}"
                return ("", "")
        }

    let seedDb () =
        task {
            use conn = new NpgsqlConnection(publicConnectionString)
            do! conn.OpenAsync()

            // Seed owner + expiring coupon before bot starts so ReminderRunOnStart can pick it up.
            if seedExpiringToday then
                //language=postgresql
                let userSql =
                    """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (100, 'owner', 'Owner', NOW(), NOW())
ON CONFLICT (id) DO NOTHING;
"""
                let! _ = conn.ExecuteAsync(userSql)
                ()

                //language=postgresql
                let couponSql =
                    """
INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES (100, 'seed-photo', 10.00, 50.00, @expires_at::date, 'available');
"""
                let! _ = conn.ExecuteAsync(couponSql, {| expires_at = fixedDateIso |})
                ()
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(
                task {
                    // Set up test artifacts directory for container logs
                    let fixtureName = if ocrEnabled then "OcrCouponHubTestContainers" else "DefaultCouponHubTestContainers"
                    testArtifactsDir <- Path.Combine(solutionDirPath, "test-artifacts", fixtureName)

                    do! dbContainer.StartAsync()

                    publicConnectionString <- $"Server=127.0.0.1;Database=coupon_hub_bot;Port={dbContainer.GetMappedPublicPort(5432)};User Id=coupon_hub_bot_service;Password=coupon_hub_bot_service;Include Error Detail=true;"
                    adminConnectionString <- $"Server=127.0.0.1;Database=coupon_hub_bot;Port={dbContainer.GetMappedPublicPort(5432)};User Id=admin;Password=admin;Include Error Detail=true;"
                    
                    // init schema/user/db
                    let initSql = File.ReadAllText(Path.Combine(solutionDirPath, "init.sql"))
                    let! initResult = dbContainer.ExecScriptAsync(initSql)
                    if initResult.Stderr <> "" then failwith initResult.Stderr

                    // run migrations
                    do! flywayContainer.StartAsync()

                    // seed if needed (after migrations)
                    do! seedDb ()

                    // build images & start fake + bot
                    let fakeImageTask = fakeImage.CreateAsync()
                    let fakeAzureImageTask =
                        if ocrEnabled then fakeAzureImage.CreateAsync() else Task.CompletedTask
                    let botImageTask = botImage.CreateAsync()
                    do! Task.WhenAll(fakeImageTask, fakeAzureImageTask, botImageTask)
                    
                    do! fakeContainer.StartAsync()
                    if ocrEnabled then
                        do! fakeAzureContainer.StartAsync()
                    do! botContainer.StartAsync()
                    
                    // Important: mapped ports are accessible from host via 127.0.0.1, not via container network aliases.
                    botHttp <- new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{botContainer.GetMappedPublicPort(80)}"))
                    // Give webhook enough time; Telegram API calls inside bot will timeout faster (2s).
                    botHttp.Timeout <- TimeSpan.FromSeconds(15.0)
                    botHttp.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", secret)

                    fakeHttp <- new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{fakeContainer.GetMappedPublicPort(8080)}"))
                    fakeHttp.Timeout <- TimeSpan.FromSeconds(5.0)
                    if ocrEnabled then
                        fakeAzureHttp <- new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{fakeAzureContainer.GetMappedPublicPort(8081)}"))
                        fakeAzureHttp.Timeout <- TimeSpan.FromSeconds(5.0)
                } :> Task)

        member _.DisposeAsync() =
            ValueTask(
                task {
                    // Dump container logs BEFORE stopping -- logs vanish after disposal.
                    let! _ = dumpContainerLogs "bot" botContainer
                    let! _ = dumpContainerLogs "fake-tg-api" fakeContainer
                    if ocrEnabled then
                        let! _ = dumpContainerLogs "fake-azure-ocr" fakeAzureContainer
                        ()
                    let! _ = dumpContainerLogs "flyway" flywayContainer
                    let! _ = dumpContainerLogs "postgres" dbContainer

                    if not (isNull botHttp) then botHttp.Dispose()
                    if not (isNull fakeHttp) then fakeHttp.Dispose()
                    if not (isNull fakeAzureHttp) then fakeAzureHttp.Dispose()
                    do! botContainer.DisposeAsync()
                    do! fakeContainer.DisposeAsync()
                    if ocrEnabled then
                        do! fakeAzureContainer.DisposeAsync()
                    do! flywayContainer.DisposeAsync()
                    do! dbContainer.DisposeAsync()
                } :> Task)

    /// Get stdout+stderr logs from the bot container (useful for debugging test failures).
    member _.GetBotLogs() =
        task {
            let! (stdout, stderr) = botContainer.GetLogsAsync()
            return $"=== STDOUT ===\n{stdout}\n=== STDERR ===\n{stderr}"
        }

    /// Get logs from all containers as a single string (useful for debugging test failures).
    member _.GetAllLogs() =
        task {
            let sb = System.Text.StringBuilder()
            for (name, container) in
                [ "bot", botContainer
                  "fake-tg-api", fakeContainer
                  "postgres", dbContainer ] do
                let! (stdout, stderr) = container.GetLogsAsync()
                sb.AppendLine($"=== {name} STDOUT ===").AppendLine(stdout) |> ignore
                sb.AppendLine($"=== {name} STDERR ===").AppendLine(stderr) |> ignore
            if ocrEnabled then
                let! (stdout, stderr) = fakeAzureContainer.GetLogsAsync()
                sb.AppendLine("=== fake-azure-ocr STDOUT ===").AppendLine(stdout) |> ignore
                sb.AppendLine("=== fake-azure-ocr STDERR ===").AppendLine(stderr) |> ignore
            return sb.ToString()
        }

    member _.CommunityChatId = communityChatId
    member _.Bot = botHttp
        member _.TelegramApi = fakeHttp
    member _.DbConnectionString = publicConnectionString
    member _.FixedUtcNow = fixedUtcNow
    member _.FixedToday = DateOnly.FromDateTime(fixedUtcNow.UtcDateTime)
    


    member _.ClearFakeCalls() =
        task {
            let! _ = fakeHttp.DeleteAsync("/test/calls")
            return ()
        }

    member _.GetFakeCalls(method: string) =
        task {
            let! resp = fakeHttp.GetFromJsonAsync<FakeCall array>($"/test/calls?method={method}")
            return resp
        }

    member _.SetChatMemberStatus(userId: int64, status: string) =
        task {
            let payload: ChatMemberMock = { userId = userId; status = status }
            let! _ = fakeHttp.PostAsJsonAsync("/test/mock/chatMember", payload)
            return ()
        }

    member _.SetTelegramFile(fileId: string, bytes: byte[]) =
        task {
            let payload: FileMock =
                { fileId = fileId
                  contentBase64 = Convert.ToBase64String(bytes) }
            let! _ = fakeHttp.PostAsJsonAsync("/test/mock/file", payload)
            return ()
        }

    member _.SetAzureOcrResponse(status: int, body: string) =
        task {
            if not ocrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureResponseMock = { status = status; body = body }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/response", payload)
            return ()
        }

    member _.SendUpdate(update: Telegram.Bot.Types.Update) =
        task {
            let jsonOptions = JsonSerializerOptions(JsonSerializerDefaults.Web)
            Telegram.Bot.JsonBotAPI.Configure(jsonOptions)
            let json = JsonSerializer.Serialize(update, jsonOptions)
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            return! botHttp.PostAsync("/bot", content)
        }

    member _.QuerySingle<'t>(sql: string, param: obj) =
        task {
            use conn = new NpgsqlConnection(publicConnectionString)
            return! conn.QuerySingleAsync<'t>(sql, param)
        }
        
    member _.TruncateCoupons() =
        task {
            use conn = new NpgsqlConnection(adminConnectionString)
            do! conn.OpenAsync()
            do! conn.ExecuteAsync("TRUNCATE coupon CASCADE") :> Task
        }

type DefaultCouponHubTestContainers() =
    inherit CouponHubTestContainers(seedExpiringToday = false, ocrEnabled = false)

type OcrCouponHubTestContainers() =
    inherit CouponHubTestContainers(seedExpiringToday = false, ocrEnabled = true)

