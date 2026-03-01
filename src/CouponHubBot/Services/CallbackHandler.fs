namespace CouponHubBot.Services

open System
open System.Collections.Generic
open Telegram.Bot
open Telegram.Bot.Types
open CouponHubBot
open CouponHubBot.Utils
open CouponHubBot.Telemetry
open CouponHubBot.Services.BotHelpers

/// Routes inline keyboard callback queries to the appropriate handler.
type CallbackHandler(
    botClient: ITelegramBotClient,
    db: DbService,
    botConfig: BotConfiguration,
    time: TimeProvider,
    membership: TelegramMembershipService,
    commands: CommandHandler,
    flow: CouponFlowHandler
) =
    let sendText (chatId: int64) (text: string) =
        botClient.SendMessage(ChatId chatId, text) |> taskIgnore

    let ensureCommunityMember (userId: int64) (chatId: int64) =
        task {
            let! isMember = membership.IsMember(userId)
            if not isMember then
                do! sendText chatId "Бот доступен только членам сообщества. Если ты уверен что ты в чате — напиши /start ещё раз."
            return isMember
        }

    member _.HandleCallbackQuery(cq: CallbackQuery) =
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

                let isPrivateChat = cq.Message.Chat.Type = Telegram.Bot.Types.Enums.ChatType.Private
                let hasData = not (isNull cq.Data)

                if isPrivateChat && hasData && cq.Data.StartsWith("take:") then
                    Metrics.buttonClickTotal.Add(1L, KeyValuePair("button", box "take"))
                    let idStr = cq.Data.Substring("take:".Length)
                    match parseInt idStr with
                    | Some couponId ->
                        do! commands.HandleTake(user, cq.Message.Chat.Id, couponId)
                    | None ->
                        ()
                elif isPrivateChat && hasData && cq.Data.StartsWith("addflow:") then
                    Metrics.buttonClickTotal.Add(1L, KeyValuePair("button", box cq.Data))
                    match! db.GetPendingAddFlow user.id with
                    | None ->
                        do! sendText cq.Message.Chat.Id "Этот шаг добавления уже устарел. Начни заново: /add"
                    | Some pendingFlow ->
                        do! flow.HandleAddFlowCallback(user, cq, pendingFlow)
                elif isPrivateChat && hasData && cq.Data.StartsWith("return:") then
                    let deleteOnSuccess = cq.Data.EndsWith(":del")
                    let baseData = if deleteOnSuccess then cq.Data.Substring(0, cq.Data.Length - 4) else cq.Data
                    let idStr = baseData.Substring("return:".Length)
                    match parseInt idStr with
                    | Some couponId ->
                        let! ok = commands.HandleReturn(user, cq.Message.Chat.Id, couponId)
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
                        let! ok = commands.HandleUsed(user, cq.Message.Chat.Id, couponId)
                        if ok && deleteOnSuccess && cq.Message <> null then
                            try
                                do! botClient.DeleteMessage(ChatId cq.Message.Chat.Id, cq.Message.MessageId)
                            with _ -> ()
                    | None -> ()
                elif isPrivateChat && hasData && cq.Data.StartsWith("void:") then
                    let deleteOnSuccess = cq.Data.EndsWith(":del")
                    let baseData = if deleteOnSuccess then cq.Data.Substring(0, cq.Data.Length - 4) else cq.Data
                    let idStr = baseData.Substring("void:".Length)
                    match parseInt idStr with
                    | Some couponId ->
                        let isAdmin = botConfig.FeedbackAdminIds |> Array.contains user.id
                        do! commands.HandleVoid(user, cq.Message.Chat.Id, couponId, isAdmin, deleteOnSuccess, Some cq.Message)
                    | None -> ()
                elif isPrivateChat && hasData && cq.Data = "myAdded" then
                    do! commands.HandleAdded(user, cq.Message.Chat.Id)

            do! botClient.AnswerCallbackQuery(cq.Id)
        }
