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
                        [| BotCommand(Command = "start", Description = "Начать / приветствие")
                           BotCommand(Command = "help", Description = "Помощь по командам")
                           BotCommand(Command = "add", Description = "Добавить купон (фото + подпись)")
                           BotCommand(Command = "coupons", Description = "Доступные купоны")
                           BotCommand(Command = "take", Description = "Взять купон: /take <id> (или /take для списка)")
                           BotCommand(Command = "used", Description = "Использован: /used <id>")
                           BotCommand(Command = "return", Description = "Вернуть купон: /return <id>")
                           BotCommand(Command = "my", Description = "Мои купоны")
                           BotCommand(Command = "stats", Description = "Моя статистика") |]
                    do! botClient.SetMyCommands(commands, scope = BotCommandScope.AllPrivateChats())
                    logger.LogInformation("Bot commands set for Telegram menu (/-autocomplete)")
                with ex ->
                    logger.LogWarning(ex, "Could not set bot commands in Telegram; menu/autocomplete may be empty")
            }
            :> Task

        member _.StopAsync(_ct: CancellationToken) = Task.CompletedTask
