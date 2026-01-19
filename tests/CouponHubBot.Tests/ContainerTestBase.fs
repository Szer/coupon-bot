namespace CouponHubBot.Tests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Dapper
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations
open DotNet.Testcontainers.Containers
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

[<AbstractClass>]
type CouponHubTestContainers(seedExpiringToday: bool) =
    let solutionDir = CommonDirectoryPath.GetSolutionDirectory()
    let solutionDirPath = solutionDir.DirectoryPath
    let dbAlias = "coupon-db"
    let fakeAlias = "fake-tg-api"
    let secret = "OUR_SECRET"
    let botToken = "123:456"
    let communityChatId = -42L

    let mutable botHttp: HttpClient = null
    let mutable fakeHttp: HttpClient = null
    let mutable publicConnectionString: string = null

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
            .WithEnvironment("FLYWAY_USER", "coupon_hub_bot_service")
            .WithEnvironment("FLYWAY_PASSWORD", "coupon_hub_bot_service")
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
        ContainerBuilder()
            .WithImage(botImage)
            .WithNetwork(network)
            .WithPortBinding(80, true)
            .WithEnvironment("BOT_TELEGRAM_TOKEN", botToken)
            .WithEnvironment("BOT_AUTH_TOKEN", secret)
            .WithEnvironment("COMMUNITY_CHAT_ID", string communityChatId)
            .WithEnvironment("LOGS_CHAT_ID", "")
            .WithEnvironment("DATABASE_URL", $"Server={dbAlias};Database=coupon_hub_bot;Port=5432;User Id=coupon_hub_bot_service;Password=coupon_hub_bot_service;Include Error Detail=true;")
            .WithEnvironment("TELEGRAM_API_URL", $"http://{fakeAlias}:8080")
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", "80")
            .WithEnvironment("OCR_ENABLED", "false")
            .WithEnvironment("REMINDER_RUN_ON_START", "false")
            .WithEnvironment("REMINDER_HOUR_UTC", "8")
            .WithEnvironment("TEST_MODE", "true")
            .DependsOn(flywayContainer)
            .DependsOn(fakeContainer)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(80, fun s -> s.WithTimeout(TimeSpan.FromMinutes(2.0)) |> ignore))
            .Build()

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
INSERT INTO coupon(owner_id, photo_file_id, value, expires_at, status)
VALUES (100, 'seed-photo', 10.00, CURRENT_DATE, 'available');
"""
                let! _ = conn.ExecuteAsync(couponSql)
                ()
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                do! dbContainer.StartAsync()

                publicConnectionString <- $"Server=127.0.0.1;Database=coupon_hub_bot;Port={dbContainer.GetMappedPublicPort(5432)};User Id=coupon_hub_bot_service;Password=coupon_hub_bot_service;Include Error Detail=true;"

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
                let botImageTask = botImage.CreateAsync()
                do! Task.WhenAll(fakeImageTask, botImageTask)
                
                do! fakeContainer.StartAsync()
                do! botContainer.StartAsync()
                
                // Important: mapped ports are accessible from host via 127.0.0.1, not via container network aliases.
                botHttp <- new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{botContainer.GetMappedPublicPort(80)}"))
                // Give webhook enough time; Telegram API calls inside bot will timeout faster (2s).
                botHttp.Timeout <- TimeSpan.FromSeconds(15.0)
                botHttp.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", secret)

                fakeHttp <- new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{fakeContainer.GetMappedPublicPort(8080)}"))
                fakeHttp.Timeout <- TimeSpan.FromSeconds(5.0)
            }

        member _.DisposeAsync() =
            task {
                if not (isNull botHttp) then botHttp.Dispose()
                if not (isNull fakeHttp) then fakeHttp.Dispose()
                do! botContainer.DisposeAsync()
                do! fakeContainer.DisposeAsync()
                do! flywayContainer.DisposeAsync()
                do! dbContainer.DisposeAsync()
            }

    member _.CommunityChatId = communityChatId
    member _.Bot = botHttp
        member _.TelegramApi = fakeHttp
    member _.DbConnectionString = publicConnectionString
    


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

type DefaultCouponHubTestContainers() =
    inherit CouponHubTestContainers(seedExpiringToday = false)

