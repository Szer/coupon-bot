namespace CouponHubBot.Services

open System
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open Telegram.Bot.Types.ReplyMarkups
open CouponHubBot
open CouponHubBot.Utils
open CouponHubBot.Services.BotHelpers

/// Handles individual coupon commands and dispatches text commands to the right handler.
type CommandHandler(
    botClient: ITelegramBotClient,
    db: DbService,
    botConfig: BotConfiguration,
    notifications: TelegramNotificationService,
    time: TimeProvider,
    logger: ILogger<CommandHandler>,
    flow: CouponFlowHandler
) =
    let sendText (chatId: int64) (text: string) =
        botClient.SendMessage(ChatId chatId, text) |> taskIgnore

    member _.HandleDebug(userId: int64, chatId: int64, couponId: int) =
        task {
            if botConfig.FeedbackAdminIds |> Array.contains userId then
                let! rows = db.GetCouponEventHistory(couponId)
                if rows.Length = 0 then
                    do! sendText chatId $"Нет событий для купона #{couponId}"
                else
                    let table = formatEventHistoryTable rows
                    let html = $"<pre>{table}</pre>"
                    do! botClient.SendMessage(ChatId chatId, html, parseMode = ParseMode.Html) |> taskIgnore
            // else silently ignore for non-admins
        }

    member _.HandleStart(chatId: int64) =
        sendText chatId
            "Привет! Я бот для совместного управления купонами Dunnes.\n\nКоманды:\n/add (или /a) — добавить купон\n/list (или /l) — доступные купоны\n/my (или /m) — мои купоны\n/added (или /ad) — мои добавленные\n/stats (или /s) — моя статистика\n/feedback (или /f) — фидбэк авторам\n\nДополнительно (не в меню):\n/take <id>\n/used <id>\n/return <id>\n/void <id>\n/help"

    member _.HandleHelp(chatId: int64) =
        sendText chatId
            "Команды (все в личке):\n/add (/a)\n/list (/l)\n/my (/m)\n/added (/ad)\n/stats (/s)\n/feedback (/f)\n\nДополнительно:\n/take <id> (или /take для списка)\n/used <id>\n/return <id>\n/void <id>\n/help"

    member _.HandleCoupons(chatId: int64) =
        task {
            let today = Utils.TimeZones.dublinToday time
            let todayStr = today |> formatUiDate
            let! coupons = db.GetAvailableCoupons()
            let totalStr = $"Всего доступно купонов: {coupons.Length}"
            if coupons.Length = 0 then
                do! sendText chatId $"{todayStr}\n{totalStr}\n\nСейчас нет доступных купонов."
            else
                let shown = pickCouponsForList today coupons
                let text =
                    shown
                    |> Array.indexed
                    |> Array.map (fun (i, c) -> formatAvailableCouponLine (i + 1) c)
                    |> String.concat "\n"
                do!
                    botClient.SendMessage(
                        ChatId chatId,
                        $"{todayStr}\n{totalStr}\n\nДоступные купоны:\n{text}",
                        replyMarkup = couponsKeyboard shown
                    )
                    |> taskIgnore
        }

    member _.HandleTake(taker: DbUser, chatId: int64, couponId: int) =
        task {
            match! db.TryTakeCoupon(couponId, taker.id) with
            | LimitReached ->
                do!
                    let n = botConfig.MaxTakenCoupons
                    let couponWord = Utils.RussianPlural.choose n "купона" "купонов" "купонов"
                    sendText chatId
                        $"Нельзя взять больше {n} {couponWord} одновременно. Сначала верни или отметь использованным один из купонов."
            | NotFoundOrNotAvailable ->
                do! sendText chatId $"Купон ID:{couponId} уже взят или не существует."
            | Taken coupon ->
                let d = formatUiDate coupon.expires_at
                do! botClient.SendPhoto(
                        ChatId chatId,
                        InputFileId coupon.photo_file_id,
                        caption = $"Ты взял(а) купон ID:{couponId}: {formatCouponValue coupon}, истекает {d}",
                        replyMarkup = singleTakenKeyboard coupon)
                    |> taskIgnore
        }

    member _.HandleUsed(user: DbUser, chatId: int64, couponId: int) =
        task {
            let! updated = db.MarkUsed(couponId, user.id)
            if updated then
                do! sendText chatId $"Отметил купон ID:{couponId} как использованный."
            else
                do! sendText chatId $"Не получилось отметить купон ID:{couponId}. Убедись что он взят тобой."
            return updated
        }

    member _.HandleReturn(user: DbUser, chatId: int64, couponId: int) =
        task {
            let! updated = db.ReturnToAvailable(couponId, user.id)
            if updated then
                do! sendText chatId $"Вернул купон ID:{couponId} в доступные."
            else
                do! sendText chatId $"Не получилось вернуть купон ID:{couponId}. Убедись что он взят тобой."
            return updated
        }

    member _.HandleStats(user: DbUser, chatId: int64) =
        task {
            let! added, taken, returned, used, voided = db.GetUserStats(user.id)
            do!
                sendText chatId
                    $"Статистика:\nДобавлено: {added}\nВзято: {taken}\nВозвращено: {returned}\nИспользовано: {used}\nАннулировано: {voided}"
        }

    member _.HandleMy(user: DbUser, chatId: int64) =
        task {
            let! taken = db.GetCouponsTakenBy(user.id)
            let todayStr =
                Utils.TimeZones.dublinToday time
                |> formatUiDate
            if taken.Length = 0 then
                do!
                    botClient.SendMessage(
                        ChatId chatId,
                        $"{todayStr}\n\nМои купоны:\n—",
                        replyMarkup = InlineKeyboardMarkup(seq { seq { InlineKeyboardButton.WithCallbackData("Мои добавленные", "myAdded") } })
                    )
                    |> taskIgnore
            else
                let shown = taken |> Array.truncate botConfig.MaxTakenCoupons

                // 1) Album message with photos only (1..4)
                let media =
                    shown
                    |> Array.map (fun c -> InputMediaPhoto(InputFileId c.photo_file_id) :> IAlbumInputMedia)
                    |> Seq.ofArray

                do! botClient.SendMediaGroup(ChatId chatId, media) |> taskIgnore

                // 2) Text + inline buttons
                let text =
                    shown
                    |> Array.indexed
                    |> Array.map (fun (i, c) ->
                        let n = i + 1
                        let d = formatUiDate c.expires_at
                        $"{n}. Купон ID:{c.id} на {formatCouponValue c}, до {d}")
                    |> String.concat "\n"

                let kb =
                    let couponRows =
                        shown
                        |> Array.indexed
                        |> Array.map (fun (i, c) ->
                            let humanIdx = i + 1
                            let ord = formatOrdinalShort humanIdx
                            seq {
                                InlineKeyboardButton.WithCallbackData($"Вернуть {ord}", $"return:{c.id}")
                                InlineKeyboardButton.WithCallbackData($"Использован {ord}", $"used:{c.id}")
                            })
                    let addedRow = [| seq { InlineKeyboardButton.WithCallbackData("Мои добавленные", "myAdded") } |]
                    InlineKeyboardMarkup(Array.append couponRows addedRow)

                do!
                    botClient.SendMessage(
                        ChatId chatId,
                        $"{todayStr}\n\nМои купоны:\n{text}",
                        replyMarkup = kb
                    )
                    |> taskIgnore
        }

    member _.HandleAdded(user: DbUser, chatId: int64) =
        task {
            let! allCoupons = db.GetVoidableCouponsByOwner(user.id)
            if allCoupons.Length = 0 then
                do! sendText chatId "У тебя нет активных добавленных купонов."
            else
                let maxShown = 20
                let coupons = allCoupons |> Array.truncate maxShown
                let remaining = allCoupons.Length - coupons.Length
                let text =
                    let lines =
                        coupons
                        |> Array.indexed
                        |> Array.map (fun (i, c) ->
                            let n = i + 1
                            let d = formatUiDate c.expires_at
                            let barcodeSuffix =
                                if String.IsNullOrEmpty(c.barcode_text) || c.barcode_text.Length < 4 then ""
                                else $" ···{c.barcode_text[c.barcode_text.Length - 4 ..]}"
                            let statusText =
                                match c.status with
                                | "taken" -> " (взят)"
                                | _ -> ""
                            $"{n}. {formatCouponValue c}, до {d}{barcodeSuffix}{statusText}")
                        |> String.concat "\n"
                    if remaining > 0 then
                        lines + $"\n...и ещё {remaining} купонов"
                    else
                        lines

                let kb =
                    coupons
                    |> Array.indexed
                    |> Array.map (fun (i, c) ->
                        let humanIdx = i + 1
                        let ord = formatOrdinalShort humanIdx
                        seq { InlineKeyboardButton.WithCallbackData($"Аннулировать {ord}", $"void:{c.id}") })
                    |> Seq.ofArray
                    |> InlineKeyboardMarkup

                do!
                    botClient.SendMessage(
                        ChatId chatId,
                        $"Мои добавленные купоны:\n{text}",
                        replyMarkup = kb
                    )
                    |> taskIgnore
        }

    member _.HandleVoid(user: DbUser, chatId: int64, couponId: int, isAdmin: bool, deleteMsg: bool, msgToDelete: Message option) =
        task {
            match! db.VoidCoupon(couponId, user.id, isAdmin) with
            | VoidCouponResult.NotFoundOrNotAllowed ->
                do! sendText chatId $"Не удалось аннулировать купон ID:{couponId}. Убедись, что он не истёк и не использован."
            | VoidCouponResult.Voided (coupon, takenBy) ->
                if isAdmin && coupon.owner_id <> user.id then
                    logger.LogInformation("Admin {AdminUserId} voided coupon {CouponId} owned by {OwnerId}", user.id, couponId, coupon.owner_id)
                let! notifyWarning =
                    match takenBy with
                    | Some takerId ->
                        task {
                            let! notified = notifications.NotifyTakerCouponVoided(takerId, coupon)
                            return if not notified then " (⚠️ Не удалось уведомить того, кто взял купон)" else ""
                        }
                    | None -> task { return "" }
                let confirmText = $"Купон ID:{couponId} аннулирован.{notifyWarning}"
                do! sendText chatId confirmText
                if deleteMsg then
                    match msgToDelete with
                    | Some msg ->
                        try
                            do! botClient.DeleteMessage(ChatId chatId, msg.MessageId)
                        with _ -> ()
                    | None -> ()
        }

    member _.HandleFeedback(user: DbUser, chatId: int64) =
        task {
            if botConfig.FeedbackAdminIds.Length = 0 then
                do! sendText chatId "Фидбэк пока не настроен (нет админов)."
            else
                do! db.SetPendingFeedback(user.id)
                do!
                    sendText chatId
                        "Следующее твоё сообщение в этом чате (в любом виде: текст, фото, голосовое и т.д.) я отправлю моим авторам. Если передумал — просто введи любую команду (например /help)."
        }

    /// Dispatch a text command to the appropriate handler.
    /// Returns unit; if the command is not recognized, it's ignored.
    member this.DispatchCommand(user: DbUser, msg: Message) =
        task {
            match msg.Text with
            | "/start" -> do! this.HandleStart(msg.Chat.Id)
            | "/help" -> do! this.HandleHelp(msg.Chat.Id)
            | "/list" -> do! this.HandleCoupons(msg.Chat.Id)
            | "/l" -> do! this.HandleCoupons(msg.Chat.Id)
            | "/coupons" -> do! this.HandleCoupons(msg.Chat.Id) // legacy alias
            | "/take" -> do! this.HandleCoupons(msg.Chat.Id) // legacy alias (list)
            | "/my" -> do! this.HandleMy(user, msg.Chat.Id)
            | "/m" -> do! this.HandleMy(user, msg.Chat.Id)
            | "/added" -> do! this.HandleAdded(user, msg.Chat.Id)
            | "/ad" -> do! this.HandleAdded(user, msg.Chat.Id)
            | "/stats" -> do! this.HandleStats(user, msg.Chat.Id)
            | "/s" -> do! this.HandleStats(user, msg.Chat.Id)
            | "/feedback" -> do! this.HandleFeedback(user, msg.Chat.Id)
            | "/f" -> do! this.HandleFeedback(user, msg.Chat.Id)
            | "/add" -> do! flow.HandleWizardStart(user, msg.Chat.Id)
            | "/a" -> do! flow.HandleWizardStart(user, msg.Chat.Id)
            | t when not (isNull t) && t.StartsWith("/take ") ->
                match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind parseInt with
                | Some couponId -> do! this.HandleTake(user, msg.Chat.Id, couponId)
                | None -> do! sendText msg.Chat.Id "Формат: /take <id>"
            | t when not (isNull t) && t.StartsWith("/used ") ->
                match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind parseInt with
                | Some couponId ->
                    let! _ = this.HandleUsed(user, msg.Chat.Id, couponId)
                    ()
                | None -> do! sendText msg.Chat.Id "Формат: /used <id>"
            | t when not (isNull t) && t.StartsWith("/return ") ->
                match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind parseInt with
                | Some couponId ->
                    let! _ = this.HandleReturn(user, msg.Chat.Id, couponId)
                    ()
                | None -> do! sendText msg.Chat.Id "Формат: /return <id>"
            | t when not (isNull t) && (t.StartsWith("/add ") || t.StartsWith("/a ")) ->
                do! sendText msg.Chat.Id "Для ручного добавления пришли фото с подписью: /add <discount> <min_check> <date>"
            | t when not (isNull t) && t.StartsWith("/void ") ->
                match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind parseInt with
                | Some couponId ->
                    let isAdmin = botConfig.FeedbackAdminIds |> Array.contains msg.From.Id
                    do! this.HandleVoid(user, msg.Chat.Id, couponId, isAdmin, false, None)
                | None -> do! sendText msg.Chat.Id "Формат: /void <id>"
            | t when not (isNull t) && t.StartsWith("/debug ") ->
                match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind parseInt with
                | Some couponId -> do! this.HandleDebug(msg.From.Id, msg.Chat.Id, couponId)
                | None -> ()
            | _ ->
                if msg.Photo <> null && msg.Photo.Length > 0 && not (isNull msg.Caption) && (msg.Caption.StartsWith("/add") || msg.Caption.StartsWith("/a")) then
                    do! flow.HandleAddManual(user, msg)
                else
                    logger.LogInformation("Ignoring private message")
        }
