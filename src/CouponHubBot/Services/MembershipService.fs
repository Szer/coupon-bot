namespace CouponHubBot.Services

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types.Enums
open Telegram.Bot.Types

type TelegramMembershipService(
    botClient: ITelegramBotClient,
    botConfig: CouponHubBot.BotConfiguration,
    logger: ILogger<TelegramMembershipService>
) =
    // userId -> (isMember, cachedAtUtc)
    let cache = ConcurrentDictionary<int64, bool * DateTime>()
    let expiry = TimeSpan.FromDays(1.0)

    let isFresh (cachedAt: DateTime) =
        DateTime.UtcNow - cachedAt < expiry

    let statusIsMember (status: ChatMemberStatus) =
        match status with
        | ChatMemberStatus.Member
        | ChatMemberStatus.Administrator
        | ChatMemberStatus.Creator -> true
        | _ -> false

    member _.InvalidateCache() = cache.Clear()

    member _.OnChatMemberUpdated(update: ChatMemberUpdated) =
        if update.Chat <> null && update.Chat.Id = botConfig.CommunityChatId && update.NewChatMember <> null then
            let uid = update.NewChatMember.User.Id
            let isMember = statusIsMember update.NewChatMember.Status
            cache[uid] <- (isMember, DateTime.UtcNow)

    member _.IsMember(userId) =
        task {
            match cache.TryGetValue(userId) with
            | true, (isMember, cachedAt) when isFresh cachedAt -> return isMember
            | _ ->
                try
                    let! cm = botClient.GetChatMember(botConfig.CommunityChatId, userId)
                    let isMember = statusIsMember cm.Status
                    cache[userId] <- (isMember, DateTime.UtcNow)
                    return isMember
                with ex ->
                    logger.LogWarning(ex, "Failed to check membership for {UserId}", userId)
                    return false
        }

/// Clears membership cache on startup and then once per day (insurance against missed updates).
type MembershipCacheInvalidationService(membership: TelegramMembershipService, logger: ILogger<MembershipCacheInvalidationService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken) =
        task {
            membership.InvalidateCache()
            logger.LogInformation("Membership cache invalidated on startup")

            while not stoppingToken.IsCancellationRequested do
                do! Task.Delay(TimeSpan.FromDays(1.0), stoppingToken)
                if not stoppingToken.IsCancellationRequested then
                    membership.InvalidateCache()
                    logger.LogInformation("Membership cache invalidated (daily)")
        }
