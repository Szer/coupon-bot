namespace CouponHubBot.Services

open System
open System.Diagnostics
open System.Runtime.ExceptionServices
open System.Text.Json
open System.Text.RegularExpressions
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
    ocr: IOcrService,
    logger: ILogger<BotService>
) =
    let sendText (chatId: int64) (text: string) =
        botClient.SendMessage(ChatId chatId, text) |> taskIgnore

    let parseInt (s: string) =
        match System.Int32.TryParse(s) with
        | true, v -> Some v
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
        if DateOnly.TryParseExact(s, formats, culture, styles, &parsed) then Some parsed
        else None
        
    let tryParseValueFromOcrText (text: string) =
        if String.IsNullOrWhiteSpace text then None else
        let m = Regex.Match(text, @"(?i)(?<n>\d{1,3}(?:[.,]\d{1,2})?)\s*(€|eur)\b")
        if m.Success then
            let n = m.Groups.["n"].Value.Replace(',', '.')
            match Decimal.TryParse(n, Globalization.NumberStyles.Number, Globalization.CultureInfo.InvariantCulture) with
            | true, v -> Some v
            | _ -> None
        else None

    let tryParseAmountsFromOcrText (text: string) =
        if String.IsNullOrWhiteSpace text then [||] else
        let ms = Regex.Matches(text, @"(?i)(?<n>\d{1,3}(?:[.,]\d{1,2})?)\s*(€|eur)\b")
        ms
        |> Seq.cast<Match>
        |> Seq.choose (fun m ->
            let n = m.Groups.["n"].Value.Replace(',', '.')
            match Decimal.TryParse(n, Globalization.NumberStyles.Number, Globalization.CultureInfo.InvariantCulture) with
            | true, v -> Some v
            | _ -> None)
        |> Seq.toArray

    let formatCouponValue (c: Coupon) =
        let v = c.value.ToString("0.##")
        let mc = c.min_check.ToString("0.##")
        $"{v} EUR из {mc} EUR"

    let tryParseDateFromOcrText (text: string) =
        if String.IsNullOrWhiteSpace text then None else
        let m = Regex.Match(text, @"(?<d>\d{1,2})[./-](?<m>\d{1,2})[./-](?<y>\d{2,4})")
        if m.Success then
            let d = int m.Groups.["d"].Value
            let mo = int m.Groups.["m"].Value
            let yRaw = int m.Groups.["y"].Value
            let y = if yRaw < 100 then 2000 + yRaw else yRaw
            try Some (DateOnly(y, mo, d))
            with _ -> None
        else None

    let formatCouponLine (c: Coupon) =
        let d = c.expires_at.ToString("dd.MM.yyyy")
        $"{c.id}. {formatCouponValue c}, истекает {d}"

    let formatAvailableCouponLine (idx: int) (c: Coupon) =
        let d = c.expires_at.ToString("dd.MM.yyyy")
        $"{idx}. {formatCouponValue c}, истекает {d}"

    let couponsKeyboard (coupons: Coupon array) =
        // Show only first 5 to keep UX simple (1..5)
        coupons
        |> Array.truncate 5
        |> Array.indexed
        |> Array.map (fun (i, c) ->
            let humanIdx = i + 1
            seq { InlineKeyboardButton.WithCallbackData($"Взять {humanIdx}", $"take:{c.id}") })
        |> Seq.ofArray
        |> InlineKeyboardMarkup

    let addConfirmKeyboard (id: Guid) =
        seq { seq { InlineKeyboardButton.WithCallbackData("✅ Добавить", $"confirm_add:{id}") } }
        |> InlineKeyboardMarkup

    let myTakenKeyboard (taken: Coupon array) =
        taken
        |> Array.truncate 20
        |> Array.map (fun c ->
            seq {
                InlineKeyboardButton.WithCallbackData("Вернуть", $"return:{c.id}")
                InlineKeyboardButton.WithCallbackData("Использован", $"used:{c.id}")
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

    let ensureCommunityMember (userId: int64) (chatId: int64) =
        task {
            let! isMember = membership.IsMember(userId)
            if not isMember then
                do! sendText chatId "Бот доступен только членам сообщества. Если ты уверен что ты в чате — напиши /start ещё раз."
            return isMember
        }

    let handleStart (chatId: int64) =
        sendText chatId
            "Привет! Я бот для совместного управления купонами Dunnes.\n\nКоманды:\n/add — добавить купон\n/coupons — посмотреть доступные\n/take <id> — взять купон (или /take для списка)\n/used <id> — отметить использованным\n/return <id> — вернуть в доступные\n/my — мои купоны\n/stats — моя статистика\n/help — помощь"

    let handleHelp (chatId: int64) =
        sendText chatId
            "Команды (все в личке):\n/add\n/coupons\n/take <id> (или /take)\n/used <id>\n/return <id>\n/my\n/stats\n/help"

    let handleCoupons (chatId: int64) =
        task {
            let! coupons = db.GetAvailableCoupons()
            if coupons.Length = 0 then
                do! sendText chatId "Сейчас нет доступных купонов."
            else
                let shown = coupons |> Array.truncate 5
                let text =
                    shown
                    |> Array.indexed
                    |> Array.map (fun (i, c) -> formatAvailableCouponLine (i + 1) c)
                    |> String.concat "\n"
                do!
                    botClient.SendMessage(ChatId chatId, $"Доступные купоны:\n{text}", replyMarkup = couponsKeyboard shown)
                    |> taskIgnore
        }

    let handleTake (taker: DbUser) (chatId: int64) (couponId: int) =
        task {
            match! db.TryTakeCoupon(couponId, taker.id) with
            | NotFoundOrNotAvailable ->
                do! sendText chatId $"Купон {couponId} уже взят или не существует."
            | Taken coupon ->
                let d = coupon.expires_at.ToString("dd.MM.yyyy")
                do! botClient.SendPhoto(
                        ChatId chatId,
                        InputFileId coupon.photo_file_id,
                        caption = $"Ты взял купон {couponId}: {formatCouponValue coupon}, истекает {d}",
                        replyMarkup = singleTakenKeyboard coupon)
                    |> taskIgnore
                do! notifications.CouponTaken(coupon, taker)
        }

    let handleUsed (user: DbUser) (chatId: int64) (couponId: int) =
        task {
            let! updated = db.MarkUsed(couponId, user.id)
            if updated then
                do! sendText chatId $"Отметил купон {couponId} как использованный."
                match! db.GetCouponById(couponId) with
                | Some coupon -> do! notifications.CouponUsed(coupon, user)
                | None -> ()
            else
                do! sendText chatId $"Не получилось отметить купон {couponId}. Убедись что он взят тобой."
            return updated
        }

    let handleReturn (user: DbUser) (chatId: int64) (couponId: int) =
        task {
            let! updated = db.ReturnToAvailable(couponId, user.id)
            if updated then
                do! sendText chatId $"Вернул купон {couponId} в доступные."
                match! db.GetCouponById(couponId) with
                | Some coupon -> do! notifications.CouponReturned(coupon, user)
                | None -> ()
            else
                do! sendText chatId $"Не получилось вернуть купон {couponId}. Убедись что он взят тобой."
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
            let takenText =
                if taken.Length = 0 then "—"
                else taken |> Array.truncate 20 |> Array.map formatCouponLine |> String.concat "\n"
            if taken.Length = 0 then
                do! sendText chatId $"Мои взятые:\n{takenText}"
            else
                do!
                    botClient.SendMessage(ChatId chatId, $"Мои взятые:\n{takenText}", replyMarkup = myTakenKeyboard taken)
                    |> taskIgnore
        }

    let handleAdd (user: DbUser) (msg: Message) =
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
                do! sendText chatId "Пришли фото купона с подписью:\n/add <discount> <min_check> <date>\nНапример: /add 10 50 25.01.2026"
            elif parts.Length = 1 && parts[0] = "/add" then
                // OCR-assisted flow: attempt to prefill value/date
                if not botConfig.OcrEnabled then
                    do! sendText chatId "Используй подпись вида: /add <discount> <min_check> <date>\nНапример: /add 10 50 25.01.2026"
                else
                    let candidatePhotos =
                        msg.Photo
                        |> Array.filter (fun p ->
                            let size = if p.FileSize.HasValue then int64 p.FileSize.Value else 0L
                            size = 0L || size <= botConfig.OcrMaxFileSizeBytes)

                    if candidatePhotos.Length = 0 then
                        do! sendText chatId $"Фото слишком большое (лимит {botConfig.OcrMaxFileSizeBytes/(1024L*1024L)} MBs). Используй ручной ввод: /add <discount> <min_check> <date>"
                    else
                        let largestPhoto =
                            candidatePhotos
                            |> Array.maxBy (fun p -> if p.FileSize.HasValue then p.FileSize.Value else 0)
                        let! file = botClient.GetFile(largestPhoto.FileId)
                        if String.IsNullOrWhiteSpace(file.FilePath) then
                            do! sendText chatId "Не смог получить путь файла в Telegram. Попробуй ещё раз."
                        else
                            let apiBase = if isNull botConfig.TelegramApiBaseUrl then "https://api.telegram.org" else botConfig.TelegramApiBaseUrl
                            let fileUrl = $"{apiBase}/file/bot{botConfig.BotToken}/{file.FilePath}"
                            let! ocrText = ocr.TextFromImageUrl(fileUrl)
                            let amounts = tryParseAmountsFromOcrText ocrText
                            let valueOpt = if amounts.Length > 0 then Some (amounts |> Array.min) else None
                            let minCheckOpt = if amounts.Length >= 2 then Some (amounts |> Array.max) else None
                            let dateOpt = tryParseDateFromOcrText ocrText
                            match valueOpt, minCheckOpt, dateOpt with
                            | Some value, Some minCheck, Some expiresAt ->
                                let id = Guid.NewGuid()
                                do! db.CreatePendingAdd(id, user.id, largestPhoto.FileId, value, minCheck, expiresAt)
                                let v = value.ToString("0.##")
                                let mc = minCheck.ToString("0.##")
                                let d = expiresAt.ToString("dd.MM.yyyy")
                                do!
                                    botClient.SendMessage(
                                        ChatId chatId,
                                        $"Я распознал: {v} EUR из {mc} EUR, истекает {d}. Подтвердить добавление?",
                                        replyMarkup = addConfirmKeyboard id)
                                    |> taskIgnore
                            | _ ->
                                do! sendText chatId "Я не смог распознать discount/min_check и/или дату. Используй ручной ввод: фото с подписью /add <discount> <min_check> <date>"
            elif parts.Length >= 4 && parts[0] = "/add" then
                let valueOpt =
                    match System.Decimal.TryParse(parts[1], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture) with
                    | true, v -> Some v
                    | _ -> None
                let minCheckOpt =
                    match System.Decimal.TryParse(parts[2], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture) with
                    | true, v -> Some v
                    | _ -> None
                let dateOpt = tryParseDateOnly parts[3]
                match valueOpt, minCheckOpt, dateOpt with
                | Some value, Some minCheck, Some expiresAt ->
                    let largestPhoto =
                        msg.Photo
                        |> Array.maxBy (fun p -> if p.FileSize.HasValue then p.FileSize.Value else 0)
                    let! coupon = db.AddCoupon(user.id, largestPhoto.FileId, value, minCheck, expiresAt, null)
                    let v = coupon.value.ToString("0.##")
                    let mc = coupon.min_check.ToString("0.##")
                    let d = coupon.expires_at.ToString("dd.MM.yyyy")
                    do! sendText chatId $"Добавил купон {coupon.id}: {v} EUR из {mc} EUR, истекает {d}"
                    do! notifications.CouponAdded(coupon)
                | _ ->
                    do! sendText chatId "Не понял discount/min_check/date. Пример: /add 10 50 2026-01-25 (или /add 10 50 25.01.2026)"
            else
                do! sendText chatId "Нужна подпись вида: /add <discount> <min_check> <date>\nНапример: /add 10 50 25.01.2026"
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

                let! user = db.UpsertUser(DbUser.ofTelegramUser cq.From)

                let isPrivateChat = cq.Message.Chat.Type = ChatType.Private
                let hasData = not (isNull cq.Data)

                if isPrivateChat && hasData && cq.Data.StartsWith("take:") then
                    let idStr = cq.Data.Substring("take:".Length)
                    match parseInt idStr with
                    | Some couponId ->
                        do! handleTake user cq.Message.Chat.Id couponId
                    | None ->
                        ()
                elif isPrivateChat && hasData && cq.Data.StartsWith("confirm_add:") then
                    let idStr = cq.Data.Substring("confirm_add:".Length)
                    match Guid.TryParse(idStr) with
                    | true, id ->
                        let! pendingOpt = db.ConsumePendingAdd id
                        match pendingOpt with
                        | Some pending ->
                            let! coupon =
                                db.AddCoupon(
                                    pending.owner_id,
                                    pending.photo_file_id,
                                    pending.value,
                                    pending.min_check,
                                    pending.expires_at,
                                    null
                                )

                            let v = coupon.value.ToString("0.##")
                            let mc = coupon.min_check.ToString("0.##")
                            let d = coupon.expires_at.ToString("dd.MM.yyyy")
                            do! sendText cq.Message.Chat.Id $"Добавил купон {coupon.id}: {v} EUR из {mc} EUR, истекает {d}"
                            do! notifications.CouponAdded(coupon)
                        | None ->
                            do! sendText cq.Message.Chat.Id "Эта операция уже устарела. Пришли /add ещё раз."
                    | _ ->
                        ()
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
            if msg.Chat <> null && msg.Chat.Type = ChatType.Private && msg.From <> null then
                let! ok = ensureCommunityMember msg.From.Id msg.Chat.Id
                if not ok then () else
                
                let! user = db.UpsertUser(DbUser.ofTelegramUser msg.From)
                match msg.Text with
                | "/start" -> do! handleStart msg.Chat.Id
                | "/help" -> do! handleHelp msg.Chat.Id
                | "/coupons" -> do! handleCoupons msg.Chat.Id
                | "/take" -> do! handleCoupons msg.Chat.Id
                | "/my" -> do! handleMy user msg.Chat.Id
                | "/stats" -> do! handleStats user msg.Chat.Id
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
                | t when not (isNull t) && t.StartsWith("/add") ->
                    do! handleAdd user msg
                | _ ->
                    if msg.Photo <> null && msg.Photo.Length > 0 && not (isNull msg.Caption) && msg.Caption.StartsWith("/add") then
                        do! handleAdd user msg
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
