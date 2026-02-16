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

    let formatCombinedStats (usedRows: UserEventCount array) (addedRows: UserEventCount array) =
        let usedMap = usedRows |> Array.map (fun r -> r.user_id, r.count) |> Map.ofArray
        let addedMap = addedRows |> Array.map (fun r -> r.user_id, r.count) |> Map.ofArray

        // Collect all users from both arrays, prefer usedRows for user info (username, first_name)
        let userInfoMap =
            Array.append usedRows addedRows
            |> Array.map (fun r -> r.user_id, (r.username, r.first_name))
            |> Map.ofArray

        let allUserIds =
            Set.union
                (usedRows |> Array.map _.user_id |> Set.ofArray)
                (addedRows |> Array.map _.user_id |> Set.ofArray)

        if Set.isEmpty allUserIds then
            "—"
        else
            allUserIds
            |> Set.toArray
            |> Array.map (fun uid ->
                let usedCount = Map.tryFind uid usedMap |> Option.defaultValue 0L
                let addedCount = Map.tryFind uid addedMap |> Option.defaultValue 0L
                let (username, firstName) = Map.find uid userInfoMap
                (uid, username, firstName, usedCount, addedCount))
            |> Array.sortByDescending (fun (_, _, _, used, added) -> used + added)
            |> Array.indexed
            |> Array.map (fun (i, (uid, username, firstName, usedCount, addedCount)) ->
                let n = i + 1
                let who = formatUser uid username firstName
                $"{n}. {who} — {usedCount}/{addedCount}")
            |> String.concat "\n"

    let runOnce (nowUtc: DateTime) =
        task {
            let mutable anySent = false

            let! coupons = db.GetExpiringTodayAvailable()
            if coupons.Length > 0 then
                let total = coupons |> Array.sumBy (fun c -> c.value)
                let totalStr = total.ToString("0.##")
                let couponWord = Utils.RussianPlural.choose coupons.Length "купон" "купона" "купонов"
                let msg = $"Сегодня истекает {coupons.Length} {couponWord} на сумму {totalStr}€!"
                do! botClient.SendMessage(ChatId botConfig.CommunityChatId, msg) :> Task
                anySent <- true

            if nowUtc.DayOfWeek = DayOfWeek.Monday then
                let since = nowUtc.AddDays(-7.0)
                let until = nowUtc
                let! usedRows = db.GetUserEventCounts("used", since, until)
                let! addedRows = db.GetUserEventCounts("added", since, until)

                let text =
                    "Статистика за последние 7 дней (использовано/добавлено):\n"
                    + formatCombinedStats usedRows addedRows

                do! botClient.SendMessage(ChatId botConfig.CommunityChatId, text) :> Task
                anySent <- true

            // DM reminder: user has taken coupons older than 1 day and forgot to mark used/return.
            // One message per user even if multiple overdue coupons.
            let! overdueUsers = db.GetUsersWithOverdueTakenCoupons(nowUtc, TimeSpan.FromDays(1.0))
            for r in overdueUsers do
                try
                    let couponWord = Utils.RussianPlural.choose r.overdue_count "купон" "купона" "купонов"
                    let participle = if r.overdue_count = 1 then "взятый" else "взятых"
                    let notMarked = if r.overdue_count = 1 then "не отмеченный" else "не отмеченных"
                    let text =
                        $"Напоминание: у тебя есть {r.overdue_count} {couponWord}, {participle} более 1 дня назад и всё ещё {notMarked}.\nОткрой /my и нажми «Использован» или «Вернуть»."
                    do! botClient.SendMessage(ChatId r.user_id, text) :> Task
                    anySent <- true
                with ex ->
                    logger.LogWarning(ex, "Failed to send overdue-taken reminder to {UserId}", r.user_id)

            // DM reminder: user used coupon yesterday but did not add any coupon on the same day.
            // One message per user.
            let! usersWhoUsedButDidNotAdd = db.GetUsersWhoUsedButDidNotAddYesterday(nowUtc)
            for userId in usersWhoUsedButDidNotAdd do
                try
                    let text = "Не забудь добавить купоны в бота"
                    do! botClient.SendMessage(ChatId userId, text) :> Task
                    anySent <- true
                with ex ->
                    logger.LogWarning(ex, "Failed to send add-coupon reminder to {UserId}", userId)

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
