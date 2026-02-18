namespace CouponHubBot.Services

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types

/// At startup, sets the bot command list in Telegram so that /-autocomplete and menu show commands with descriptions.
type BotCommandsSetupService(botClient: ITelegramBotClient, logger: ILogger<BotCommandsSetupService>) =
    interface IHostedService with
        member _.StartAsync(_ct: CancellationToken) =
            task {
                try
                    let commands =
                        [| BotCommand(Command = "add", Description = "Добавить купон")
                           BotCommand(Command = "list", Description = "Доступные купоны")
                           BotCommand(Command = "my", Description = "Мои купоны")
                           BotCommand(Command = "added", Description = "Мои добавленные")
                           BotCommand(Command = "stats", Description = "Моя статистика")
                           BotCommand(Command = "feedback", Description = "Фидбэк авторам бота") |]
                    do! botClient.SetMyCommands(commands, scope = BotCommandScope.AllPrivateChats())
                    logger.LogInformation("Bot commands set for Telegram menu (/-autocomplete)")
                with ex ->
                    logger.LogWarning(ex, "Could not set bot commands in Telegram; menu/autocomplete may be empty")
            }
            :> Task

        member _.StopAsync(_ct: CancellationToken) = Task.CompletedTask
