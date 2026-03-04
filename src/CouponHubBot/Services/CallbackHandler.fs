namespace CouponHubBot.Services

open System
open System.Collections.Generic
open Telegram.Bot
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open CouponHubBot
open CouponHubBot.Services
open CouponHubBot.Telemetry
open CouponHubBot.Utils

type CallbackHandler(
    botClient: ITelegramBotClient,
    botConfig: BotConfiguration,
    db: DbService,
    membership: TelegramMembershipService,
    couponFlow: CouponFlowHandler,
    commandHandler: CommandHandler,
    notifications: TelegramNotificationService,
    time: TimeProvider
) =
    let sendText = BotHelpers.sendText botClient
    let ensureCommunityMember = BotHelpers.ensureCommunityMember membership sendText

    member _.HandleCallbackQuery (cq: CallbackQuery) =
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
                    match BotHelpers.parseInt idStr with
                    | Some couponId ->
                        do! commandHandler.HandleTake user cq.Message.Chat.Id couponId
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
                                match BotHelpers.parseDecimalInvariant parts[2], BotHelpers.parseDecimalInvariant parts[3] with
                                | Some v, Some mc ->
                                    if flow.expires_at.HasValue then
                                        let next =
                                            { flow with
                                                stage = "awaiting_confirm"
                                                value = Nullable(v)
                                                min_check = Nullable(mc)
                                                updated_at = time.GetUtcNow().UtcDateTime }
                                        do! db.UpsertPendingAddFlow next
                                        do! couponFlow.HandleAddWizardSendConfirm cq.Message.Chat.Id v mc flow.expires_at.Value flow.barcode_text
                                    else
                                        let next =
                                            { flow with
                                                stage = "awaiting_date_choice"
                                                value = Nullable(v)
                                                min_check = Nullable(mc)
                                                updated_at = time.GetUtcNow().UtcDateTime }
                                        do! db.UpsertPendingAddFlow next
                                        do! couponFlow.HandleAddWizardAskDate cq.Message.Chat.Id
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
                                do! couponFlow.HandleAddWizardSendConfirm cq.Message.Chat.Id flow.value.Value flow.min_check.Value expiresAt flow.barcode_text
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
                                do! couponFlow.HandleAddWizardSendConfirm cq.Message.Chat.Id flow.value.Value flow.min_check.Value expiresAt flow.barcode_text
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
                                    let d = BotHelpers.formatUiDate coupon.expires_at
                                    do! sendText cq.Message.Chat.Id $"Добавил купон ID:{coupon.id}: {v}€ из {mc}€, до {d}"
                                    do! notifications.CouponAdded(coupon)
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
                                    ChatId cq.Message.Chat.Id,
                                    "Ок, выбери скидку и минимальный чек.\nИли просто напиши следующим сообщением: \"10 50\" или \"10/50\".",
                                    replyMarkup = BotHelpers.addWizardDiscountKeyboard()
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
                                    let d = BotHelpers.formatUiDate coupon.expires_at
                                    do! sendText cq.Message.Chat.Id $"Добавил купон ID:{coupon.id}: {v}€ из {mc}€, до {d}"
                                    do! notifications.CouponAdded(coupon)
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
                    match BotHelpers.parseInt idStr with
                    | Some couponId ->
                        let! ok = commandHandler.HandleReturn user cq.Message.Chat.Id couponId
                        if ok && deleteOnSuccess && cq.Message <> null then
                            try
                                do! botClient.DeleteMessage(ChatId cq.Message.Chat.Id, cq.Message.MessageId)
                            with _ -> ()
                    | None -> ()
                elif isPrivateChat && hasData && cq.Data.StartsWith("used:") then
                    let deleteOnSuccess = cq.Data.EndsWith(":del")
                    let baseData = if deleteOnSuccess then cq.Data.Substring(0, cq.Data.Length - 4) else cq.Data
                    let idStr = baseData.Substring("used:".Length)
                    match BotHelpers.parseInt idStr with
                    | Some couponId ->
                        let! ok = commandHandler.HandleUsed user cq.Message.Chat.Id couponId
                        if ok && deleteOnSuccess && cq.Message <> null then
                            try
                                do! botClient.DeleteMessage(ChatId cq.Message.Chat.Id, cq.Message.MessageId)
                            with _ -> ()
                    | None -> ()
                elif isPrivateChat && hasData && cq.Data.StartsWith("void:") then
                    let deleteOnSuccess = cq.Data.EndsWith(":del")
                    let baseData = if deleteOnSuccess then cq.Data.Substring(0, cq.Data.Length - 4) else cq.Data
                    let idStr = baseData.Substring("void:".Length)
                    match BotHelpers.parseInt idStr with
                    | Some couponId ->
                        let isAdmin = botConfig.FeedbackAdminIds |> Array.contains user.id
                        do! commandHandler.HandleVoid user cq.Message.Chat.Id couponId isAdmin deleteOnSuccess (Some cq.Message)
                    | None -> ()
                elif isPrivateChat && hasData && cq.Data = "myAdded" then
                    do! commandHandler.HandleAdded user cq.Message.Chat.Id

            do! botClient.AnswerCallbackQuery(cq.Id)
        }
