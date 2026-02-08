namespace CouponHubBot.Tests

open System.Net
open Dapper
open Npgsql
open Xunit
open Xunit.Extensions.AssemblyFixture
open FakeCallHelpers

type AdminDebugTests(fixture: DefaultCouponHubTestContainers) =

    let getLatestCouponId () =
        task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            //language=postgresql
            let sql = "SELECT id FROM coupon ORDER BY id DESC LIMIT 1"
            return! conn.QuerySingleAsync<int>(sql)
        }

    [<Fact>]
    let ``Non-admin user sends debug command and gets no response`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(id = 800L, username = "regular_user", firstName = "Regular")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/debug 1", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.Equal(0, calls.Length)
        }

    [<Fact>]
    let ``Admin user sends debug command and gets formatted event history`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            // Admin user (900 is in FEEDBACK_ADMINS)
            let admin = Tg.user(id = 900L, username = "admin_debug", firstName = "Admin")
            do! fixture.SetChatMemberStatus(admin.Id, "member")

            // Add a coupon to generate an 'added' event
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", admin))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/debug {couponId}", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            let debugResponse =
                calls |> Array.tryFind (fun call ->
                    match parseCallBody call.Body with
                    | Some parsed when parsed.ChatId = Some 900L ->
                        parsed.Text.IsSome && parsed.Text.Value.Contains("<pre>")
                    | _ -> false)
            Assert.True(debugResponse.IsSome, "Admin should receive debug output with <pre> block")

            let text = (parseCallBody debugResponse.Value.Body).Value.Text.Value
            Assert.Contains("date", text)
            Assert.Contains("user", text)
            Assert.Contains("event_type", text)
            Assert.Contains("added", text)
            Assert.Contains("admin_debug", text)
        }

    interface IAssemblyFixture<DefaultCouponHubTestContainers>
