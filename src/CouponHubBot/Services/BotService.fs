namespace CouponHubBot.Services

open System
open System.Diagnostics
open System.Collections.Generic
open System.Runtime.ExceptionServices
open System.Text.Json
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open Telegram.Bot.Types.ReplyMarkups
open CouponHubBot
open CouponHubBot.Services
open CouponHubBot.Telemetry
open CouponHubBot.Utils

type BotService(
    botClient: ITelegramBotClient,
    botConfig: BotConfiguration,
    db: DbService,
    membership: TelegramMembershipService,
    notifications: TelegramNotificationService,
    couponOcr: CouponOcrEngine,
    logger: ILogger<BotService>,
    time: TimeProvider
) =
    let sendText (chatId: int64) (text: string) =
        botClient.SendMessage(ChatId chatId, text) |> taskIgnore

    /// Short Russian ordinal form used in UI: 1ый, 2ой, 3ий, 4ый, ...
    let formatOrdinalShort (n: int) =
        let suffix =
            match n with
            | 2 | 6 | 7 | 8 -> "ой"
            | 3 -> "ий"
            | _ -> "ый"
        $"{n}{suffix}"

    let parseInt (s: string) =
        match System.Int32.TryParse(s) with
        | true, v -> Some v
        | _ -> None

    let parseDecimalInvariant (s: string) =
        let s2 = s.Trim().Replace(',', '.')
        match Decimal.TryParse(s2, Globalization.NumberStyles.Number, Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    let tryParseTwoDecimals (text: string) =
        if String.IsNullOrWhiteSpace text then None
        else
            let t = text.Trim()
            // Support both "X Y" and "X/Y" formats (spaces around '/' are ok).
            if t.Contains("/") then
                let parts = t.Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)
                if parts.Length <> 2 then None
                else
                    match parseDecimalInvariant parts[0], parseDecimalInvariant parts[1] with
                    | Some a, Some b -> Some(a, b)
                    | _ -> None
            else
                let parts = t.Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                if parts.Length < 2 then None
                else
                    match parseDecimalInvariant parts[0], parseDecimalInvariant parts[1] with
                    | Some a, Some b -> Some(a, b)
                    | _ -> None

    let tryParseDateOnly (s: string) =
        let styles = System.Globalization.DateTimeStyles.None
        let culture = System.Globalization.CultureInfo.InvariantCulture
        let formats =
            [| "yyyy-MM-dd"
               "yyyy.MM.dd"
               "yyyy/MM/dd"
               "dd.MM.yyyy"
               "d.M.yyyy"
               "dd/MM/yyyy"
               "d/M/yyyy"
               "dd-MM-yyyy"
               "d-M-yyyy" |]
        let mutable parsed = Unchecked.defaultof<DateOnly>
        let t = if isNull s then "" else s.Trim()
        if DateOnly.TryParseExact(t, formats, culture, styles, &parsed) then
            Some parsed
        else
            // Shortcut: allow user to send a single day-of-month number (1..31),
            // and interpret it as the next such day strictly in the future (UTC).
            let isDigitsOnly =
                not (String.IsNullOrWhiteSpace t) && t |> Seq.forall Char.IsDigit

            if isDigitsOnly then
                match Int32.TryParse(t) with
                | true, day when day >= 1 && day <= 31 ->
                    let today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)
                    DateUtils.nextDayOfMonthStrictlyFuture today day
                | _ -> None
            else
                None

    let formatCouponValue (c: Coupon) =
        let v = c.value.ToString("0.##")
        let mc = c.min_check.ToString("0.##")
        $"{v}€ из {mc}€"

    let formatUiDate (d: DateOnly) =
        Utils.DateFormatting.formatDateNoYearWithDow d

    let formatAvailableCouponLine (idx: int) (c: Coupon) =
        let d = formatUiDate c.expires_at
        $"{idx}. {formatCouponValue c}, до {d}"

    /// Picks up to 5 coupons for /list, ensuring at least one coupon with the minimal min_check
    /// among all available coupons (if any exist). Input is expected to be sorted by expires_at, id.
    let pickCouponsForList (coupons: Coupon array) =
        let takeCount = min 5 coupons.Length
        if takeCount <= 0 then
            [||]
        else
            let baseShown = coupons |> Array.truncate takeCount
            let minCheck = (coupons |> Array.minBy (fun c -> c.min_check)).min_check

            if baseShown |> Array.exists (fun c -> c.min_check = minCheck) then
                baseShown
            else
                let bestSmall =
                    coupons
                    |> Array.filter (fun c -> c.min_check = minCheck)
                    |> Array.minBy (fun c -> c.expires_at, c.id)

                // Replace the "least urgent" item from baseShown (the last one, since baseShown is sorted).
                let replaced =
                    if takeCount = 1 then
                        [| bestSmall |]
                    else
                        Array.append baseShown.[0 .. takeCount - 2] [| bestSmall |]

                // Keep presentation stable: still ordered by expiry (and id as a tiebreaker).
                replaced |> Array.sortBy (fun c -> c.expires_at, c.id)

    let couponsKeyboard (coupons: Coupon array) =
        // Show only first 5 to keep UX simple (1..5)
        coupons
        |> Array.truncate 5
        |> Array.indexed
        |> Array.map (fun (i, c) ->
            let humanIdx = i + 1
            seq { InlineKeyboardButton.WithCallbackData($"Взять {formatOrdinalShort humanIdx}", $"take:{c.id}") })
        |> Seq.ofArray
        |> InlineKeyboardMarkup

    let addWizardDiscountKeyboard () =
        seq {
            seq { InlineKeyboardButton.WithCallbackData("5€/25€", "addflow:disc:5:25") }
            seq { InlineKeyboardButton.WithCallbackData("10€/40€", "addflow:disc:10:40") }
            seq { InlineKeyboardButton.WithCallbackData("10€/50€", "addflow:disc:10:50") }
            seq { InlineKeyboardButton.WithCallbackData("20€/100€", "addflow:disc:20:100") }
        }
        |> InlineKeyboardMarkup

    let addWizardDateKeyboard () =
        seq {
            seq { InlineKeyboardButton.WithCallbackData("Сегодня", "addflow:date:today") }
            seq { InlineKeyboardButton.WithCallbackData("Завтра", "addflow:date:tomorrow") }
        }
        |> InlineKeyboardMarkup

    let addWizardOcrKeyboard () =
        seq {
            seq {
                InlineKeyboardButton.WithCallbackData("✅ Да, всё верно", "addflow:ocr:yes")
                InlineKeyboardButton.WithCallbackData("Нет, выбрать вручную", "addflow:ocr:no")
            }
        }
        |> InlineKeyboardMarkup

    let addWizardConfirmKeyboard () =
        seq {
            seq {
                InlineKeyboardButton.WithCallbackData("✅ Добавить", "addflow:confirm")
                InlineKeyboardButton.WithCallbackData("↩️ Отмена", "addflow:cancel")
            }
        }
        |> InlineKeyboardMarkup

    /// Keyboard for /my list (no :del). One row per coupon, with numbered actions.
    let myTakenKeyboard (taken: Coupon array) =
        taken
        |> Array.truncate 4
        |> Array.indexed
        |> Array.map (fun (i, c) ->
            let humanIdx = i + 1
            let ord = formatOrdinalShort humanIdx
            seq {
                InlineKeyboardButton.WithCallbackData($"Вернуть {ord}", $"return:{c.id}")
                InlineKeyboardButton.WithCallbackData($"Использован {ord}", $"used:{c.id}")
            })
        |> Seq.ofArray
        |> InlineKeyboardMarkup

    /// Клавиатура для сообщения «Ты взял купон»: при успешном used/return сообщение удаляем.
    let singleTakenKeyboard (c: Coupon) =
        seq {
            seq {
                InlineKeyboardButton.WithCallbackData("Вернуть", $"return:{c.id}:del")
                InlineKeyboardButton.WithCallbackData("Использован", $"used:{c.id}:del")
            }
        }
        |> InlineKeyboardMarkup

    let getLargestPhotoFileId (msg: Message) =
        if isNull msg.Photo || msg.Photo.Length = 0 then None
        else
            let p = msg.Photo |> Array.maxBy (fun p -> if p.FileSize.HasValue then p.FileSize.Value else 0)
            Some p.FileId

    let ensureCommunityMember (userId: int64) (chatId: int64) =
        task {
            let! isMember = membership.IsMember(userId)
            if not isMember then
                do! sendText chatId "Бот доступен только членам сообщества. Если ты уверен что ты в чате — напиши /start ещё раз."
            return isMember
        }

    let handleStart (chatId: int64) =
        sendText chatId
            "Привет! Я бот для совместного управления купонами Dunnes.\n\nКоманды:\n/add (или /a) — добавить купон\n/list (или /l) — доступные купоны\n/my (или /m) — мои купоны\n/stats (или /s) — моя статистика\n/feedback (или /f) — фидбэк авторам\n\nДополнительно (не в меню):\n/take <id>\n/used <id>\n/return <id>\n/help"

    let handleHelp (chatId: int64) =
        sendText chatId
            "Команды (все в личке):\n/add (/a)\n/list (/l)\n/my (/m)\n/stats (/s)\n/feedback (/f)\n\nДополнительно:\n/take <id> (или /take для списка)\n/used <id>\n/return <id>\n/help"

    let handleCoupons (chatId: int64) =
        task {
            let todayStr =
                Utils.TimeZones.dublinToday time
                |> formatUiDate
            let! coupons = db.GetAvailableCoupons()
            if coupons.Length = 0 then
                do! sendText chatId $"{todayStr}\n\nСейчас нет доступных купонов."
            else
                let shown = pickCouponsForList coupons
                let text =
                    shown
                    |> Array.indexed
                    |> Array.map (fun (i, c) -> formatAvailableCouponLine (i + 1) c)
                    |> String.concat "\n"
                do!
                    botClient.SendMessage(
                        ChatId chatId,
                        $"{todayStr}\n\nДоступные купоны:\n{text}",
                        replyMarkup = couponsKeyboard shown
                    )
                    |> taskIgnore
        }

    let handleTake (taker: DbUser) (chatId: int64) (couponId: int) =
        task {
            match! db.TryTakeCoupon(couponId, taker.id) with
            | LimitReached ->
                do!
                    sendText chatId
                        "Нельзя взять больше 4 купонов одновременно. Сначала верни или отметь использованным один из купонов."
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

    let handleUsed (user: DbUser) (chatId: int64) (couponId: int) =
        task {
            let! updated = db.MarkUsed(couponId, user.id)
            if updated then
                do! sendText chatId $"Отметил купон ID:{couponId} как использованный."
            else
                do! sendText chatId $"Не получилось отметить купон ID:{couponId}. Убедись что он взят тобой."
            return updated
        }

    let handleReturn (user: DbUser) (chatId: int64) (couponId: int) =
        task {
            let! updated = db.ReturnToAvailable(couponId, user.id)
            if updated then
                do! sendText chatId $"Вернул купон ID:{couponId} в доступные."
            else
                do! sendText chatId $"Не получилось вернуть купон ID:{couponId}. Убедись что он взят тобой."
            return updated
        }

    let handleStats (user: DbUser) (chatId: int64) =
        task {
            let! added, taken, used = db.GetUserStats(user.id)
            do! sendText chatId $"Статистика:\nДобавлено: {added}\nВзято: {taken}\nИспользовано: {used}"
        }

    let handleMy (user: DbUser) (chatId: int64) =
        task {
            let! taken = db.GetCouponsTakenBy(user.id)
            let todayStr =
                Utils.TimeZones.dublinToday time
                |> formatUiDate
            if taken.Length = 0 then
                do! sendText chatId $"{todayStr}\n\nМои купоны:\n—"
            else
                let shown = taken |> Array.truncate 4

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

                do!
                    botClient.SendMessage(
                        ChatId chatId,
                        $"{todayStr}\n\nМои купоны:\n{text}",
                        replyMarkup = myTakenKeyboard shown
                    )
                    |> taskIgnore
        }

    let handleAddWizardStart (user: DbUser) (chatId: int64) =
        task {
            do! db.UpsertPendingAddFlow(
                    { user_id = user.id
                      stage = "awaiting_photo"
                      photo_file_id = null
                      value = Nullable()
                      min_check = Nullable()
                      expires_at = Nullable()
                      barcode_text = null
                      updated_at = time.GetUtcNow().UtcDateTime }
                )
            do! sendText chatId "Пришли фото купона (просто картинку)."
        }

    let handleAddManual (user: DbUser) (msg: Message) =
        task {
            use a = botActivity.StartActivity("handleAdd")
            %a.SetTag("userId", user.id)
            
            let chatId = msg.Chat.Id
            let caption = msg.Caption
            let hasPhoto = not (isNull msg.Photo) && msg.Photo.Length > 0
            let parts =
                if isNull caption then [||]
                else caption.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries)

            if not hasPhoto then
                do! sendText chatId "Для ручного добавления пришли фото купона с подписью:\n/add <discount> <min_check> <date>\nНапример: /add 10 50 25.01.2026 (или просто день: /add 10 50 25)"
            elif parts.Length >= 4 && (parts[0] = "/add" || parts[0] = "/a") then
                let valueOpt =
                    parseDecimalInvariant parts[1]
                let minCheckOpt =
                    parseDecimalInvariant parts[2]
                let dateOpt = tryParseDateOnly parts[3]
                match valueOpt, minCheckOpt, dateOpt with
                | Some value, Some minCheck, Some expiresAt ->
                    let largestPhoto =
                        msg.Photo
                        |> Array.maxBy (fun p -> if p.FileSize.HasValue then p.FileSize.Value else 0)
                    match! db.TryAddCoupon(user.id, largestPhoto.FileId, value, minCheck, expiresAt, null) with
                    | AddCouponResult.Added coupon ->
                        let v = coupon.value.ToString("0.##")
                        let mc = coupon.min_check.ToString("0.##")
                        let d = formatUiDate coupon.expires_at
                        do! sendText chatId $"Добавил купон ID:{coupon.id}: {v}€ из {mc}€, до {d}"
                    | AddCouponResult.Expired ->
                        do! sendText chatId "Нельзя добавить истёкший купон (дата в прошлом). Проверь дату и попробуй ещё раз."
                    | AddCouponResult.DuplicatePhoto existingId ->
                        do! sendText chatId $"Похоже, этот купон уже был добавлен ранее (та же фотография). Уже есть купон ID:{existingId}."
                    | AddCouponResult.DuplicateBarcode existingId ->
                        do! sendText chatId $"Купон с таким штрихкодом уже есть в базе и ещё не истёк. Уже есть купон ID:{existingId}."
                | _ ->
                    do! sendText chatId "Не понял discount/min_check/date. Примеры: /add 10 50 2026-01-25 (или /add 10 50 25.01.2026, или /add 10 50 25)"
            else
                do! sendText chatId "Нужна подпись вида: /add <discount> <min_check> <date>\nНапример: /add 10 50 25.01.2026"
        }

    let handleAddWizardPhoto (user: DbUser) (chatId: int64) (photoFileId: string) =
        task {
            do! db.UpsertPendingAddFlow(
                    { user_id = user.id
                      stage = "awaiting_discount_choice"
                      photo_file_id = photoFileId
                      value = Nullable()
                      min_check = Nullable()
                      expires_at = Nullable()
                      barcode_text = null
                      updated_at = time.GetUtcNow().UtcDateTime }
                )

            if not botConfig.OcrEnabled then
                do!
                    botClient.SendMessage(
                        ChatId chatId,
                        "Выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\".",
                        replyMarkup = addWizardDiscountKeyboard()
                    )
                    |> taskIgnore
            else
                // Attempt OCR prefill (optional). Download photo into memory, then run OCR engine.
                let! file = botClient.GetFile(photoFileId)
                if String.IsNullOrWhiteSpace(file.FilePath) then
                    do!
                        botClient.SendMessage(
                            ChatId chatId,
                            "Выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\".",
                            replyMarkup = addWizardDiscountKeyboard()
                        )
                        |> taskIgnore
                else
                    use ms = new System.IO.MemoryStream()
                    do! botClient.DownloadFile(file.FilePath, ms)
                    let bytes = ms.ToArray()

                    if int64 bytes.Length > botConfig.OcrMaxFileSizeBytes then
                        do!
                            botClient.SendMessage(
                                ChatId chatId,
                                "Картинка слишком большая для распознавания. Выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\".",
                                replyMarkup = addWizardDiscountKeyboard()
                            )
                            |> taskIgnore
                    else
                        let! ocr = couponOcr.Recognize(ReadOnlyMemory<byte>(bytes))

                        let valueOpt =
                            if ocr.couponValue.HasValue then
                                Some ocr.couponValue.Value
                            else None
                        let minCheckOpt =
                            if ocr.minCheck.HasValue then
                                Some ocr.minCheck.Value
                            else None
                        let validToOpt =
                            if ocr.validTo.HasValue then
                                Some (DateOnly.FromDateTime(ocr.validTo.Value))
                            else None
                        let barcodeText =
                            if String.IsNullOrWhiteSpace ocr.barcode then null else ocr.barcode

                        // Persist whatever we managed to recognize, and continue the wizard from the first missing step.
                        match valueOpt, minCheckOpt, validToOpt with
                        | Some value, Some minCheck, Some expiresAt ->
                            do! db.UpsertPendingAddFlow(
                                    { user_id = user.id
                                      stage = "awaiting_ocr_confirm"
                                      photo_file_id = photoFileId
                                      value = Nullable(value)
                                      min_check = Nullable(minCheck)
                                      expires_at = Nullable(expiresAt)
                                      barcode_text = barcodeText
                                      updated_at = time.GetUtcNow().UtcDateTime }
                                )
                            let v = value.ToString("0.##")
                            let mc = minCheck.ToString("0.##")
                            let d = formatUiDate expiresAt
                            do!
                                botClient.SendMessage(
                                    ChatId chatId,
                                    $"Я распознал: {v}€ из {mc}€, до {d}. Всё верно?",
                                    replyMarkup = addWizardOcrKeyboard()
                                )
                                |> taskIgnore
                        | Some value, Some minCheck, None ->
                            do! db.UpsertPendingAddFlow(
                                    { user_id = user.id
                                      stage = "awaiting_date_choice"
                                      photo_file_id = photoFileId
                                      value = Nullable(value)
                                      min_check = Nullable(minCheck)
                                      expires_at = Nullable()
                                      barcode_text = barcodeText
                                      updated_at = time.GetUtcNow().UtcDateTime }
                                )
                            let v = value.ToString("0.##")
                            let mc = minCheck.ToString("0.##")
                            do!
                                botClient.SendMessage(
                                    ChatId chatId,
                                    $"Я распознал скидку: {v}€ из {mc}€. Теперь выбери дату истечения (или напиши \"25\", \"25.01.2026\", \"2026-01-25\"):",
                                    replyMarkup = addWizardDateKeyboard()
                                )
                                |> taskIgnore
                        | _ ->
                            do! db.UpsertPendingAddFlow(
                                    { user_id = user.id
                                      stage = "awaiting_discount_choice"
                                      photo_file_id = photoFileId
                                      value = Nullable()
                                      min_check = Nullable()
                                      expires_at =
                                        match validToOpt with
                                        | Some d -> Nullable(d)
                                        | None -> Nullable()
                                      barcode_text = barcodeText
                                      updated_at = time.GetUtcNow().UtcDateTime }
                                )
                            let text =
                                match validToOpt with
                                | Some expiresAt ->
                                    let d = formatUiDate expiresAt
                                    $"Я распознал дату истечения {d}. Теперь выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\"."
                                | None -> "Выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\"."
                            do!
                                botClient.SendMessage(
                                    ChatId chatId,
                                    text,
                                    replyMarkup = addWizardDiscountKeyboard()
                                )
                                |> taskIgnore
        }

    let handleAddWizardAskDate (chatId: int64) =
        botClient.SendMessage(ChatId chatId, "Выбери дату истечения (или напиши \"25\", \"25.01.2026\", \"2026-01-25\"):", replyMarkup = addWizardDateKeyboard())
        |> taskIgnore

    let handleAddWizardSendConfirm (chatId: int64) (value: decimal) (minCheck: decimal) (expiresAt: DateOnly) =
        let v = value.ToString("0.##")
        let mc = minCheck.ToString("0.##")
        let d = formatUiDate expiresAt
        botClient.SendMessage(
            ChatId chatId,
            $"Подтвердить добавление купона: {v}€ из {mc}€, до {d}?",
            replyMarkup = addWizardConfirmKeyboard()
        )
        |> taskIgnore

    let handleFeedback (user: DbUser) (chatId: int64) =
        task {
            if botConfig.FeedbackAdminIds.Length = 0 then
                do! sendText chatId "Фидбэк пока не настроен (нет админов)."
            else
                do! db.SetPendingFeedback(user.id)
                do!
                    sendText chatId
                        "Следующее твоё сообщение в этом чате (в любом виде: текст, фото, голосовое и т.д.) я отправлю моим авторам. Если передумал — просто введи любую команду (например /help)."
        }

    let handleCallbackQuery (cq: CallbackQuery) =
        task {
            use a = botActivity.StartActivity("handleCallbackQuery")
            %a.SetTag("callbackQueryId", cq.Id)
            if not (isNull a) then %a.SetTag("callbackData", cq.Data)
            if cq.Message <> null && cq.From <> null then
                %a.SetTag("chatId", cq.Message.Chat.Id)
                %a.SetTag("fromId", cq.From.Id)
                let! ok = ensureCommunityMember cq.From.Id cq.Message.Chat.Id
                if not ok then () else

                
                
                let! user =
                    { id = cq.From.Id
                      username = cq.From.Username
                      first_name = cq.From.FirstName
                      last_name = cq.From.LastName
                      created_at = time.GetUtcNow().UtcDateTime
                      updated_at = time.GetUtcNow().UtcDateTime }
                    |> db.UpsertUser

                let isPrivateChat = cq.Message.Chat.Type = ChatType.Private
                let hasData = not (isNull cq.Data)

                if isPrivateChat && hasData && cq.Data.StartsWith("take:") then
                    Metrics.buttonClickTotal.Add(1L, KeyValuePair("button", box "take"))
                    let idStr = cq.Data.Substring("take:".Length)
                    match parseInt idStr with
                    | Some couponId ->
                        do! handleTake user cq.Message.Chat.Id couponId
                    | None ->
                        ()
                elif isPrivateChat && hasData && cq.Data.StartsWith("addflow:") then
                    Metrics.buttonClickTotal.Add(1L, KeyValuePair("button", box cq.Data))
                    match! db.GetPendingAddFlow user.id with
                    | None ->
                        do! sendText cq.Message.Chat.Id "Этот шаг добавления уже устарел. Начни заново: /add"
                    | Some flow ->
                        match cq.Data with
                        | d when d.StartsWith("addflow:disc:") ->
                            // addflow:disc:<value>:<min_check>
                            let parts = d.Split(':', StringSplitOptions.RemoveEmptyEntries)
                            if parts.Length >= 4 then
                                match parseDecimalInvariant parts[2], parseDecimalInvariant parts[3] with
                                | Some v, Some mc ->
                                    if flow.expires_at.HasValue then
                                        let next =
                                            { flow with
                                                stage = "awaiting_confirm"
                                                value = Nullable(v)
                                                min_check = Nullable(mc)
                                                updated_at = time.GetUtcNow().UtcDateTime }
                                        do! db.UpsertPendingAddFlow next
                                        do! handleAddWizardSendConfirm cq.Message.Chat.Id v mc flow.expires_at.Value
                                    else
                                        let next =
                                            { flow with
                                                stage = "awaiting_date_choice"
                                                value = Nullable(v)
                                                min_check = Nullable(mc)
                                                updated_at = time.GetUtcNow().UtcDateTime }
                                        do! db.UpsertPendingAddFlow next
                                        do! handleAddWizardAskDate cq.Message.Chat.Id
                                | _ ->
                                    do! sendText cq.Message.Chat.Id "Не понял значения. Попробуй ещё раз: /add"
                            else
                                do! sendText cq.Message.Chat.Id "Не понял значения. Попробуй ещё раз: /add"
                        | "addflow:date:today" ->
                            if flow.value.HasValue && flow.min_check.HasValue then
                                let expiresAt = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)
                                let next =
                                    { flow with
                                        stage = "awaiting_confirm"
                                        expires_at = Nullable(expiresAt)
                                        updated_at = time.GetUtcNow().UtcDateTime }
                                do! db.UpsertPendingAddFlow next
                                do! handleAddWizardSendConfirm cq.Message.Chat.Id flow.value.Value flow.min_check.Value expiresAt
                            else
                                do! sendText cq.Message.Chat.Id "Сначала выбери скидку. Начни заново: /add"
                        | "addflow:date:tomorrow" ->
                            if flow.value.HasValue && flow.min_check.HasValue then
                                let expiresAt = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddDays(1.0))
                                let next =
                                    { flow with
                                        stage = "awaiting_confirm"
                                        expires_at = Nullable(expiresAt)
                                        updated_at = time.GetUtcNow().UtcDateTime }
                                do! db.UpsertPendingAddFlow next
                                do! handleAddWizardSendConfirm cq.Message.Chat.Id flow.value.Value flow.min_check.Value expiresAt
                            else
                                do! sendText cq.Message.Chat.Id "Сначала выбери скидку. Начни заново: /add"
                        | "addflow:ocr:yes" ->
                            // If OCR fully recognized and user confirms, add immediately (no extra confirm screen).
                            if
                                flow.stage = "awaiting_ocr_confirm"
                                && flow.photo_file_id <> null
                                && flow.value.HasValue
                                && flow.min_check.HasValue
                                && flow.expires_at.HasValue
                            then
                                match!
                                    db.TryAddCoupon(
                                        user.id,
                                        flow.photo_file_id,
                                        flow.value.Value,
                                        flow.min_check.Value,
                                        flow.expires_at.Value,
                                        flow.barcode_text
                                    )
                                with
                                | AddCouponResult.Added coupon ->
                                    do! db.ClearPendingAddFlow user.id
                                    let v = coupon.value.ToString("0.##")
                                    let mc = coupon.min_check.ToString("0.##")
                                    let d = formatUiDate coupon.expires_at
                                    do! sendText cq.Message.Chat.Id $"Добавил купон ID:{coupon.id}: {v}€ из {mc}€, до {d}"
                                | AddCouponResult.Expired ->
                                    do! db.ClearPendingAddFlow user.id
                                    do! sendText cq.Message.Chat.Id "Нельзя добавить истёкший купон (дата в прошлом). Начни заново: /add"
                                | AddCouponResult.DuplicatePhoto existingId ->
                                    do! db.ClearPendingAddFlow user.id
                                    do! sendText cq.Message.Chat.Id $"Похоже, этот купон уже был добавлен ранее (та же фотография). Уже есть купон ID:{existingId}. Начни заново: /add"
                                | AddCouponResult.DuplicateBarcode existingId ->
                                    do! db.ClearPendingAddFlow user.id
                                    do! sendText cq.Message.Chat.Id $"Купон с таким штрихкодом уже есть в базе и ещё не истёк. Уже есть купон ID:{existingId}. Начни заново: /add"
                            else
                                do! sendText cq.Message.Chat.Id "Этот шаг уже неактуален. Начни заново: /add"
                        | "addflow:ocr:no" ->
                            // Clear OCR suggestion and continue manually
                            let next =
                                { flow with
                                    stage = "awaiting_discount_choice"
                                    value = Nullable()
                                    min_check = Nullable()
                                    expires_at = Nullable()
                                    barcode_text = null
                                    updated_at = time.GetUtcNow().UtcDateTime }
                            do! db.UpsertPendingAddFlow next
                            do!
                                botClient.SendMessage(
                                    ChatId cq.Message.Chat.Id,
                                    "Ок, выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\".",
                                    replyMarkup = addWizardDiscountKeyboard()
                                )
                                |> taskIgnore
                        | "addflow:confirm" ->
                            if flow.photo_file_id <> null && flow.value.HasValue && flow.min_check.HasValue && flow.expires_at.HasValue then
                                match!
                                    db.TryAddCoupon(
                                        user.id,
                                        flow.photo_file_id,
                                        flow.value.Value,
                                        flow.min_check.Value,
                                        flow.expires_at.Value,
                                        flow.barcode_text
                                    )
                                with
                                | AddCouponResult.Added coupon ->
                                    do! db.ClearPendingAddFlow user.id
                                    let v = coupon.value.ToString("0.##")
                                    let mc = coupon.min_check.ToString("0.##")
                                    let d = formatUiDate coupon.expires_at
                                    do! sendText cq.Message.Chat.Id $"Добавил купон ID:{coupon.id}: {v}€ из {mc}€, до {d}"
                                | AddCouponResult.Expired ->
                                    do! db.ClearPendingAddFlow user.id
                                    do! sendText cq.Message.Chat.Id "Нельзя добавить истёкший купон (дата в прошлом). Начни заново: /add"
                                | AddCouponResult.DuplicatePhoto existingId ->
                                    do! db.ClearPendingAddFlow user.id
                                    do! sendText cq.Message.Chat.Id $"Похоже, этот купон уже был добавлен ранее (та же фотография). Уже есть купон ID:{existingId}. Начни заново: /add"
                                | AddCouponResult.DuplicateBarcode existingId ->
                                    do! db.ClearPendingAddFlow user.id
                                    do! sendText cq.Message.Chat.Id $"Купон с таким штрихкодом уже есть в базе и ещё не истёк. Уже есть купон ID:{existingId}. Начни заново: /add"
                            else
                                do! sendText cq.Message.Chat.Id "Не хватает данных для добавления. Начни заново: /add"
                        | "addflow:cancel" ->
                            do! db.ClearPendingAddFlow user.id
                            do! sendText cq.Message.Chat.Id "Ок, отменил добавление купона."
                        | _ ->
                            do! sendText cq.Message.Chat.Id "Не понял действие. Начни заново: /add"
                elif isPrivateChat && hasData && cq.Data.StartsWith("return:") then
                    let deleteOnSuccess = cq.Data.EndsWith(":del")
                    let baseData = if deleteOnSuccess then cq.Data.Substring(0, cq.Data.Length - 4) else cq.Data
                    let idStr = baseData.Substring("return:".Length)
                    match parseInt idStr with
                    | Some couponId ->
                        let! ok = handleReturn user cq.Message.Chat.Id couponId
                        if ok && deleteOnSuccess && cq.Message <> null then
                            try
                                do! botClient.DeleteMessage(ChatId cq.Message.Chat.Id, cq.Message.MessageId)
                            with _ -> ()
                    | None -> ()
                elif isPrivateChat && hasData && cq.Data.StartsWith("used:") then
                    let deleteOnSuccess = cq.Data.EndsWith(":del")
                    let baseData = if deleteOnSuccess then cq.Data.Substring(0, cq.Data.Length - 4) else cq.Data
                    let idStr = baseData.Substring("used:".Length)
                    match parseInt idStr with
                    | Some couponId ->
                        let! ok = handleUsed user cq.Message.Chat.Id couponId
                        if ok && deleteOnSuccess && cq.Message <> null then
                            try
                                do! botClient.DeleteMessage(ChatId cq.Message.Chat.Id, cq.Message.MessageId)
                            with _ -> ()
                    | None -> ()

            do! botClient.AnswerCallbackQuery(cq.Id)
        }

    let handlePrivateMessage (msg: Message) =
        task {
            use a =
                botActivity
                    .StartActivity("handlePrivateMessage")

            if msg.Chat <> null && msg.Chat.Type = ChatType.Private && msg.From <> null then
                %a.SetTag("fromId", msg.From.Id)
                %a.SetTag("text", msg.Text)
                let! ok = ensureCommunityMember msg.From.Id msg.Chat.Id
                if not ok then %a.SetTag("isMember", false) else
                
                %a.SetTag("isMember", true)
                let! user =
                    { id = msg.From.Id
                      username = msg.From.Username
                      first_name = msg.From.FirstName
                      last_name = msg.From.LastName
                      created_at = time.GetUtcNow().UtcDateTime
                      updated_at = time.GetUtcNow().UtcDateTime }
                    |> db.UpsertUser

                // Pending /feedback: next non-command message is forwarded to admins.
                let isCommand =
                    not (isNull msg.Text)
                    && msg.Text.StartsWith("/")

                if isCommand then
                    // Any command cancels pending feedback (if present)
                    do! db.ClearPendingFeedback(user.id)

                    // Any command except /add cancels add wizard (if present)
                    if msg.Text <> "/add" && msg.Text <> "/a" then
                        do! db.ClearPendingAddFlow(user.id)

                // Handle add wizard steps for non-command messages (photo / free-form inputs).
                // Important: if /feedback consumes this message, do NOT run /add implicit flow.
                let mutable handledAddFlow = false
                if not isCommand then
                    let! feedbackConsumed = db.TryConsumePendingFeedback(user.id)
                    if feedbackConsumed then
                        for adminId in botConfig.FeedbackAdminIds do
                            try
                                do! botClient.ForwardMessage(ChatId adminId, ChatId msg.Chat.Id, msg.MessageId) |> taskIgnore
                            with _ -> ()
                        do! sendText msg.Chat.Id "Спасибо! Сообщение отправлено авторам."
                        handledAddFlow <- true
                    else
                        let! pendingFlow = db.GetPendingAddFlow(user.id)
                        match pendingFlow with
                        | None ->
                            // UX: if no pending flow is active, a plain photo starts /add implicitly.
                            // Do NOT override explicit /add manual flow via caption.
                            match getLargestPhotoFileId msg with
                            | Some photoFileId when isNull msg.Caption || (not (msg.Caption.StartsWith("/add")) && not (msg.Caption.StartsWith("/a"))) ->
                                handledAddFlow <- true
                                do! handleAddWizardPhoto user msg.Chat.Id photoFileId
                            | _ -> ()
                        | Some flow when flow.stage = "awaiting_photo" ->
                            match getLargestPhotoFileId msg with
                            | Some photoFileId ->
                                handledAddFlow <- true
                                do! handleAddWizardPhoto user msg.Chat.Id photoFileId
                            | None -> ()
                        | Some flow when flow.stage = "awaiting_discount_choice" && not (isNull msg.Text) ->
                            match tryParseTwoDecimals msg.Text with
                            | Some (v, mc) ->
                                handledAddFlow <- true
                                if flow.expires_at.HasValue then
                                    do! db.UpsertPendingAddFlow(
                                            { flow with
                                                stage = "awaiting_confirm"
                                                value = Nullable(v)
                                                min_check = Nullable(mc)
                                                updated_at = time.GetUtcNow().UtcDateTime }
                                        )
                                    do! handleAddWizardSendConfirm msg.Chat.Id v mc flow.expires_at.Value
                                else
                                    do! db.UpsertPendingAddFlow(
                                            { flow with
                                                stage = "awaiting_date_choice"
                                                value = Nullable(v)
                                                min_check = Nullable(mc)
                                                updated_at = time.GetUtcNow().UtcDateTime }
                                        )
                                    do! handleAddWizardAskDate msg.Chat.Id
                            | None ->
                                handledAddFlow <- true
                                do! sendText msg.Chat.Id "Не понял. Пришли два числа: скидка и минимальный чек. Например: 10 50 или 10/50"
                        | Some flow when flow.stage = "awaiting_date_choice" && not (isNull msg.Text) ->
                            match tryParseDateOnly msg.Text with
                            | Some expiresAt ->
                                if flow.value.HasValue && flow.min_check.HasValue then
                                    handledAddFlow <- true
                                    let todayUtc = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)
                                    if expiresAt < todayUtc then
                                        // Don't allow past dates (today is ok).
                                        do! sendText msg.Chat.Id "Эта дата уже в прошлом. Нельзя добавить истёкший купон. Пришли дату заново."
                                    else
                                        do! db.UpsertPendingAddFlow({ flow with stage = "awaiting_confirm"; expires_at = Nullable(expiresAt); updated_at = time.GetUtcNow().UtcDateTime })
                                        do! handleAddWizardSendConfirm msg.Chat.Id flow.value.Value flow.min_check.Value expiresAt
                                else
                                    handledAddFlow <- true
                                    do! sendText msg.Chat.Id "Сначала выбери скидку. Начни заново: /add"
                            | None ->
                                handledAddFlow <- true
                                do! sendText msg.Chat.Id "Не понял дату. Примеры: 25, 25.01.2026 или 2026-01-25"
                        | Some _ ->
                            // If user sends a photo at a stage where we don't expect photos (e.g. awaiting_confirm),
                            // don't silently ignore: warn how to proceed.
                            if msg.Photo <> null && msg.Photo.Length > 0 then
                                handledAddFlow <- true
                                do! sendText msg.Chat.Id "Сейчас идёт добавление купона. Закончи текущий шаг или начни заново: /add"
                            else
                                ()

                if handledAddFlow then
                    ()
                else

                match msg.Text with
                | "/start" -> do! handleStart msg.Chat.Id
                | "/help" -> do! handleHelp msg.Chat.Id
                | "/list" -> do! handleCoupons msg.Chat.Id
                | "/l" -> do! handleCoupons msg.Chat.Id
                | "/coupons" -> do! handleCoupons msg.Chat.Id // legacy alias
                | "/take" -> do! handleCoupons msg.Chat.Id // legacy alias (list)
                | "/my" -> do! handleMy user msg.Chat.Id
                | "/m" -> do! handleMy user msg.Chat.Id
                | "/stats" -> do! handleStats user msg.Chat.Id
                | "/s" -> do! handleStats user msg.Chat.Id
                | "/feedback" -> do! handleFeedback user msg.Chat.Id
                | "/f" -> do! handleFeedback user msg.Chat.Id
                | "/add" -> do! handleAddWizardStart user msg.Chat.Id
                | "/a" -> do! handleAddWizardStart user msg.Chat.Id
                | t when not (isNull t) && t.StartsWith("/take ") ->
                    match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind parseInt with
                    | Some couponId -> do! handleTake user msg.Chat.Id couponId
                    | None -> do! sendText msg.Chat.Id "Формат: /take <id>"
                | t when not (isNull t) && t.StartsWith("/used ") ->
                    match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind parseInt with
                    | Some couponId ->
                        let! _ = handleUsed user msg.Chat.Id couponId
                        ()
                    | None -> do! sendText msg.Chat.Id "Формат: /used <id>"
                | t when not (isNull t) && t.StartsWith("/return ") ->
                    match t.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast |> Option.bind parseInt with
                    | Some couponId ->
                        let! _ = handleReturn user msg.Chat.Id couponId
                        ()
                    | None -> do! sendText msg.Chat.Id "Формат: /return <id>"
                | t when not (isNull t) && (t.StartsWith("/add ") || t.StartsWith("/a ")) ->
                    do! sendText msg.Chat.Id "Для ручного добавления пришли фото с подписью: /add <discount> <min_check> <date>"
                | _ ->
                    if msg.Photo <> null && msg.Photo.Length > 0 && not (isNull msg.Caption) && (msg.Caption.StartsWith("/add") || msg.Caption.StartsWith("/a")) then
                        do! handleAddManual user msg
                    else
                        logger.LogInformation("Ignoring private message")
            else ()
        }

    member _.OnUpdate(update: Update) =
        task {
            let updateBodyJson =
                try JsonSerializer.Serialize(update, options = jsonOptions)
                with e -> e.Message
            use top =
                botActivity
                    .StartActivity("onUpdate")
                    .SetTag("updateBodyObject", update)
                    .SetTag("updateBodyJson", updateBodyJson)
                    .SetTag("updateId", update.Id)
            try
                logger.LogInformation("BotService.OnUpdate: UpdateId={UpdateId}, Message={HasMessage}, CallbackQuery={HasCallback}",
                    update.Id, not (isNull update.Message), not (isNull update.CallbackQuery))
                if isNull update then
                    ()
                elif update.ChatMember <> null then
                    membership.OnChatMemberUpdated(update.ChatMember)
                elif not (isNull update.CallbackQuery) then
                    do! handleCallbackQuery update.CallbackQuery
                elif not (isNull update.Message) then
                    do! handlePrivateMessage update.Message
                else
                    ()
            with ex ->
                if not (isNull top) then
                    %top.SetStatus(ActivityStatusCode.Error)
                    %top.SetTag("error", true)
                ExceptionDispatchInfo.Throw ex
        }
