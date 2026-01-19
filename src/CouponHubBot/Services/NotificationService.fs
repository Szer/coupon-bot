namespace CouponHubBot.Services

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open CouponHubBot

type INotificationService =
    abstract member CouponAdded: coupon: Coupon -> Task
    abstract member CouponTaken: coupon: Coupon * taker: DbUser -> Task
    abstract member CouponUsed: coupon: Coupon * user: DbUser -> Task
    abstract member CouponReturned: coupon: Coupon * user: DbUser -> Task

/// Placeholder implementation. Real Telegram group notifications come in `notifications` todo.
type NoopNotificationService() =
    interface INotificationService with
        member _.CouponAdded(_coupon) = Task.CompletedTask
        member _.CouponTaken(_coupon, _taker) = Task.CompletedTask
        member _.CouponUsed(_coupon, _user) = Task.CompletedTask
        member _.CouponReturned(_coupon, _user) = Task.CompletedTask

type TelegramNotificationService(
    botClient: ITelegramBotClient,
    botConfig: BotConfiguration,
    db: IDbService,
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

    interface INotificationService with
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

