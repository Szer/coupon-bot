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
    gitHub: GitHubService,
    logger: ILogger<BotService>,
    time: TimeProvider
) =
    let sendText = BotHelpers.sendText botClient
    let ensureCommunityMember = BotHelpers.ensureCommunityMember membership sendText

    let handleCommunityMessage (msg: Message) =
        task {
            if msg.Chat <> null && msg.Chat.Id = botConfig.CommunityChatId then
                // Only persist regular content messages, skip Telegram service/system messages
                let isRegularContent =
                    match msg.Type with
                    | MessageType.Text
                    | MessageType.Photo
                    | MessageType.Document -> true
                    | _ -> false

                if isRegularContent then
                    // Determine sender: regular user (msg.From) or anonymous admin/channel post (msg.SenderChat)
                    let senderId =
                        if msg.From <> null && not msg.From.IsBot then Some msg.From.Id
                        elif msg.SenderChat <> null then Some msg.SenderChat.Id
                        else None
                    match senderId with
                    | None -> ()
                    | Some userId ->
                        let text =
                            if not (isNull msg.Text) then msg.Text
                            elif not (isNull msg.Caption) then msg.Caption
                            else null
                        let hasPhoto = msg.Photo <> null && msg.Photo.Length > 0
                        let hasDocument = not (isNull msg.Document)
                        let replyToId =
                            if msg.ReplyToMessage <> null then Nullable(msg.ReplyToMessage.MessageId)
                            else Nullable()
                        try
                            do! db.SaveChatMessage(msg.Chat.Id, msg.MessageId, userId, text, hasPhoto, hasDocument, replyToId)
                        with ex ->
                            logger.LogWarning(ex, "Failed to save community chat message {MessageId}", msg.MessageId)
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
                        Metrics.feedbackTotal.Add(1L)

                        // Extract feedback content
                        let feedbackText =
                            if not (isNull msg.Text) then msg.Text
                            elif not (isNull msg.Caption) then msg.Caption
                            else null
                        let hasMedia =
                            (msg.Photo <> null && msg.Photo.Length > 0)
                            || not (isNull msg.Document)
                            || not (isNull msg.Voice)
                            || not (isNull msg.Video)

                        // 1. Save feedback to database
                        let! feedbackId =
                            try db.SaveUserFeedback(user.id, feedbackText, hasMedia, msg.MessageId)
                            with ex ->
                                logger.LogError(ex, "Failed to save user feedback to database")
                                task { return 0L }

                        // 2. Forward to admins (existing behavior)
                        for adminId in botConfig.FeedbackAdminIds do
                            try
                                do! botClient.ForwardMessage(ChatId adminId, ChatId msg.Chat.Id, msg.MessageId) |> taskIgnore
                            with _ -> ()

                        // 3. Create GitHub issue (best-effort)
                        if gitHub.IsConfigured && feedbackId > 0L then
                            try
                                let displayName =
                                    match user.username with
                                    | null -> user.first_name |> Option.ofObj |> Option.defaultValue (string user.id)
                                    | uname -> uname
                                let! issueNumber = gitHub.CreateFeedbackIssue(displayName, feedbackText, hasMedia)
                                match issueNumber with
                                | Some num ->
                                    do! db.UpdateFeedbackGitHubIssue(feedbackId, num)
                                    try do! gitHub.AssignProductAgent(num)
                                    with ex -> logger.LogWarning(ex, "Failed to assign product agent to issue #{IssueNumber}", num)
                                | None -> ()
                            with ex ->
                                logger.LogWarning(ex, "Failed to create GitHub issue for feedback")

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
                    if update.Message.Chat <> null
                       && (update.Message.Chat.Type = ChatType.Group || update.Message.Chat.Type = ChatType.Supergroup)
                       && update.Message.Chat.Id = botConfig.CommunityChatId then
                        do! handleCommunityMessage update.Message
                    do! handlePrivateMessage update.Message
                else
                    ()
            with ex ->
                if not (isNull top) then
                    %top.SetStatus(ActivityStatusCode.Error)
                    %top.SetTag("error", true)
                ExceptionDispatchInfo.Throw ex
        }
