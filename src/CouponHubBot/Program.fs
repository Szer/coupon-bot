open System
open System.Data
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Dapper
open Npgsql
open Telegram.Bot
open Telegram.Bot.Types
open CouponHubBot
open CouponHubBot.Utils
open CouponHubBot.Services

type Root = class end

/// Dapper type handler for DateOnly (maps to PostgreSQL DATE)
type DateOnlyTypeHandler() =
    inherit SqlMapper.TypeHandler<DateOnly>()
    override _.SetValue(parameter: IDbDataParameter, value: DateOnly) =
        parameter.Value <- value.ToDateTime(TimeOnly.MinValue)
    override _.Parse(value: obj) =
        match value with
        | :? DateOnly as d -> d
        | :? DateTime as dt -> DateOnly.FromDateTime(dt)
        | x -> failwithf "Unsupported DateOnly value: %A" x

let jsonOptions =
    let opts = JsonSerializerOptions(JsonSerializerDefaults.Web)
    opts.NumberHandling <- JsonNumberHandling.AllowReadingFromString
    Telegram.Bot.JsonBotAPI.Configure(opts)
    opts

let globalBotConfDontUseOnlyRegister =
    { BotToken = Utils.getEnv "BOT_TELEGRAM_TOKEN"
      SecretToken = Utils.getEnv "BOT_AUTH_TOKEN"
      CommunityChatId = Utils.getEnv "COMMUNITY_CHAT_ID" |> int64
      LogsChatId =
        match Utils.getEnvOr "LOGS_CHAT_ID" "" with
        | "" -> None
        | v -> Some (int64 v)
      TelegramApiBaseUrl =
        match Utils.getEnvOr "TELEGRAM_API_URL" "" with
        | "" -> null
        | v -> v
      ReminderHourUtc = Utils.getEnvOr "REMINDER_HOUR_UTC" "8" |> int
      ReminderRunOnStart = Utils.getEnvOrBool "REMINDER_RUN_ON_START" false
      OcrEnabled = Utils.getEnvOrBool "OCR_ENABLED" false
      OcrMaxFileSizeBytes = Utils.getEnvOrInt64 "OCR_MAX_FILE_SIZE_BYTES" (20L * 1024L * 1024L)
      AzureOcrEndpoint = Utils.getEnvOr "AZURE_OCR_ENDPOINT" ""
      AzureOcrKey = Utils.getEnvOr "AZURE_OCR_KEY" ""
      TestMode = Utils.getEnvOrBool "TEST_MODE" false }

let validateApiKey (ctx: HttpContext) =
    let botConf = ctx.RequestServices.GetRequiredService<BotConfiguration>()
    match ctx.Request.Headers.TryGetValue "X-Telegram-Bot-Api-Secret-Token" with
    | true, headerValues when headerValues.Count > 0 && headerValues[0] = botConf.SecretToken -> true
    | _ -> false

let builder = WebApplication.CreateBuilder()

// Register Dapper type handler for DateOnly
SqlMapper.AddTypeHandler(DateOnlyTypeHandler())

builder.Services.AddSingleton globalBotConfDontUseOnlyRegister |> ignore
// Configure JSON options for Telegram.Bot compatibility
builder.Services.Configure<JsonSerializerOptions>(fun (opts: JsonSerializerOptions) ->
    opts.NumberHandling <- JsonNumberHandling.AllowReadingFromString
    Telegram.Bot.JsonBotAPI.Configure(opts)
) |> ignore

%builder.Services
    .AddHttpClient("telegram_bot_client")
    .ConfigureHttpClient(fun httpClient ->
        // Prevent 100s hangs in tests when FakeTgApi is unreachable.
        httpClient.Timeout <- TimeSpan.FromSeconds(2.0)
    )
    .AddHttpMessageHandler<OutgoingHttpLoggingHandler>()
    .AddTypedClient(fun httpClient _sp ->
        let botConf = _sp.GetRequiredService<BotConfiguration>()
        let options =
            if isNull botConf.TelegramApiBaseUrl then
                TelegramBotClientOptions(botConf.BotToken)
            else
                // Telegram.Bot will omit path/query/fragment; we only need scheme://host:port
                TelegramBotClientOptions(botConf.BotToken, botConf.TelegramApiBaseUrl)
        TelegramBotClient(options, httpClient) :> ITelegramBotClient)

builder.Services.AddTransient<OutgoingHttpLoggingHandler>() |> ignore

builder.Services.AddSingleton<IBotService, BotService>() |> ignore
builder.Services.AddSingleton<IDbService>(fun _sp -> DbService(Utils.getEnv "DATABASE_URL") :> IDbService) |> ignore
builder.Services.AddSingleton<IMembershipService, TelegramMembershipService>() |> ignore
builder.Services.AddSingleton<INotificationService, TelegramNotificationService>() |> ignore

builder.Services.AddHttpClient<IOcrService, AzureOcrService>() |> ignore
builder.Services.AddHostedService<MembershipCacheInvalidationService>() |> ignore

// Register ReminderService as both hosted service and singleton (for IReminderRunner)
builder.Services.AddSingleton<ReminderService>() |> ignore
builder.Services.AddHostedService<ReminderService>(fun sp -> sp.GetRequiredService<ReminderService>()) |> ignore
builder.Services.AddSingleton<IReminderRunner>(fun sp -> sp.GetRequiredService<ReminderService>() :> IReminderRunner) |> ignore

let app = builder.Build()

// Log all incoming requests
app.Use(Func<HttpContext, Func<Task>, Task>(fun ctx next ->
    task {
        let logger = ctx.RequestServices.GetRequiredService<ILogger<Root>>()
        logger.LogInformation("HTTP REQUEST: {Method} {Path}", ctx.Request.Method, ctx.Request.Path.Value)
        do! next.Invoke()
    })) |> ignore

// Health check
app.MapGet("/health", Func<string>(fun () -> "OK")) |> ignore

// Test-only hook to trigger reminder immediately
app.MapPost("/test/run-reminder", Func<HttpContext, Task<IResult>>(fun ctx ->
    task {
        let botConf = ctx.RequestServices.GetRequiredService<BotConfiguration>()
        if not botConf.TestMode then
            return Results.NotFound()
        else
            let runner = ctx.RequestServices.GetRequiredService<IReminderRunner>()
            let! sent = runner.RunOnce()
            return Results.Json({| ok = true; sent = sent |})
    })) |> ignore

// Main webhook endpoint
app.MapPost("/bot", Func<HttpContext, Task<IResult>>(fun ctx ->
    task {
        // Validate API key
        if not (validateApiKey ctx) then
            ctx.Response.StatusCode <- 401
            return Results.Text("Access Denied")
        else
            let logger = ctx.RequestServices.GetRequiredService<ILogger<Root>>()
            logger.LogInformation("WEBHOOK: POST /bot received, API key validated")
            
            // Deserialize Update from request body
            let! update = JsonSerializer.DeserializeAsync<Update>(ctx.Request.Body, jsonOptions)
            logger.LogInformation("WEBHOOK: Received update {UpdateId}", update.Id)
            
            try
                let bot = ctx.RequestServices.GetRequiredService<IBotService>()
                do! bot.OnUpdate(update)
                logger.LogInformation("WEBHOOK: Update {UpdateId} processed successfully", update.Id)
                return Results.Ok()
            with ex ->
                logger.LogError(ex, "WEBHOOK: Unhandled error in update handler for {UpdateId}", update.Id)
                return Results.Ok()
    })) |> ignore

app.Run()

