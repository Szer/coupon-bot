namespace CouponHubBot.Services

open System
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open CouponHubBot
open CouponHubBot.Services
open CouponHubBot.Telemetry
open CouponHubBot.Utils

type CouponFlowHandler(
    botClient: ITelegramBotClient,
    botConfig: BotConfiguration,
    db: DbService,
    couponOcr: CouponOcrEngine,
    time: TimeProvider
) =
    let sendText = BotHelpers.sendText botClient

    let buildConfirmTextAndKeyboard (value: decimal) (minCheck: decimal) (expiresAt: DateOnly) (barcodeText: string | null) =
        let v = value.ToString("0.##")
        let mc = minCheck.ToString("0.##")
        let d = BotHelpers.formatUiDate expiresAt
        let barcodeStr =
            if String.IsNullOrWhiteSpace barcodeText then ""
            else $", штрихкод: {barcodeText}"
        let kb = BotHelpers.addWizardConfirmKeyboard ()
        let text = $"Подтвердить добавление купона: {v}€ из {mc}€, до {d}{barcodeStr}?"
        text, kb

    member _.HandleAddWizardStart (user: DbUser) (chatId: int64) =
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

    member _.HandleAddManual (user: DbUser) (msg: Message) =
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
                    BotHelpers.parseDecimalInvariant parts[1]
                let minCheckOpt =
                    BotHelpers.parseDecimalInvariant parts[2]
                let dateOpt = BotHelpers.tryParseDateOnly time parts[3]
                match valueOpt, minCheckOpt, dateOpt with
                | Some value, Some minCheck, Some expiresAt ->
                    let largestPhoto =
                        msg.Photo
                        |> Array.maxBy (fun p -> if p.FileSize.HasValue then p.FileSize.Value else 0)
                    match! db.TryAddCoupon(user.id, largestPhoto.FileId, value, minCheck, expiresAt, null) with
                    | AddCouponResult.Added coupon ->
                        let v = coupon.value.ToString("0.##")
                        let mc = coupon.min_check.ToString("0.##")
                        let d = BotHelpers.formatUiDate coupon.expires_at
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

    member _.HandleAddWizardPhoto (user: DbUser) (chatId: int64) (photoFileId: string) =
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
                        replyMarkup = BotHelpers.addWizardDiscountKeyboard()
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
                            replyMarkup = BotHelpers.addWizardDiscountKeyboard()
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
                                replyMarkup = BotHelpers.addWizardDiscountKeyboard()
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
                            let d = BotHelpers.formatUiDate expiresAt
                            do!
                                botClient.SendMessage(
                                    ChatId chatId,
                                    $"Я распознал: {v}€ из {mc}€, до {d}, штрихкод: {barcodeText}. Всё верно?",
                                    replyMarkup = BotHelpers.addWizardOcrKeyboard()
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
                                    replyMarkup = BotHelpers.addWizardDateKeyboard()
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
                                    let d = BotHelpers.formatUiDate expiresAt
                                    $"Я распознал дату истечения {d}. Теперь выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\"."
                                | None -> "Выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\"."
                            do!
                                botClient.SendMessage(
                                    ChatId chatId,
                                    text,
                                    replyMarkup = BotHelpers.addWizardDiscountKeyboard()
                                )
                                |> taskIgnore
        }

    member _.HandleAddWizardAskDate (chatId: int64) =
        botClient.SendMessage(ChatId chatId, "Выбери дату истечения (или напиши \"25\", \"25.01.2026\", \"2026-01-25\"):", replyMarkup = BotHelpers.addWizardDateKeyboard())
        |> taskIgnore

    member _.HandleAddWizardSendConfirm (chatId: int64) (value: decimal) (minCheck: decimal) (expiresAt: DateOnly) (barcodeText: string | null) =
        let text, kb = buildConfirmTextAndKeyboard value minCheck expiresAt barcodeText
        botClient.SendMessage(ChatId chatId, text, replyMarkup = kb) |> taskIgnore

    member _.HandleAddWizardEditConfirm (chatId: int64) (messageId: int) (value: decimal) (minCheck: decimal) (expiresAt: DateOnly) (barcodeText: string | null) =
        let text, kb = buildConfirmTextAndKeyboard value minCheck expiresAt barcodeText
        task {
            try
                do! botClient.EditMessageText(ChatId chatId, messageId, text, replyMarkup = kb) |> taskIgnore
            with _ ->
                do! botClient.SendMessage(ChatId chatId, text, replyMarkup = kb) |> taskIgnore
        }

    /// Tries to advance the add wizard for a non-command message.
    /// Returns true if the message was consumed by the wizard, false otherwise.
    member this.TryHandleWizardMessage (user: DbUser) (msg: Message) =
        task {
            let! pendingFlow = db.GetPendingAddFlow(user.id)
            match pendingFlow with
            | None ->
                // UX: if no pending flow is active, a plain photo starts /add implicitly.
                // Do NOT override explicit /add manual flow via caption.
                match BotHelpers.getLargestPhotoFileId msg with
                | Some photoFileId when isNull msg.Caption || (not (msg.Caption.StartsWith("/add")) && not (msg.Caption.StartsWith("/a"))) ->
                    do! this.HandleAddWizardPhoto user msg.Chat.Id photoFileId
                    return true
                | _ -> return false
            | Some _ when pendingFlow.Value.stage = "awaiting_photo" ->
                match BotHelpers.getLargestPhotoFileId msg with
                | Some photoFileId ->
                    do! this.HandleAddWizardPhoto user msg.Chat.Id photoFileId
                    return true
                | None -> return false
            | Some flow when flow.stage = "awaiting_discount_choice" && not (isNull msg.Text) ->
                match BotHelpers.tryParseTwoDecimals msg.Text with
                | Some (v, mc) ->
                    if flow.expires_at.HasValue then
                        do! db.UpsertPendingAddFlow(
                                { flow with
                                    stage = "awaiting_confirm"
                                    value = Nullable(v)
                                    min_check = Nullable(mc)
                                    updated_at = time.GetUtcNow().UtcDateTime }
                            )
                        do! this.HandleAddWizardSendConfirm msg.Chat.Id v mc flow.expires_at.Value flow.barcode_text
                    else
                        do! db.UpsertPendingAddFlow(
                                { flow with
                                    stage = "awaiting_date_choice"
                                    value = Nullable(v)
                                    min_check = Nullable(mc)
                                    updated_at = time.GetUtcNow().UtcDateTime }
                            )
                        do! this.HandleAddWizardAskDate msg.Chat.Id
                    return true
                | None ->
                    do! sendText msg.Chat.Id "Не понял. Пришли два числа: скидка и минимальный чек. Например: 10 50 или 10/50"
                    return true
            | Some flow when flow.stage = "awaiting_date_choice" && not (isNull msg.Text) ->
                match BotHelpers.tryParseDateOnly time msg.Text with
                | Some expiresAt ->
                    if flow.value.HasValue && flow.min_check.HasValue then
                        let todayUtc = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)
                        if expiresAt < todayUtc then
                            // Don't allow past dates (today is ok).
                            do! sendText msg.Chat.Id "Эта дата уже в прошлом. Нельзя добавить истёкший купон. Пришли дату заново."
                        else
                            do! db.UpsertPendingAddFlow({ flow with stage = "awaiting_confirm"; expires_at = Nullable(expiresAt); updated_at = time.GetUtcNow().UtcDateTime })
                            do! this.HandleAddWizardSendConfirm msg.Chat.Id flow.value.Value flow.min_check.Value expiresAt flow.barcode_text
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
