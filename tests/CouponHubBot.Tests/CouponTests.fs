namespace CouponHubBot.Tests

open System
open System.Net
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit
open Xunit.Extensions.AssemblyFixture

type CouponTests(fixture: DefaultCouponHubTestContainers) =

    let getLatestCouponId () =
        task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            //language=postgresql
            let sql = "SELECT id FROM coupon ORDER BY id DESC LIMIT 1"
            return! conn.QuerySingleAsync<int>(sql)
        }

    [<Fact>]
    let ``User can add coupon and group gets notification`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 200L, username = "vasya", firstName = "Вася")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 2026-01-25", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(calls |> Array.exists (fun c -> c.Body.Contains("\"chat_id\":200") && c.Body.Contains("Добавил купон")))
            Assert.True(calls |> Array.exists (fun c -> c.Body.Contains("\"chat_id\":-42") && c.Body.Contains("добавил купон")))
        }

    [<Fact>]
    let ``Taking coupon sends photo to user and notification to group`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 201L, username = "petya", firstName = "Петя")
            let taker = Tg.user(id = 202L, username = "masha", firstName = "Маша")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            // Add via bot (photo caption)
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 20 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! photoCalls = fixture.GetFakeCalls("sendPhoto")
            Assert.True(photoCalls |> Array.exists (fun c -> c.Body.Contains("\"chat_id\":202")))

            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(msgCalls |> Array.exists (fun c -> c.Body.Contains("\"chat_id\":-42") && c.Body.Contains("взял купон")))
        }

    [<Fact>]
    let ``Double take is prevented transactionally`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 301L, username = "owner", firstName = "Owner")
            let u1 = Tg.user(id = 302L, username = "u1", firstName = "U1")
            let u2 = Tg.user(id = 303L, username = "u2", firstName = "U2")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(u1.Id, "member")
            do! fixture.SetChatMemberStatus(u2.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()

            let t1 = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", u1))
            let t2 = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", u2))
            let! _ = Task.WhenAll [| t1 :> Task; t2 :> Task |]

            // Exactly one sendPhoto should be sent (one winner)
            let! photoCalls = fixture.GetFakeCalls("sendPhoto")
            Assert.Equal(1, photoCalls.Length)
        }

    interface IAssemblyFixture<DefaultCouponHubTestContainers>

