namespace CouponHubBot.Tests

open System
open Dapper
open Npgsql
open Xunit

[<CLIMutable>]
type ChatMessageRow =
    { chat_id: int64
      message_id: int
      user_id: int64
      text: string | null
      has_photo: bool
      has_document: bool
      reply_to_message_id: Nullable<int> }

type ChatMonitoringTests(fixture: DefaultCouponHubTestContainers) =

    [<Fact>]
    let ``Community text message is saved to chat_message table`` () =
        task {
            let user = Tg.user(id = 2001L, username = "chat_user", firstName = "Chat")
            let update = Tg.groupMessage("Hello community!", user, fixture.CommunityChatId)
            let msgId = update.Message.Id

            let! _ = fixture.SendUpdate(update)

            let! row =
                fixture.QuerySingleOrDefault<ChatMessageRow>(
                    "SELECT chat_id, message_id, user_id, text, has_photo, has_document, reply_to_message_id FROM chat_message WHERE message_id = @mid AND chat_id = @cid",
                    {| mid = msgId; cid = fixture.CommunityChatId |})

            Assert.NotNull(row)
            Assert.Equal(fixture.CommunityChatId, row.chat_id)
            Assert.Equal(msgId, row.message_id)
            Assert.Equal(2001L, row.user_id)
            Assert.Equal("Hello community!", row.text)
            Assert.False(row.has_photo)
            Assert.False(row.has_document)
            Assert.False(row.reply_to_message_id.HasValue)
        }

    [<Fact>]
    let ``Community photo message is saved with has_photo flag`` () =
        task {
            let user = Tg.user(id = 2002L, username = "photo_user", firstName = "Photo")
            let update = Tg.groupPhotoMessage(user, fixture.CommunityChatId, caption = "Check this out")
            let msgId = update.Message.Id

            let! _ = fixture.SendUpdate(update)

            let! row =
                fixture.QuerySingleOrDefault<ChatMessageRow>(
                    "SELECT chat_id, message_id, user_id, text, has_photo, has_document, reply_to_message_id FROM chat_message WHERE message_id = @mid AND chat_id = @cid",
                    {| mid = msgId; cid = fixture.CommunityChatId |})

            Assert.NotNull(row)
            Assert.True(row.has_photo)
            Assert.False(row.has_document)
            Assert.Equal("Check this out", row.text)
        }

    [<Fact>]
    let ``Community document message is saved with has_document flag`` () =
        task {
            let user = Tg.user(id = 2003L, username = "doc_user", firstName = "Doc")
            let update = Tg.groupDocumentMessage(user, fixture.CommunityChatId, caption = "A file")
            let msgId = update.Message.Id

            let! _ = fixture.SendUpdate(update)

            let! row =
                fixture.QuerySingleOrDefault<ChatMessageRow>(
                    "SELECT chat_id, message_id, user_id, text, has_photo, has_document, reply_to_message_id FROM chat_message WHERE message_id = @mid AND chat_id = @cid",
                    {| mid = msgId; cid = fixture.CommunityChatId |})

            Assert.NotNull(row)
            Assert.False(row.has_photo)
            Assert.True(row.has_document)
            Assert.Equal("A file", row.text)
        }

    [<Fact>]
    let ``Community message reply stores reply_to_message_id`` () =
        task {
            let user = Tg.user(id = 2004L, username = "reply_user", firstName = "Reply")
            let replyToId = 99999

            let update = Tg.groupMessage("Replying to that", user, fixture.CommunityChatId, replyToMessageId = replyToId)
            let msgId = update.Message.Id

            let! _ = fixture.SendUpdate(update)

            let! row =
                fixture.QuerySingleOrDefault<ChatMessageRow>(
                    "SELECT chat_id, message_id, user_id, text, has_photo, has_document, reply_to_message_id FROM chat_message WHERE message_id = @mid AND chat_id = @cid",
                    {| mid = msgId; cid = fixture.CommunityChatId |})

            Assert.NotNull(row)
            Assert.True(row.reply_to_message_id.HasValue)
            Assert.Equal(replyToId, row.reply_to_message_id.Value)
        }

    [<Fact>]
    let ``Messages from non-community group chats are not saved`` () =
        task {
            let user = Tg.user(id = 2005L, username = "other_user", firstName = "Other")
            let otherChatId = -999L
            let update = Tg.groupMessage("Not community", user, otherChatId)
            let msgId = update.Message.Id

            let! _ = fixture.SendUpdate(update)

            let! count =
                fixture.QuerySingle<int>(
                    "SELECT COUNT(*)::int FROM chat_message WHERE message_id = @mid AND chat_id = @cid",
                    {| mid = msgId; cid = otherChatId |})

            Assert.Equal(0, count)
        }
