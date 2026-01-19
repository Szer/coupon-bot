namespace CouponHubBot.Tests

open System.Net
open System.Net.Http
open Xunit
open Xunit.Extensions.AssemblyFixture

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
            let dm = calls |> Array.tryFind (fun c -> c.Body.Contains("\"chat_id\":111"))
            Assert.True(dm.IsSome, "Expected a DM response")
            Assert.Contains("только членам", dm.Value.Body)
        }
        
    [<Fact>]
    let ``Just a test`` () =
        task {
            let http = fixture.Bot.BaseAddress
            let newHttpClient = new HttpClient()
            newHttpClient.GetAsync("http://example.com") |> ignore
            
            Assert.NotNull(newHttpClient)
            Assert.NotNull(http)
        }

    interface IAssemblyFixture<DefaultCouponHubTestContainers>

