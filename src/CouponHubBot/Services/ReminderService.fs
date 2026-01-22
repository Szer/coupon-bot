namespace CouponHubBot.Services

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open CouponHubBot

type ReminderService(
    botClient: ITelegramBotClient,
    botConfig: BotConfiguration,
    db: DbService,
    logger: ILogger<ReminderService>,
    time: TimeProvider
) =
    inherit BackgroundService()

    let formatUser (userId: int64) (username: string) (firstName: string) =
        if not (String.IsNullOrWhiteSpace username) then
            "@" + username
        elif not (String.IsNullOrWhiteSpace firstName) then
            firstName
        else
            string userId

    let formatTopList (title: string) (rows: UserEventCount array) =
        if rows.Length = 0 then
            $"{title}\n—"
        else
            let lines =
                rows
                |> Array.truncate 25
                |> Array.indexed
                |> Array.map (fun (i, r) ->
                    let n = i + 1
                    let who = formatUser r.user_id r.username r.first_name
                    $"{n}. {who} — {r.count}")
                |> String.concat "\n"
            $"{title}\n{lines}"

    let runOnce (nowUtc: DateTime) =
        task {
            let mutable anySent = false

            let! coupons = db.GetExpiringTodayAvailable()
            if coupons.Length > 0 then
                let total = coupons |> Array.sumBy (fun c -> c.value)
                let totalStr = total.ToString("0.##")
                 let msg = $"Сегодня истекают {coupons.Length} купонов на общую сумму {totalStr}€!"
                do! botClient.SendMessage(ChatId botConfig.CommunityChatId, msg) :> Task
                anySent <- true

            if nowUtc.DayOfWeek = DayOfWeek.Monday then
                let since = nowUtc.AddDays(-7.0)
                let until = nowUtc
                let! usedRows = db.GetUserEventCounts("used", since, until)
                let! addedRows = db.GetUserEventCounts("added", since, until)

                let text =
                    [
                        "Статистика за последние 7 дней:"
                        ""
                        formatTopList "Использовано купонов:" usedRows
                        ""
                        formatTopList "Добавлено купонов:" addedRows
                    ]
                    |> String.concat "\n"

                do! botClient.SendMessage(ChatId botConfig.CommunityChatId, text) :> Task
                anySent <- true

            // DM reminder: user has taken coupons older than 1 day and forgot to mark used/return.
            // One message per user even if multiple overdue coupons.
            let! overdueUsers = db.GetUsersWithOverdueTakenCoupons(nowUtc, TimeSpan.FromDays(1.0))
            for r in overdueUsers do
                try
                    let suffix = if r.overdue_count = 1 then "" else "ов"
                    let text =
                        $"Напоминание: у тебя есть {r.overdue_count} купон{suffix}, взятый более 1 дня назад и всё ещё не отмеченный.\nОткрой /my и нажми «Использован» или «Вернуть»."
                    do! botClient.SendMessage(ChatId r.user_id, text) :> Task
                    anySent <- true
                with ex ->
                    logger.LogWarning(ex, "Failed to send overdue-taken reminder to {UserId}", r.user_id)

            return anySent
        }

    let nextRunUtc (hourUtc: int) =
        let now = time.GetUtcNow().UtcDateTime
        let today = DateTime(now.Year, now.Month, now.Day, hourUtc, 0, 0, DateTimeKind.Utc)
        if now <= today then today else today.AddDays(1.0)

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            if botConfig.ReminderRunOnStart then
                try
                    let! _ = runOnce (time.GetUtcNow().UtcDateTime)
                    ()
                with ex ->
                    logger.LogError(ex, "Failed to run reminder on startup")

            while not stoppingToken.IsCancellationRequested do
                let next = nextRunUtc botConfig.ReminderHourUtc
                let delay = next - time.GetUtcNow().UtcDateTime
                if delay > TimeSpan.Zero then
                    logger.LogInformation("Next reminder run at {NextRunUtc}", next)
                    do! Task.Delay(delay, stoppingToken)

                if stoppingToken.IsCancellationRequested then
                    ()
                else
                    try
                        let! _ = runOnce(time.GetUtcNow().UtcDateTime)
                        ()
                    with ex ->
                        logger.LogError(ex, "Failed to send reminder")
        }

    member _.RunOnce(?nowUtc: DateTime) =
        let now = defaultArg nowUtc (time.GetUtcNow().UtcDateTime)
        runOnce now
