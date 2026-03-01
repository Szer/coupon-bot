namespace CouponHubBot.Services

open System
open System.Diagnostics
open System.Runtime.ExceptionServices
open System.Text.Json
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open CouponHubBot
open CouponHubBot.Services
open CouponHubBot.Telemetry
open CouponHubBot.Utils

type BotService(
    botClient: ITelegramBotClient,
    botConfig: BotConfiguration,
    db: DbService,
    membership: TelegramMembershipService,
    couponFlow: CouponFlowHandler,
    commandHandler: CommandHandler,
    callbackHandler: CallbackHandler,
    logger: ILogger<BotService>,
    time: TimeProvider
) =
    let sendText = BotHelpers.sendText botClient
    let ensureCommunityMember = BotHelpers.ensureCommunityMember membership sendText

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
                        let! handled = couponFlow.TryHandleWizardMessage user msg
                        handledAddFlow <- handled

                if handledAddFlow then
                    ()
                else

                do! commandHandler.Dispatch user msg
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
                    do! callbackHandler.HandleCallbackQuery update.CallbackQuery
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
