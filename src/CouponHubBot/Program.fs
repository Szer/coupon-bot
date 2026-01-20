open System
open System.Collections.Generic
open System.Data
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open OpenTelemetry.Resources
open OpenTelemetry.Trace
open OpenTelemetry.Exporter
open Dapper
open Npgsql
open Serilog
open Serilog.Formatting.Compact
open Serilog.Enrichers.Span
open Telegram.Bot
open Telegram.Bot.Types
open CouponHubBot
open CouponHubBot.Utils
open CouponHubBot.Services
open CouponHubBot.Telemetry

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
SqlMapper.AddTypeHandler(DateOnlyTypeHandler())

let jsonOptions =
    let opts = JsonSerializerOptions(JsonSerializerDefaults.Web)
    opts.NumberHandling <- JsonNumberHandling.AllowReadingFromString
    Telegram.Bot.JsonBotAPI.Configure(opts)
    opts

let globalBotConfDontUseOnlyRegister =
    { BotToken = Utils.getEnv "BOT_TELEGRAM_TOKEN"
      SecretToken = Utils.getEnv "BOT_AUTH_TOKEN"
      CommunityChatId = Utils.getEnv "COMMUNITY_CHAT_ID" |> int64
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

// Configure Serilog for structured JSON logging (Loki-friendly) with trace correlation
%builder.Host.UseSerilog(fun context _ configuration ->
    %configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithSpan()
        .WriteTo.Console(CompactJsonFormatter())
)

%builder.Services.AddSingleton globalBotConfDontUseOnlyRegister
// Configure JSON options for Telegram.Bot compatibility
%builder.Services.Configure<JsonSerializerOptions>(fun (opts: JsonSerializerOptions) ->
    opts.NumberHandling <- JsonNumberHandling.AllowReadingFromString
    JsonBotAPI.Configure(opts)
)

%builder.Services
    .AddHttpClient("telegram_bot_client")
    .AddTypedClient(fun httpClient _sp ->
        let botConf = _sp.GetRequiredService<BotConfiguration>()
        let options =
            if isNull botConf.TelegramApiBaseUrl then
                TelegramBotClientOptions(botConf.BotToken)
            else
                // Telegram.Bot will omit path/query/fragment; we only need scheme://host:port
                TelegramBotClientOptions(botConf.BotToken, botConf.TelegramApiBaseUrl)
        TelegramBotClient(options, httpClient) :> ITelegramBotClient)

%builder.Services.AddHttpClient<IOcrService, AzureOcrService>()

%builder
    .Services
    .AddSingleton<BotService>()
    .AddSingleton<DbService>(fun _sp -> DbService(Utils.getEnv "DATABASE_URL"))
    .AddSingleton<TelegramMembershipService>()
    .AddSingleton<TelegramNotificationService>()
    .AddHostedService<MembershipCacheInvalidationService>()
    .AddHostedService<BotCommandsSetupService>()
    .AddSingleton<ReminderService>()
    .AddHostedService<ReminderService>(fun sp -> sp.GetRequiredService<ReminderService>())

%builder.Services.AddOpenTelemetry()
    .WithTracing(fun b ->
        %b         
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddNpgsql()
            .ConfigureResource(fun r ->
                %r.AddAttributes([ KeyValuePair("service.name", Utils.getEnvOr "OTEL_SERVICE_NAME" "coupon-hub-bot") ])
            )
            .AddSource(botActivity.Name)
        Utils.getEnvWith "OTEL_EXPORTER_OTLP_ENDPOINT" (fun endpoint ->
            %b.AddOtlpExporter(fun opts ->
                opts.Endpoint <- Uri(endpoint)
                opts.Protocol <- OtlpExportProtocol.Grpc
            )
        )
        Utils.getEnvWith "OTEL_EXPORTER_CONSOLE" (fun v ->
            if Boolean.Parse(v) then %b.AddConsoleExporter()
        )
    )

let app = builder.Build()

// Health check
%app.MapGet("/health", Func<string>(fun () -> "OK"))

// Test-only hook to trigger reminder immediately
%app.MapPost("/test/run-reminder", Func<HttpContext, Task<IResult>>(fun ctx ->
    task {
        let botConf = ctx.RequestServices.GetRequiredService<BotConfiguration>()
        if not botConf.TestMode then
            return Results.NotFound()
        else
            let runner = ctx.RequestServices.GetRequiredService<ReminderService>()
            let! sent = runner.RunOnce()
            return Results.Json({| ok = true; sent = sent |})
    }))

// Main webhook endpoint
%app.MapPost("/bot", Func<HttpContext, Task<IResult>>(fun ctx ->
    task {
        // Validate API key
        if not (validateApiKey ctx) then
            ctx.Response.StatusCode <- 401
            return Results.Text("Access Denied")
        else
            let logger = ctx.RequestServices.GetRequiredService<ILogger<Root>>()
            
            // Deserialize Update from request body
            let! update = JsonSerializer.DeserializeAsync<Update>(ctx.Request.Body, jsonOptions)

            try
                let bot = ctx.RequestServices.GetRequiredService<BotService>()
                do! bot.OnUpdate(update)
                return Results.Ok()
            with ex ->
                logger.LogError(ex, "Unhandled error in update handler for {UpdateId}", update.Id)
                return Results.Ok()
    }))

app.Run()
