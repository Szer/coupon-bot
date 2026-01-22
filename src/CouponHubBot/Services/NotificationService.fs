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
        match u.username, u.first_name with
        | un, _ when not (String.IsNullOrWhiteSpace un) -> "@" + un
        | _, fn when not (String.IsNullOrWhiteSpace fn) -> fn
        | _ -> string u.id

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
            do! sendToGroup $"{formatUser owner} добавил(а) купон на {v}€ из {mc}€ сроком {d}"
        }

    member _.CouponTaken(coupon, taker) =
        task {
            let! ownerOpt = db.GetUserById(coupon.owner_id)
            let v, mc, d = fmtCoupon coupon
            match ownerOpt with
            | Some owner ->
                do! sendToGroup $"{formatUser taker} взял(а) купон на {v}€ из {mc}€ сроком {d} от {formatUser owner}"
            | None ->
                do! sendToGroup $"{formatUser taker} взял(а) купон на {v}€ из {mc}€ сроком {d}"
        }

    member _.CouponUsed(coupon, user) =
        task {
            let v, mc, _d = fmtCoupon coupon
            do! sendToGroup $"{formatUser user} использовал(а) купон на {v}€ из {mc}€"
        }

    member _.CouponReturned(coupon, user) =
        task {
            let v, mc, d = fmtCoupon coupon
            do! sendToGroup $"{formatUser user} вернул(а) купон на {v}€ из {mc}€ (срок {d}) в общий доступ"
        }
