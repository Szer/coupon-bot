namespace CouponHubBot.Services

open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open CouponHubBot

type TelegramNotificationService(
    botClient: ITelegramBotClient,
    logger: ILogger<TelegramNotificationService>
) =

    member _.NotifyTakerCouponVoided(takerUserId: int64, coupon: Coupon) : Task<bool> =
        task {
            let v = coupon.value.ToString("0.##")
            let mc = coupon.min_check.ToString("0.##")
            let msg = $"Купон ID:{coupon.id} ({v}€/{mc}€) был аннулирован владельцем. Он больше недоступен."
            try
                do! botClient.SendMessage(ChatId takerUserId, msg) :> Task
                return true
            with ex1 ->
                logger.LogWarning(ex1, "First attempt to notify taker {TakerId} about voided coupon {CouponId} failed, retrying", takerUserId, coupon.id)
                try
                    do! Task.Delay(500)
                    do! botClient.SendMessage(ChatId takerUserId, msg) :> Task
                    return true
                with ex2 ->
                    logger.LogError(ex2, "Failed to notify taker {TakerId} about voided coupon {CouponId} after retry", takerUserId, coupon.id)
                    return false
        }
