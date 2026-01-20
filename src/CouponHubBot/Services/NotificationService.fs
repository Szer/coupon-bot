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
    logger: ILogger<TelegramNotificationService>
) =
    let formatUser (u: DbUser) =
        match u.username, u.first_name with
        | un, _ when not (String.IsNullOrWhiteSpace un) -> "@" + un
        | _, fn when not (String.IsNullOrWhiteSpace fn) -> fn
        | _ -> string u.id

    let fmtCoupon (c: Coupon) =
        let v = c.value.ToString("0.##")
        let d = c.expires_at.ToString("dd.MM.yyyy")
        v, d

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
            let owner = match ownerOpt with | Some o -> o | None -> { id = coupon.owner_id; username = null; first_name = null; last_name = null; created_at = DateTime.UtcNow; updated_at = DateTime.UtcNow }
            let v, d = fmtCoupon coupon
            do! sendToGroup $"{formatUser owner} добавил купон на {v} EUR сроком {d}"
        }

    member _.CouponTaken(coupon, taker) =
        task {
            let! ownerOpt = db.GetUserById(coupon.owner_id)
            let v, d = fmtCoupon coupon
            match ownerOpt with
            | Some owner ->
                do! sendToGroup $"{formatUser taker} взял купон на {v} EUR сроком {d} от {formatUser owner}"
            | None ->
                do! sendToGroup $"{formatUser taker} взял купон на {v} EUR сроком {d}"
        }

    member _.CouponUsed(coupon, user) =
        task {
            let v, _d = fmtCoupon coupon
            do! sendToGroup $"{formatUser user} использовал купон на {v} EUR"
        }

    member _.CouponReturned(coupon, user) =
        task {
            let v, d = fmtCoupon coupon
            do! sendToGroup $"{formatUser user} вернул купон на {v} EUR (срок {d}) в общий доступ"
        }
