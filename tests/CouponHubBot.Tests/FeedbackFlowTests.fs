namespace CouponHubBot.Tests

open System
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit
open FakeCallHelpers

[<CLIMutable>]
type UserFeedbackRow =
    { id: int64
      user_id: int64
      feedback_text: string | null
      has_media: bool
      telegram_message_id: int
      github_issue_number: Nullable<int> }

type FeedbackFlowTests(fixture: DefaultCouponHubTestContainers) =

    [<Fact>]
    let ``Feedback text is persisted in user_feedback table`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 3001L, username = "fb_persist", firstName = "FBP")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // Step 1: Send /feedback command
            let! _ = fixture.SendUpdate(Tg.dmMessage("/feedback", user))

            // Step 2: Send feedback message
            do! fixture.ClearFakeCalls()
            let feedbackUpdate = Tg.dmMessage("The bot is great but needs dark mode", user)
            let feedbackMsgId = feedbackUpdate.Message.Id
            let! _ = fixture.SendUpdate(feedbackUpdate)

            // Verify saved in database
            let! row =
                fixture.QuerySingleOrDefault<UserFeedbackRow>(
                    "SELECT id, user_id, feedback_text, has_media, telegram_message_id, github_issue_number FROM user_feedback WHERE user_id = @uid ORDER BY id DESC LIMIT 1",
                    {| uid = 3001L |})

            Assert.NotNull(row)
            Assert.Equal(3001L, row.user_id)
            Assert.Equal("The bot is great but needs dark mode", row.feedback_text)
            Assert.False(row.has_media)
            Assert.Equal(feedbackMsgId, row.telegram_message_id)
            // GitHub issue not created (no token configured in tests)
            Assert.False(row.github_issue_number.HasValue)
        }

    [<Fact>]
    let ``Feedback with photo sets has_media flag`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 3002L, username = "fb_photo", firstName = "FBPhoto")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // Step 1: Send /feedback command
            let! _ = fixture.SendUpdate(Tg.dmMessage("/feedback", user))

            // Step 2: Send photo feedback
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("Screenshot of the issue", user))

            // Verify has_media flag
            let! row =
                fixture.QuerySingleOrDefault<UserFeedbackRow>(
                    "SELECT id, user_id, feedback_text, has_media, telegram_message_id, github_issue_number FROM user_feedback WHERE user_id = @uid ORDER BY id DESC LIMIT 1",
                    {| uid = 3002L |})

            Assert.NotNull(row)
            Assert.True(row.has_media)
            Assert.Equal("Screenshot of the issue", row.feedback_text)
        }

    [<Fact>]
    let ``Feedback confirmation and admin forwarding still works`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 3003L, username = "fb_confirm", firstName = "FBConf")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmMessage("/feedback", user))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("Please add feature X", user))

            // Verify user confirmation
            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls user.Id "Спасибо",
                "Expected confirmation message to user")

            // Verify forwarded to both admins
            let! fwCalls = fixture.GetFakeCalls("forwardMessage")
            Assert.Equal(2, fwCalls.Length)
        }

    [<Fact>]
    let ``Stale pending_feedback older than 24 hours is not consumed`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 3005L, username = "fb_stale", firstName = "FBStale")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // Trigger /feedback to register the user and create a pending_feedback row.
            let! _ = fixture.SendUpdate(Tg.dmMessage("/feedback", user))

            // Back-date the pending_feedback row to 25 hours before the fixed bot time.
            let staleTime = fixture.FixedUtcNow.UtcDateTime.AddHours(-25.0)
            do! fixture.Execute(
                "UPDATE pending_feedback SET created_at = @t WHERE user_id = @uid",
                {| t = staleTime; uid = user.Id |}) :> Task

            do! fixture.ClearFakeCalls()

            // Send a non-command message — should NOT be consumed as feedback.
            let! _ = fixture.SendUpdate(Tg.dmMessage("This should not become feedback", user))

            // Verify no user_feedback row was created.
            let! count =
                fixture.QuerySingle<int>(
                    "SELECT COUNT(*)::int FROM user_feedback WHERE user_id = @uid",
                    {| uid = 3005L |})
            Assert.Equal(0, count)

            // Verify no forwardMessage was made.
            let! fwCalls = fixture.GetFakeCalls("forwardMessage")
            Assert.Equal(0, fwCalls.Length)
        }

    [<Fact>]
    let ``Multiple feedback submissions create separate records`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 3004L, username = "fb_multi", firstName = "FBMulti")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // Truncate all feedback records to ensure exact count assertion
            do! fixture.TruncateUserFeedback()

            // First feedback
            let! _ = fixture.SendUpdate(Tg.dmMessage("/feedback", user))
            let! _ = fixture.SendUpdate(Tg.dmMessage("First feedback", user))

            // Second feedback
            let! _ = fixture.SendUpdate(Tg.dmMessage("/feedback", user))
            let! _ = fixture.SendUpdate(Tg.dmMessage("Second feedback", user))

            // Verify exactly two records
            let! count =
                fixture.QuerySingle<int>(
                    "SELECT COUNT(*)::int FROM user_feedback WHERE user_id = @uid",
                    {| uid = 3004L |})

            Assert.Equal(2, count)
        }
