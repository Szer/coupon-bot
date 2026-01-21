namespace CouponHubBot.Tests

open System
open System.Threading
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums

type Tg() =
    static let mutable i = 1000L
    static let nextInt64 () = Interlocked.Increment(&i)
    static let nextInt () = nextInt64 () |> int

    static member user(?id: int64, ?username: string, ?firstName: string) =
        let u = User()
        u.Id <- defaultArg id (nextInt64 ())
        u.Username <- defaultArg username null
        u.FirstName <- defaultArg firstName "Test"
        u

    static member privateChat(?id: int64) =
        let c = Chat()
        c.Id <- defaultArg id (nextInt64 ())
        c.Type <- ChatType.Private
        c

    static member groupChat(?id: int64, ?username: string) =
        let c = Chat()
        c.Id <- defaultArg id (nextInt64 ())
        c.Username <- defaultArg username null
        c.Type <- ChatType.Supergroup
        c

    static member dmMessage(text: string, fromUser: User) =
        let chat = Tg.privateChat(id = fromUser.Id)
        let msg = Message()
        msg.Id <- nextInt ()
        msg.Text <- text
        msg.From <- fromUser
        msg.Chat <- chat
        msg.Date <- DateTime.UtcNow

        let upd = Update()
        upd.Id <- nextInt ()
        upd.Message <- msg
        upd

    static member dmPhotoWithCaption(caption: string, fromUser: User, ?fileId: string) =
        let chat = Tg.privateChat(id = fromUser.Id)
        // photo_file_id is unique in DB; default must be unique to avoid cross-test interference.
        let fid = defaultArg fileId ($"photo-{nextInt64 ()}")
        let photo = PhotoSize()
        photo.FileId <- fid
        photo.FileUniqueId <- fid + "-uid"
        photo.FileSize <- Nullable<int64>(1024L)
        photo.Width <- 10
        photo.Height <- 10

        let msg = Message()
        msg.Id <- nextInt ()
        msg.Caption <- caption
        msg.From <- fromUser
        msg.Chat <- chat
        msg.Date <- DateTime.UtcNow
        msg.Photo <- [| photo |]

        let upd = Update()
        upd.Id <- nextInt ()
        upd.Message <- msg
        upd

    /// Builds an Update with CallbackQuery (e.g. take:N or confirm_add:GUID) as if from a private chat.
    static member dmCallback(data: string, fromUser: User) =
        let chat = Tg.privateChat(id = fromUser.Id)
        let msg = Message()
        msg.Id <- nextInt ()
        msg.Chat <- chat
        msg.From <- fromUser
        msg.Date <- DateTime.UtcNow

        let cq = CallbackQuery()
        cq.Id <- Guid.NewGuid().ToString()
        cq.Data <- data
        cq.From <- fromUser
        cq.ChatInstance <- Guid.NewGuid().ToString()
        cq.Message <- msg

        let upd = Update()
        upd.Id <- nextInt ()
        upd.CallbackQuery <- cq
        upd
