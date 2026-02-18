namespace CouponHubBot.Services

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open CouponHubBot

type TelegramNotificationService(
    botClient: ITelegramBotClient,
    botConfig: BotConfiguration,
    db: DbService,
    logger: ILogger<TelegramNotificationService>,
    time: TimeProvider
) =
    let formatUser (u: DbUser) =
        if not (String.IsNullOrWhiteSpace u.username) then
            "@" + u.username
        elif not (String.IsNullOrWhiteSpace u.first_name) || not (String.IsNullOrWhiteSpace u.last_name) then
            String.Join(" ", u.first_name, u.last_name)
        else
            string u.id

    let fmtCoupon (c: Coupon) =
        let v = c.value.ToString("0.##")
        let mc = c.min_check.ToString("0.##")
        let d = Utils.DateFormatting.formatDateNoYearWithDow c.expires_at
        v, mc, d

    let sendToGroup (text: string) =
        task {
            try
                do! botClient.SendMessage(ChatId botConfig.CommunityChatId, text) :> Task
            with ex ->
                logger.LogWarning(ex, "Failed to send group notification")
        }

    member _.CouponAdded(coupon) =
        task {
            let! ownerOpt = db.GetUserById(coupon.owner_id)
            let owner = match ownerOpt with | Some o -> o | None -> { id = coupon.owner_id; username = null; first_name = null; last_name = null; created_at = time.GetUtcNow().UtcDateTime; updated_at = time.GetUtcNow().UtcDateTime }
            let v, mc, d = fmtCoupon coupon
            let appIcon = if coupon.is_app_coupon then "📱 " else ""
            do! sendToGroup $"{formatUser owner} добавил(а) {appIcon}купон на {v}€ из {mc}€ сроком {d}"
        }

    member _.CouponTaken(coupon, taker) =
        task {
            let! ownerOpt = db.GetUserById(coupon.owner_id)
            let v, mc, d = fmtCoupon coupon
            let appIcon = if coupon.is_app_coupon then "📱 " else ""
            match ownerOpt with
            | Some owner ->
                do! sendToGroup $"{formatUser taker} взял(а) {appIcon}купон на {v}€ из {mc}€ сроком {d} от {formatUser owner}"
            | None ->
                do! sendToGroup $"{formatUser taker} взял(а) {appIcon}купон на {v}€ из {mc}€ сроком {d}"
        }

    member _.CouponUsed(coupon, user) =
        task {
            let v, mc, _d = fmtCoupon coupon
            let appIcon = if coupon.is_app_coupon then "📱 " else ""
            do! sendToGroup $"{formatUser user} использовал(а) {appIcon}купон на {v}€ из {mc}€"
        }

    member _.CouponReturned(coupon, user) =
        task {
            let v, mc, d = fmtCoupon coupon
            let appIcon = if coupon.is_app_coupon then "📱 " else ""
            do! sendToGroup $"{formatUser user} вернул(а) {appIcon}купон на {v}€ из {mc}€ (срок {d}) в общий доступ"
        }

    member _.NotifyTakerCouponVoided(takerUserId: int64, coupon: Coupon) : Task<bool> =
        task {
            let appIcon = if coupon.is_app_coupon then "📱 " else ""
            let v = coupon.value.ToString("0.##")
            let mc = coupon.min_check.ToString("0.##")
            let msg = $"{appIcon}Купон ID:{coupon.id} ({v}€/{mc}€) был аннулирован владельцем. Он больше недоступен."
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
