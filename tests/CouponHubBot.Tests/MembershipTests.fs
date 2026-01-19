namespace CouponHubBot.Tests

open System.Net
open System.Net.Http
open Xunit
open Xunit.Extensions.AssemblyFixture
open FakeCallHelpers

type MembershipTests(fixture: DefaultCouponHubTestContainers) =

    [<Fact>]
    let ``Non-member cannot /start`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 111L, username = "non_member")
            do! fixture.SetChatMemberStatus(user.Id, "left")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/start", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 111L "только членам",
                $"Expected DM to user 111 with 'только членам'. Got %d{calls.Length} calls")
        }

    interface IAssemblyFixture<DefaultCouponHubTestContainers>

