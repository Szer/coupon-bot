namespace CouponHubBot.Services

open System
open Telegram.Bot
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open CouponHubBot
open CouponHubBot.Utils
open CouponHubBot.Services.BotHelpers

/// Handles the multi-step add/OCR wizard flow for adding new coupons.
type CouponFlowHandler(
    botClient: ITelegramBotClient,
    db: DbService,
    botConfig: BotConfiguration,
    time: TimeProvider,
    couponOcr: CouponOcrEngine
) =
    let sendText (chatId: int64) (text: string) =
        botClient.SendMessage(ChatId chatId, text) |> taskIgnore

    let todayUtc () = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)

    let wizardAskDate (chatId: int64) =
        botClient.SendMessage(ChatId chatId, "Выбери дату истечения (или напиши \"25\", \"25.01.2026\", \"2026-01-25\"):", replyMarkup = addWizardDateKeyboard())
        |> taskIgnore

    let wizardSendConfirm (chatId: int64) (value: decimal) (minCheck: decimal) (expiresAt: DateOnly) (barcodeText: string | null) =
        let text, kb = buildConfirmTextAndKeyboard value minCheck expiresAt barcodeText
        botClient.SendMessage(ChatId chatId, text, replyMarkup = kb) |> taskIgnore

    let wizardEditConfirm (chatId: int64) (messageId: int) (value: decimal) (minCheck: decimal) (expiresAt: DateOnly) (barcodeText: string | null) =
        let text, kb = buildConfirmTextAndKeyboard value minCheck expiresAt barcodeText
        task {
            try
                do! botClient.EditMessageText(ChatId chatId, messageId, text, replyMarkup = kb) |> taskIgnore
            with _ ->
                do! botClient.SendMessage(ChatId chatId, text, replyMarkup = kb) |> taskIgnore
        }

    member _.HandleWizardStart(user: DbUser, chatId: int64) =
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

    member _.HandleAddManual(user: DbUser, msg: Message) =
        task {
            let chatId = msg.Chat.Id
            let caption = msg.Caption
            let hasPhoto = not (isNull msg.Photo) && msg.Photo.Length > 0
            let parts =
                if isNull caption then [||]
                else caption.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries)

            if not hasPhoto then
                do! sendText chatId "Для ручного добавления пришли фото купона с подписью:\n/add <discount> <min_check> <date>\nНапример: /add 10 50 25.01.2026 (или просто день: /add 10 50 25)"
            elif parts.Length >= 4 && (parts[0] = "/add" || parts[0] = "/a") then
                let valueOpt = parseDecimalInvariant parts[1]
                let minCheckOpt = parseDecimalInvariant parts[2]
                let dateOpt = tryParseDateOnly (todayUtc()) parts[3]
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

    member _.HandleWizardPhoto(user: DbUser, chatId: int64, photoFileId: string) =
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
                            if ocr.couponValue.HasValue then Some ocr.couponValue.Value else None
                        let minCheckOpt =
                            if ocr.minCheck.HasValue then Some ocr.minCheck.Value else None
                        let validToOpt =
                            if ocr.validTo.HasValue then Some (DateOnly.FromDateTime(ocr.validTo.Value)) else None
                        let barcodeText =
                            if String.IsNullOrWhiteSpace ocr.barcode then null else ocr.barcode

                        if isNull barcodeText then
                            // Barcode not recognized — photo quality is insufficient.
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
                            do! sendText chatId "Не удалось распознать штрихкод на фото. Пожалуйста, пришли фото в лучшем качестве или скадрируй картинку ближе к штрихкоду, дате и сумме."
                        else

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
                                    $"Я распознал: {v}€ из {mc}€, до {d}, штрихкод: {barcodeText}. Всё верно?",
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

    /// Handle an "addflow:" callback query when there is an active pending flow.
    member _.HandleAddFlowCallback(user: DbUser, cq: CallbackQuery, flow: PendingAddFlow) =
        task {
            let chatId = cq.Message.Chat.Id
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
                            do! wizardSendConfirm chatId v mc flow.expires_at.Value flow.barcode_text
                        else
                            let next =
                                { flow with
                                    stage = "awaiting_date_choice"
                                    value = Nullable(v)
                                    min_check = Nullable(mc)
                                    updated_at = time.GetUtcNow().UtcDateTime }
                            do! db.UpsertPendingAddFlow next
                            do! wizardAskDate chatId
                    | _ ->
                        do! sendText chatId "Не понял значения. Попробуй ещё раз: /add"
                else
                    do! sendText chatId "Не понял значения. Попробуй ещё раз: /add"
            | "addflow:date:today" ->
                if flow.value.HasValue && flow.min_check.HasValue then
                    let expiresAt = todayUtc()
                    let next =
                        { flow with
                            stage = "awaiting_confirm"
                            expires_at = Nullable(expiresAt)
                            updated_at = time.GetUtcNow().UtcDateTime }
                    do! db.UpsertPendingAddFlow next
                    do! wizardSendConfirm chatId flow.value.Value flow.min_check.Value expiresAt flow.barcode_text
                else
                    do! sendText chatId "Сначала выбери скидку. Начни заново: /add"
            | "addflow:date:tomorrow" ->
                if flow.value.HasValue && flow.min_check.HasValue then
                    let expiresAt = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddDays(1.0))
                    let next =
                        { flow with
                            stage = "awaiting_confirm"
                            expires_at = Nullable(expiresAt)
                            updated_at = time.GetUtcNow().UtcDateTime }
                    do! db.UpsertPendingAddFlow next
                    do! wizardSendConfirm chatId flow.value.Value flow.min_check.Value expiresAt flow.barcode_text
                else
                    do! sendText chatId "Сначала выбери скидку. Начни заново: /add"
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
                        do! sendText chatId $"Добавил купон ID:{coupon.id}: {v}€ из {mc}€, до {d}"
                    | AddCouponResult.Expired ->
                        do! db.ClearPendingAddFlow user.id
                        do! sendText chatId "Нельзя добавить истёкший купон (дата в прошлом). Начни заново: /add"
                    | AddCouponResult.DuplicatePhoto existingId ->
                        do! db.ClearPendingAddFlow user.id
                        do! sendText chatId $"Похоже, этот купон уже был добавлен ранее (та же фотография). Уже есть купон ID:{existingId}. Начни заново: /add"
                    | AddCouponResult.DuplicateBarcode existingId ->
                        do! db.ClearPendingAddFlow user.id
                        do! sendText chatId $"Купон с таким штрихкодом уже есть в базе и ещё не истёк. Уже есть купон ID:{existingId}. Начни заново: /add"
                else
                    do! sendText chatId "Этот шаг уже неактуален. Начни заново: /add"
            | "addflow:ocr:no" ->
                // Clear OCR suggestion and continue manually; keep barcode (already validated at photo upload).
                let next =
                    { flow with
                        stage = "awaiting_discount_choice"
                        value = Nullable()
                        min_check = Nullable()
                        expires_at = Nullable()
                        updated_at = time.GetUtcNow().UtcDateTime }
                do! db.UpsertPendingAddFlow next
                do!
                    botClient.SendMessage(
                        ChatId chatId,
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
                        do! sendText chatId $"Добавил купон ID:{coupon.id}: {v}€ из {mc}€, до {d}"
                    | AddCouponResult.Expired ->
                        do! db.ClearPendingAddFlow user.id
                        do! sendText chatId "Нельзя добавить истёкший купон (дата в прошлом). Начни заново: /add"
                    | AddCouponResult.DuplicatePhoto existingId ->
                        do! db.ClearPendingAddFlow user.id
                        do! sendText chatId $"Похоже, этот купон уже был добавлен ранее (та же фотография). Уже есть купон ID:{existingId}. Начни заново: /add"
                    | AddCouponResult.DuplicateBarcode existingId ->
                        do! db.ClearPendingAddFlow user.id
                        do! sendText chatId $"Купон с таким штрихкодом уже есть в базе и ещё не истёк. Уже есть купон ID:{existingId}. Начни заново: /add"
                else
                    do! sendText chatId "Не хватает данных для добавления. Начни заново: /add"
            | "addflow:cancel" ->
                do! db.ClearPendingAddFlow user.id
                do! sendText chatId "Ок, отменил добавление купона."
            | _ ->
                do! sendText chatId "Не понял действие. Начни заново: /add"
        }

    /// Handle a non-command message that may advance the active add wizard.
    /// Returns true if the message was consumed by the wizard, false otherwise.
    member this.HandleAddFlowMessage(user: DbUser, msg: Message) =
        task {
            let! pendingFlow = db.GetPendingAddFlow(user.id)
            match pendingFlow with
            | None ->
                // UX: if no pending flow is active, a plain photo starts /add implicitly.
                // Do NOT override explicit /add manual flow via caption.
                match getLargestPhotoFileId msg with
                | Some photoFileId when isNull msg.Caption || (not (msg.Caption.StartsWith("/add")) && not (msg.Caption.StartsWith("/a"))) ->
                    do! this.HandleWizardPhoto(user, msg.Chat.Id, photoFileId)
                    return true
                | _ -> return false
            | Some flow when flow.stage = "awaiting_photo" ->
                match getLargestPhotoFileId msg with
                | Some photoFileId ->
                    do! this.HandleWizardPhoto(user, msg.Chat.Id, photoFileId)
                    return true
                | None -> return false
            | Some flow when flow.stage = "awaiting_discount_choice" && not (isNull msg.Text) ->
                match tryParseTwoDecimals msg.Text with
                | Some (v, mc) ->
                    if flow.expires_at.HasValue then
                        do! db.UpsertPendingAddFlow(
                                { flow with
                                    stage = "awaiting_confirm"
                                    value = Nullable(v)
                                    min_check = Nullable(mc)
                                    updated_at = time.GetUtcNow().UtcDateTime }
                            )
                        do! wizardSendConfirm msg.Chat.Id v mc flow.expires_at.Value flow.barcode_text
                    else
                        do! db.UpsertPendingAddFlow(
                                { flow with
                                    stage = "awaiting_date_choice"
                                    value = Nullable(v)
                                    min_check = Nullable(mc)
                                    updated_at = time.GetUtcNow().UtcDateTime }
                            )
                        do! wizardAskDate msg.Chat.Id
                    return true
                | None ->
                    do! sendText msg.Chat.Id "Не понял. Пришли два числа: скидка и минимальный чек. Например: 10 50 или 10/50"
                    return true
            | Some flow when flow.stage = "awaiting_date_choice" && not (isNull msg.Text) ->
                match tryParseDateOnly (todayUtc()) msg.Text with
                | Some expiresAt ->
                    if flow.value.HasValue && flow.min_check.HasValue then
                        let todayDate = todayUtc()
                        if expiresAt < todayDate then
                            // Don't allow past dates (today is ok).
                            do! sendText msg.Chat.Id "Эта дата уже в прошлом. Нельзя добавить истёкший купон. Пришли дату заново."
                        else
                            do! db.UpsertPendingAddFlow({ flow with stage = "awaiting_confirm"; expires_at = Nullable(expiresAt); updated_at = time.GetUtcNow().UtcDateTime })
                            do! wizardSendConfirm msg.Chat.Id flow.value.Value flow.min_check.Value expiresAt flow.barcode_text
                    else
                        do! sendText msg.Chat.Id "Сначала выбери скидку. Начни заново: /add"
                    return true
                | None ->
                    do! sendText msg.Chat.Id "Не понял дату. Примеры: 25, 25.01.2026 или 2026-01-25"
                    return true
            | Some _ ->
                // If user sends a photo at a stage where we don't expect photos (e.g. awaiting_confirm),
                // don't silently ignore: warn how to proceed.
                if msg.Photo <> null && msg.Photo.Length > 0 then
                    do! sendText msg.Chat.Id "Сейчас идёт добавление купона. Закончи текущий шаг или начни заново: /add"
                    return true
                else
                    return false
        }
