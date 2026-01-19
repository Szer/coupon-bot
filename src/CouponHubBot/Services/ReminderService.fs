namespace CouponHubBot.Services

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open CouponHubBot

type IReminderRunner =
    abstract member RunOnce: unit -> Task<bool>

type ReminderService(
    botClient: ITelegramBotClient,
    botConfig: BotConfiguration,
    db: IDbService,
    logger: ILogger<ReminderService>
) =
    inherit BackgroundService()

    let runOnce () =
        task {
            let! coupons = db.GetExpiringTodayAvailable()
            if coupons.Length > 0 then
                let total = coupons |> Array.sumBy (fun c -> c.value)
                let totalStr = total.ToString("0.##")
                let msg = $"Сегодня истекают {coupons.Length} купонов на общую сумму {totalStr} EUR!"
                do! botClient.SendMessage(ChatId botConfig.CommunityChatId, msg) :> Task
                return true
            else
                return false
        }

    let nextRunUtc (hourUtc: int) =
        let now = DateTime.UtcNow
        let today = DateTime(now.Year, now.Month, now.Day, hourUtc, 0, 0, DateTimeKind.Utc)
        if now <= today then today else today.AddDays(1.0)

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            if botConfig.ReminderRunOnStart then
                try
                    let! _ = runOnce ()
                    ()
                with ex ->
                    logger.LogError(ex, "Failed to run reminder on startup")

            while not stoppingToken.IsCancellationRequested do
                let next = nextRunUtc botConfig.ReminderHourUtc
                let delay = next - DateTime.UtcNow
                if delay > TimeSpan.Zero then
                    logger.LogInformation("Next reminder run at {NextRunUtc}", next)
                    do! Task.Delay(delay, stoppingToken)

                if stoppingToken.IsCancellationRequested then
                    ()
                else
                    try
                        let! _ = runOnce ()
                        ()
                    with ex ->
                        logger.LogError(ex, "Failed to send reminder")
        }

    interface IReminderRunner with
        member _.RunOnce() = runOnce ()

