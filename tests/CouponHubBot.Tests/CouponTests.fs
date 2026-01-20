namespace CouponHubBot.Tests

open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit
open Xunit.Extensions.AssemblyFixture
open FakeCallHelpers

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

            let! resp = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 200L "Добавил купон",
                $"Expected DM to user 200 with 'Добавил купон'. Got %d{calls.Length} calls")
            Assert.True(findCallWithText calls -42L "добавил(а) купон",
                $"Expected group notification to -42 with 'добавил(а) купон'. Got %d{calls.Length} calls")
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
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 20 100 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! photoCalls = fixture.GetFakeCalls("sendPhoto")
            Assert.True(photoCalls |> Array.exists (fun call ->
                match parseCallBody call.Body with
                | Some parsed -> parsed.ChatId = Some 202L
                | _ -> false),
                $"Expected sendPhoto to user 202. Got %d{photoCalls.Length} calls")

            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls -42L "взял(а) купон",
                $"Expected group notification to -42 with 'взял(а) купон'. Got %d{msgCalls.Length} calls")
        }

    [<Fact>]
    let ``Taking coupon by button sends photo to user and notification to group`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 203L, username = "owner_btn", firstName = "Owner")
            let taker = Tg.user(id = 204L, username = "taker_btn", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 15 75 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmCallback($"take:{couponId}", taker))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! photoCalls = fixture.GetFakeCalls("sendPhoto")
            Assert.True(photoCalls |> Array.exists (fun call ->
                match parseCallBody call.Body with
                | Some parsed -> parsed.ChatId = Some 204L
                | _ -> false),
                $"Expected sendPhoto to user 204. Got %d{photoCalls.Length} calls")

            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls -42L "взял(а) купон",
                $"Expected group notification to -42 with 'взял(а) купон'. Got %d{msgCalls.Length} calls")
        }

    [<Fact>]
    let ``Taking coupon sends confirmation with Вернуть and Использован buttons`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 259L, username = "take_btn_o", firstName = "O")
            let taker = Tg.user(id = 260L, username = "take_btn_t", firstName = "T")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"take:{couponId}", taker))

            let! photoCalls = fixture.GetFakeCalls("sendPhoto")
            let takenMsg =
                photoCalls
                |> Array.tryFind (fun c ->
                    match parseCallBody c.Body with
                    | Some p when p.ChatId = Some 260L ->
                        p.Caption.IsSome && p.Caption.Value.Contains("Ты взял купон")
                    | _ -> false)
            Assert.True(takenMsg.IsSome, "Expected 'Ты взял купон' photo with caption to taker")
            Assert.True(
                takenMsg.Value.Body.Contains("return:") && takenMsg.Value.Body.Contains("used:") && takenMsg.Value.Body.Contains(":del"),
                "Expected inline buttons Вернуть/Использован with :del (delete on success)")
        }

    [<Fact>]
    let ``Take non-existent or already taken coupon shows error`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 213L, username = "taker_fail")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/take 99999", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 213L "уже взят или не существует",
                $"Expected DM with 'уже взят или не существует'. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``Add without photo asks for photo`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 210L, username = "add_no_photo")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/add", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 210L "Пришли фото",
                $"Expected DM with 'Пришли фото'. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``Add with value and date as text without photo asks for photo`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 216L, username = "add_text_no_photo")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/add 15 50 2026-02-01", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 216L "Пришли фото",
                $"Expected DM with 'Пришли фото' when /add has args but no photo. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``Add with invalid value or date shows error`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 211L, username = "add_bad")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add x 50 2026-01-25", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 211L "Не понял discount/min_check/date",
                $"Expected DM with 'Не понял discount/min_check/date'. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``Add with only /add and no value in caption when OCR disabled asks for manual format`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 212L, username = "add_ocr_off")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            // OCR_ENABLED is false in test container

            let! resp = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 212L "Используй подпись вида",
                $"Expected DM with 'Используй подпись вида'. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``Confirm_add with seeded pending_add adds coupon and sends notification`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 214L, username = "confirm_owner", firstName = "ConfirmOwner")
            do! fixture.SetChatMemberStatus(owner.Id, "member")

            let pendingId = Guid.NewGuid()
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            //language=postgresql
            do! conn.ExecuteAsync("""
                INSERT INTO "user"(id, username, first_name, created_at, updated_at)
                VALUES (214, 'confirm_owner', 'ConfirmOwner', NOW(), NOW())
                ON CONFLICT (id) DO NOTHING
                """) :> Task
            //language=postgresql
            do! conn.ExecuteAsync("""
                INSERT INTO pending_add (id, owner_id, photo_file_id, value, min_check, expires_at)
                VALUES (@id, 214, 'seed-photo', 25.00, 100.00, '2026-02-15')
                """, {| id = pendingId |}) :> Task

            let! resp = fixture.SendUpdate(Tg.dmCallback($"confirm_add:{pendingId}", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 214L "Добавил купон",
                $"Expected DM with 'Добавил купон'. Got %d{calls.Length} calls")
            Assert.True(findCallWithText calls -42L "добавил(а) купон",
                $"Expected group notification with 'добавил(а) купон'. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``Confirm_add with missing or already consumed pending says outdated`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 215L, username = "confirm_outdated")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmCallback($"confirm_add:{Guid.NewGuid()}", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 215L "устарела",
                $"Expected DM with 'устарела'. Got %d{calls.Length} calls")
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

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()

            let t1 = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", u1))
            let t2 = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", u2))
            let! _ = Task.WhenAll [| t1 :> Task; t2 :> Task |]

            // Exactly one sendPhoto should be sent (one winner)
            let! photoCalls = fixture.GetFakeCalls("sendPhoto")
            Assert.Equal(1, photoCalls.Length)
        }

    [<Fact>]
    let ``Used by taker marks coupon and sends notification`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 220L, username = "used_owner", firstName = "Owner")
            let taker = Tg.user(id = 221L, username = "used_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/used {couponId}", taker))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 221L "Отметил",
                $"Expected DM with 'Отметил'. Got %d{calls.Length} calls")
            Assert.True(findCallWithText calls -42L "использовал(а)",
                $"Expected group notification with 'использовал(а)'. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``Used by non-taker fails`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 222L, username = "u_owner", firstName = "UO")
            let taker = Tg.user(id = 223L, username = "u_taker", firstName = "UT")
            let other = Tg.user(id = 224L, username = "u_other", firstName = "UOth")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")
            do! fixture.SetChatMemberStatus(other.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/used {couponId}", other))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 224L "Не получилось",
                $"Expected DM with 'Не получилось'. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``Return by taker returns coupon and sends notification`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 225L, username = "ret_owner", firstName = "RO")
            let taker = Tg.user(id = 226L, username = "ret_taker", firstName = "RT")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/return {couponId}", taker))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 226L "Вернул",
                $"Expected DM with 'Вернул'. Got %d{calls.Length} calls")
            Assert.True(findCallWithText calls -42L "вернул",
                $"Expected group notification with 'вернул'. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``Return by non-taker fails`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 227L, username = "r_owner", firstName = "RO")
            let taker = Tg.user(id = 228L, username = "r_taker", firstName = "RT")
            let other = Tg.user(id = 229L, username = "r_other", firstName = "ROth")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")
            do! fixture.SetChatMemberStatus(other.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/return {couponId}", other))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 229L "Не получилось",
                $"Expected DM with 'Не получилось'. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``Coupons when empty shows no coupons message`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 230L, username = "coupons_empty")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            
            do! fixture.TruncateCoupons()

            let! resp = fixture.SendUpdate(Tg.dmMessage("/coupons", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithAnyText calls 230L [| "нет доступных"; "Сейчас нет" |],
                $"Expected DM with 'нет доступных' or 'Сейчас нет'. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``My shows 0 taken when user has only used coupon`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 254L, username = "my_used_only_o", firstName = "O")
            let taker = Tg.user(id = 255L, username = "my_used_only_t", firstName = "T")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/used {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("/my", taker))
            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 255L "Мои взятые",
                $"Expected 'Мои взятые'. Got %d{calls.Length} calls")
            Assert.True(findCallWithText calls 255L "—",
                "Expected 0 taken (only used coupon): 'Мои взятые' with '—', not the used coupon")
        }

    [<Fact>]
    let ``My shows only taken with return and used buttons`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 231L, username = "my_owner", firstName = "MO")
            let taker = Tg.user(id = 232L, username = "my_taker", firstName = "MT")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 12 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("/my", owner))
            let! callsOwner = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsOwner 231L "Мои взятые",
                $"Expected 'Мои взятые' for owner. Got %d{callsOwner.Length} calls")
            Assert.True(findCallWithText callsOwner 231L "—",
                $"Expected '—' when owner has no taken. Got %d{callsOwner.Length} calls")
            Assert.False(findCallWithText callsOwner 231L "Мои добавленные",
                "Expected no 'Мои добавленные' section")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("/my", taker))
            let! callsTaker = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsTaker 232L "Мои взятые",
                $"Expected 'Мои взятые' for taker. Got %d{callsTaker.Length} calls")
            Assert.True(findCallWithText callsTaker 232L "12",
                $"Expected coupon value in /my for taker. Got %d{callsTaker.Length} calls")
            Assert.True(callsTaker |> Array.exists (fun c -> c.Body.Contains("return:") && c.Body.Contains("used:")),
                "Expected inline keyboard with return: and used: callback_data under taken coupons")
        }

    [<Fact>]
    let ``My taken inline used button marks coupon`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 235L, username = "my_used_o", firstName = "O")
            let taker = Tg.user(id = 236L, username = "my_used_t", firstName = "T")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"used:{couponId}", taker))
            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 236L "Отметил",
                $"Expected 'Отметил' when pressing Использован. Got %d{calls.Length} calls")
            Assert.True(findCallWithText calls -42L "использовал(а)",
                $"Expected group notification. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``My taken inline return button returns coupon`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 237L, username = "my_ret_o", firstName = "O")
            let taker = Tg.user(id = 238L, username = "my_ret_t", firstName = "T")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"return:{couponId}", taker))
            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 238L "Вернул",
                $"Expected 'Вернул' when pressing Вернуть. Got %d{calls.Length} calls")
            Assert.True(findCallWithText calls -42L "вернул",
                $"Expected group notification. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``Stale used callback when coupon was returned and taken by another shows error and does not delete`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 270L, username = "stale_u_o", firstName = "O")
            let userA = Tg.user(id = 271L, username = "stale_u_a", firstName = "A")
            let userB = Tg.user(id = 272L, username = "stale_u_b", firstName = "B")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(userA.Id, "member")
            do! fixture.SetChatMemberStatus(userB.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", userA))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/return {couponId}", userA))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", userB))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"used:{couponId}:del", userA))

            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls 271L "Не получилось",
                "Expected 'Не получилось' when using stale used: callback (coupon taken by another)")
            let! delCalls = fixture.GetFakeCalls("deleteMessage")
            Assert.Equal(0, delCalls.Length)
        }

    [<Fact>]
    let ``Stale used callback when coupon already used shows error and does not delete`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 273L, username = "stale_used_o", firstName = "O")
            let taker = Tg.user(id = 274L, username = "stale_used_t", firstName = "T")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/used {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"used:{couponId}:del", taker))

            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls 274L "Не получилось",
                "Expected 'Не получилось' when using used: on already-used coupon")
            let! delCalls = fixture.GetFakeCalls("deleteMessage")
            Assert.Equal(0, delCalls.Length)
        }

    [<Fact>]
    let ``Stale return callback when coupon was returned and taken by another shows error and does not delete`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 275L, username = "stale_r_o", firstName = "O")
            let userA = Tg.user(id = 276L, username = "stale_r_a", firstName = "A")
            let userB = Tg.user(id = 277L, username = "stale_r_b", firstName = "B")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(userA.Id, "member")
            do! fixture.SetChatMemberStatus(userB.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", userA))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/return {couponId}", userA))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", userB))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"return:{couponId}:del", userA))

            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls 276L "Не получилось",
                "Expected 'Не получилось' when using stale return: callback (coupon taken by another)")
            let! delCalls = fixture.GetFakeCalls("deleteMessage")
            Assert.Equal(0, delCalls.Length)
        }

    [<Fact>]
    let ``Stale return callback when coupon already used shows error and does not delete`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 278L, username = "stale_ret_used_o", firstName = "O")
            let taker = Tg.user(id = 279L, username = "stale_ret_used_t", firstName = "T")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/used {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"return:{couponId}:del", taker))

            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls 279L "Не получилось",
                "Expected 'Не получилось' when using return: on already-used coupon")
            let! delCalls = fixture.GetFakeCalls("deleteMessage")
            Assert.Equal(0, delCalls.Length)
        }

    [<Fact>]
    let ``Successful used from take-confirmation deletes that message`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 280L, username = "del_used_o", firstName = "O")
            let taker = Tg.user(id = 281L, username = "del_used_t", firstName = "T")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"used:{couponId}:del", taker))

            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls 281L "Отметил",
                "Expected 'Отметил' when pressing Использован on take-confirmation")
            let! delCalls = fixture.GetFakeCalls("deleteMessage")
            Assert.True(delCalls.Length >= 1, "Expected deleteMessage after successful used from take-confirmation")
        }

    [<Fact>]
    let ``Successful return from take-confirmation deletes that message`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 282L, username = "del_ret_o", firstName = "O")
            let taker = Tg.user(id = 283L, username = "del_ret_t", firstName = "T")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback($"return:{couponId}:del", taker))

            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls 283L "Вернул",
                "Expected 'Вернул' when pressing Вернуть on take-confirmation")
            let! delCalls = fixture.GetFakeCalls("deleteMessage")
            Assert.True(delCalls.Length >= 1, "Expected deleteMessage after successful return from take-confirmation")
        }

    [<Fact>]
    let ``/take without id shows available coupons (same as /coupons)`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 290L, username = "take_alias", firstName = "Alias")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            do! fixture.TruncateCoupons()

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", user))
            let! couponId = getLatestCouponId ()

            let extractDmText (chatId: int64) (calls: FakeCall array) =
                calls
                |> Array.choose (fun c ->
                    match parseCallBody c.Body with
                    | Some p when p.ChatId = Some chatId -> p.Text
                    | _ -> None)
                |> Array.tryHead

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("/coupons", user))
            let! callsCoupons = fixture.GetFakeCalls("sendMessage")
            let couponsText = extractDmText user.Id callsCoupons
            Assert.True(couponsText.IsSome, "Expected DM sendMessage for /coupons")
            Assert.Contains("Доступные купоны:", couponsText.Value)
            Assert.Contains("1.", couponsText.Value)
            Assert.Contains("истекает", couponsText.Value)
            // Human-visible list uses 1..N, but callback_data must still reference real coupon id
            let hasTakeButton (callBody: string) =
                try
                    use doc = JsonDocument.Parse(callBody)
                    let root = doc.RootElement

                    let tryGetProp (el: JsonElement) (name: string) =
                        match el.TryGetProperty(name) with
                        | true, v -> Some v
                        | _ -> None

                    let tryGetStringProp (el: JsonElement) (names: string list) =
                        names
                        |> List.tryPick (fun n ->
                            match el.TryGetProperty(n) with
                            | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                            | _ -> None)

                    let replyMarkupOpt =
                        tryGetProp root "reply_markup"
                        |> Option.orElseWith (fun _ -> tryGetProp root "replyMarkup")

                    match replyMarkupOpt with
                    | None -> false
                    | Some rm ->
                        let inlineKbOpt =
                            tryGetProp rm "inline_keyboard"
                            |> Option.orElseWith (fun _ -> tryGetProp rm "inlineKeyboard")

                        match inlineKbOpt with
                        | None -> false
                        | Some kb ->
                            kb.EnumerateArray()
                            |> Seq.collect (fun row -> row.EnumerateArray())
                            |> Seq.exists (fun btn ->
                                let textOpt = tryGetStringProp btn [ "text" ]
                                let cbOpt = tryGetStringProp btn [ "callback_data"; "callbackData" ]
                                textOpt = Some "Взять 1" && cbOpt = Some $"take:{couponId}")
                with _ ->
                    false

            let sampleBody = callsCoupons |> Array.tryHead |> Option.map (fun c -> c.Body) |> Option.defaultValue "<no sendMessage calls>"
            Assert.True(callsCoupons |> Array.exists (fun c -> hasTakeButton c.Body),
                $"Expected reply markup to contain button text 'Взять 1' with callback_data take:{couponId}. Sample body: {sampleBody}")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("/take", user))
            let! callsTake = fixture.GetFakeCalls("sendMessage")
            let takeText = extractDmText user.Id callsTake
            Assert.True(takeText.IsSome, "Expected DM sendMessage for /take")
            Assert.Equal(couponsText.Value, takeText.Value)
        }

    [<Fact>]
    let ``/add accepts multiple date formats and stores exact date`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let user = Tg.user(id = 291L, username = "date_formats", firstName = "Dates")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // Tricky date: 02.01.2026 must be 2 January (not 1 February)
            let expected = DateOnly(2026, 1, 2)
            let cases =
                [| "2026-01-02"
                   "02.01.2026"
                   "2.1.2026"
                   "02/01/2026"
                   "2/1/2026"
                   "02-01-2026"
                   "2-1-2026"
                   "2026/01/02"
                   "2026.01.02" |]

            for i, dateStr in cases |> Array.indexed do
                do! fixture.ClearFakeCalls()
                let caption = $"/add 10 50 {dateStr}"
                let! resp = fixture.SendUpdate(Tg.dmPhotoWithCaption(caption, user, fileId = $"photo-dates-{i}"))
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

                let! calls = fixture.GetFakeCalls("sendMessage")
                let expectedStr = expected.ToString("dd.MM.yyyy")
                Assert.True(findCallWithText calls user.Id expectedStr, $"Expected DM to include formatted date {expectedStr} for input '{dateStr}'")

                let! couponId = getLatestCouponId ()
                // Read as text to avoid DateOnly mapping issues in Dapper/Npgsql setup
                let! expiresIso =
                    fixture.QuerySingle<string>(
                        "SELECT expires_at::text FROM coupon WHERE id = @id",
                        {| id = couponId |}
                    )
                Assert.Equal(expected.ToString("yyyy-MM-dd"), expiresIso)
        }

    [<Fact>]
    let ``Stats shows added taken used`` () =
        task {
            do! fixture.ClearFakeCalls()
            let owner = Tg.user(id = 233L, username = "stats_owner", firstName = "SO")
            let taker = Tg.user(id = 234L, username = "stats_taker", firstName = "ST")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/used {couponId}", taker))

            let! _ = fixture.SendUpdate(Tg.dmMessage("/stats", taker))
            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 234L "Взято",
                $"Expected 'Взято' in /stats. Got %d{calls.Length} calls")
            Assert.True(findCallWithText calls 234L "Использовано",
                $"Expected 'Использовано' in /stats. Got %d{calls.Length} calls")
        }

    interface IAssemblyFixture<DefaultCouponHubTestContainers>

