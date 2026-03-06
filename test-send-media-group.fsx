#r "nuget: Telegram.Bot, 22.8.1"

open Telegram.Bot
open Telegram.Bot.Types

// --- FILL THESE IN ---
let token   = "6555369483:AAG5f0DQt0ISGyn4yqZDo0NABWJUqjyEOSQ"
let chatId  = 432506904L          // your DM chat id
let photoId = "AgACAgIAAxkBAAIGrmmAsXHoeMfXYWOlxebZsQ7gUnDYAAIuDmsbYnwISLIXeGuwIm8DAQADAgADeQADOAQ"
// ---------------------

let bot = TelegramBotClient(token)

let media = [|
    InputMediaPhoto(InputFileId photoId) :> IAlbumInputMedia
|]

printfn "Sending SendMediaGroup with 1 photo..."

try
    let result =
        bot.SendMediaGroup(ChatId chatId, media)
        |> Async.AwaitTask
        |> Async.RunSynchronously
    printfn "SUCCESS (unexpected): got %d messages back" result.Length
with ex ->
    printfn "ERROR (expected): %s" ex.Message
