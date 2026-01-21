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

    let getCouponCount () =
        fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon", null)

    let getLatestCouponValue () =
        fixture.QuerySingle<decimal>("SELECT value FROM coupon ORDER BY id DESC LIMIT 1", null)

    let getLatestCouponMinCheck () =
        fixture.QuerySingle<decimal>("SELECT min_check FROM coupon ORDER BY id DESC LIMIT 1", null)

    let getLatestCouponExpiresIso () =
        fixture.QuerySingle<string>("SELECT expires_at::text FROM coupon ORDER BY id DESC LIMIT 1", null)

    let getLatestCouponPhotoFileId () =
        fixture.QuerySingle<string>("SELECT photo_file_id FROM coupon ORDER BY id DESC LIMIT 1", null)

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
                        p.Caption.IsSome && p.Caption.Value.Contains("Ты взял")
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
            Assert.True(findCallWithText calls 210L "Пришли фото купона",
                $"Expected DM with 'Пришли фото купона'. Got %d{calls.Length} calls")
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
            Assert.True(findCallWithText calls 216L "пришли фото",
                $"Expected DM instructing photo for manual add. Got %d{calls.Length} calls")
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
            Assert.True(findCallWithText calls 212L "Нужна подпись вида",
                $"Expected DM with 'Нужна подпись вида'. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``/add wizard adds coupon via buttons`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(id = 214L, username = "add_wizard", firstName = "Wizard")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/add", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 214L "Пришли фото купона",
                "Expected wizard to ask for photo")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = "wizard-photo"))

            let! callsAfterPhoto = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsAfterPhoto 214L "Выбери скидку",
                "Expected wizard to ask for discount options after photo")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:disc:10:50", user))
            let! callsAfterDisc = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsAfterDisc 214L "дату истечения",
                "Expected wizard to ask for expiry date after discount choice")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:date:today", user))
            let! callsAfterDate = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsAfterDate 214L "Подтвердить добавление",
                "Expected wizard confirm step after date choice")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:confirm", user))
            let! callsAfterConfirm = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsAfterConfirm 214L "Добавил купон",
                "Expected success message after confirm")
        }

    [<Fact>]
    let ``/add wizard: unexpected photo on confirm is ignored and asks to finish or restart`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(id = 215L, username = "add_unexpected_photo", firstName = "Wizard")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmMessage("/add", user))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = "wizard-photo-1"))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:disc:10:50", user))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:date:today", user))

            let! callsConfirm = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsConfirm 215L "Подтвердить добавление",
                "Expected wizard confirm step before sending unexpected photo")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = "wizard-photo-unexpected"))

            let! callsAfter = fixture.GetFakeCalls("sendMessage")
            // Desired UX: warn user instead of silently ignoring.
            Assert.True(findCallWithText callsAfter 215L "Сейчас идёт добавление купона. Закончи текущий шаг или начни заново: /add",
                "Expected warning telling user to finish current add or restart with /add")

            let! count = getCouponCount ()
            Assert.Equal(0L, count)
        }

    [<Fact>]
    let ``/add wizard: sending /add at confirm restarts flow and adds new coupon (not old)`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(id = 217L, username = "add_restart_at_confirm", firstName = "Wizard")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // Start first wizard and reach confirm (but do not confirm).
            let! _ = fixture.SendUpdate(Tg.dmMessage("/add", user))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = "wizard-photo-old"))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:disc:10:50", user))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("2026-02-01", user))

            let! callsConfirm1 = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsConfirm1 217L "Подтвердить добавление",
                "Expected confirm step for first wizard")

            let! countBefore = getCouponCount ()
            Assert.Equal(0L, countBefore)

            // Restart with /add and create a different coupon.
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("/add", user))
            let! callsAskPhoto = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsAskPhoto 217L "Пришли фото купона",
                "Expected wizard to restart and ask for photo")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = "wizard-photo-new"))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:disc:5:25", user))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("2026-03-03", user))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:confirm", user))
            let! callsDone = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsDone 217L "Добавил купон",
                "Expected success message after confirm for restarted wizard")

            let! countAfter = getCouponCount ()
            Assert.Equal(1L, countAfter)

            let! v = getLatestCouponValue ()
            let! mc = getLatestCouponMinCheck ()
            let! exp = getLatestCouponExpiresIso ()
            let! photoId = getLatestCouponPhotoFileId ()
            Assert.Equal(5m, v)
            Assert.Equal(25m, mc)
            Assert.Equal("2026-03-03", exp)
            Assert.Equal("wizard-photo-new", photoId)
        }

    [<Fact>]
    let ``/add wizard: user can type discount as X/Y`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(id = 218L, username = "add_wizard_disc_slash", firstName = "Wizard")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmMessage("/add", user))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = "wizard-photo-slash"))

            let! callsAfterPhoto = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsAfterPhoto 218L "Выбери скидку",
                "Expected wizard to ask for discount options after photo")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("10/50", user))
            let! callsAfterDisc = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsAfterDisc 218L "дату истечения",
                "Expected wizard to ask for expiry date after manual discount input")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("2026-02-02", user))
            let! callsAfterDate = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsAfterDate 218L "Подтвердить добавление",
                "Expected wizard confirm step after manual date input")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:confirm", user))
            let! callsAfterConfirm = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsAfterConfirm 218L "Добавил купон",
                "Expected success message after confirm")

            let! v = getLatestCouponValue ()
            let! mc = getLatestCouponMinCheck ()
            let! exp = getLatestCouponExpiresIso ()
            let! photoId = getLatestCouponPhotoFileId ()
            Assert.Equal(10m, v)
            Assert.Equal(50m, mc)
            Assert.Equal("2026-02-02", exp)
            Assert.Equal("wizard-photo-slash", photoId)
        }

    [<Fact>]
    let ``/add wizard: user can type expiry date as day-of-month number`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(id = 219L, username = "add_wizard_day_number", firstName = "Wizard")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let today = DateOnly.FromDateTime(DateTime.UtcNow)
            let day =
                if today.Day <= 27 then today.Day + 1
                else 1

            let expected =
                // Same algorithm as the bot: next such day strictly in the future (UTC),
                // skipping months that don't contain the day (e.g. 31 in April).
                let startMonth = DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc)

                let rec loop monthOffset =
                    if monthOffset > 24 then
                        failwith "Expected to compute next day-of-month"
                    else
                        let dt = startMonth.AddMonths(monthOffset)
                        try
                            let candidate = DateOnly(dt.Year, dt.Month, day)
                            if candidate > today then candidate else loop (monthOffset + 1)
                        with _ ->
                            loop (monthOffset + 1)

                (loop 0).ToString("yyyy-MM-dd")

            let! _ = fixture.SendUpdate(Tg.dmMessage("/add", user))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = "wizard-photo-day"))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:disc:10:50", user))

            let! callsAfterDisc = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsAfterDisc 219L "дату истечения",
                "Expected wizard to ask for expiry date after discount choice")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage(day.ToString(), user))

            let! callsAfterDate = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsAfterDate 219L "Подтвердить добавление",
                "Expected wizard confirm step after day-of-month input")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:confirm", user))

            let! exp = getLatestCouponExpiresIso ()
            Assert.Equal(expected, exp)
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
    let ``Expired coupons are not shown as available`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let user = Tg.user(id = 240L, username = "expired_filter")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            // Seed an expired available coupon directly
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do!
                conn.ExecuteAsync(
                    """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (240, 'expired_filter', 'Expired', NOW(), NOW())
ON CONFLICT (id) DO NOTHING;

INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES (240, 'seed-expired', 10.00, 50.00, (CURRENT_DATE - interval '1 day')::date, 'available');
"""
                )
                :> Task

            let! _ = fixture.SendUpdate(Tg.dmMessage("/list", user))
            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithAnyText calls 240L [| "нет доступных"; "Сейчас нет" |],
                "Expected expired available coupon to be filtered out from /list")
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
            Assert.True(findCallWithText calls 255L "Мои купоны",
                $"Expected 'Мои купоны'. Got %d{calls.Length} calls")
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
            Assert.True(findCallWithText callsOwner 231L "Мои купоны",
                $"Expected 'Мои купоны' for owner. Got %d{callsOwner.Length} calls")
            Assert.True(findCallWithText callsOwner 231L "—",
                $"Expected '—' when owner has no taken. Got %d{callsOwner.Length} calls")
            Assert.False(findCallWithText callsOwner 231L "Мои добавленные",
                "Expected no 'Мои добавленные' section")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("/my", taker))
            let! albumCalls = fixture.GetFakeCalls("sendMediaGroup")
            Assert.Equal(1, albumCalls.Length)

            let! callsTaker = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsTaker 232L "Мои купоны",
                $"Expected 'Мои купоны' for taker. Got %d{callsTaker.Length} calls")
            Assert.True(findCallWithText callsTaker 232L "Купон ID:",
                "Expected coupon ID in /my text")
            Assert.True(callsTaker |> Array.exists (fun c -> c.Body.Contains("return:") && c.Body.Contains("used:")),
                "Expected inline keyboard with return: and used: callback_data under /my text message")
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
                                textOpt = Some "Взять 1ый" && cbOpt = Some $"take:{couponId}")
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
    let ``Limit 4 prevents taking 5th coupon`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let owner = Tg.user(id = 310L, username = "limit_owner", firstName = "Owner")
            let taker = Tg.user(id = 311L, username = "limit_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let ids = ResizeArray<int>()
            for i in 1 .. 5 do
                let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner, fileId = $"limit-photo-{i}"))
                let! cid = getLatestCouponId ()
                ids.Add(cid)

            // take first 4
            for i in 0 .. 3 do
                do! fixture.ClearFakeCalls()
                let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {ids[i]}", taker))
                let! photoCalls = fixture.GetFakeCalls("sendPhoto")
                Assert.Equal(1, photoCalls.Length)

            // 5th should be rejected
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {ids[4]}", taker))
            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls 311L "Нельзя взять больше 4",
                "Expected limit reached message on 5th take")
            let! photoCalls = fixture.GetFakeCalls("sendPhoto")
            Assert.Equal(0, photoCalls.Length)
        }

    [<Fact>]
    let ``Limit 4 is race-safe for concurrent takes`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let owner = Tg.user(id = 320L, username = "race_owner", firstName = "Owner")
            let taker = Tg.user(id = 321L, username = "race_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let ids = ResizeArray<int>()
            for i in 1 .. 5 do
                let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner, fileId = $"race-photo-{i}"))
                let! cid = getLatestCouponId ()
                ids.Add(cid)

            // Take first 3 sequentially
            for i in 0 .. 2 do
                let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {ids[i]}", taker))
                ()

            // Then take 2 concurrently: only one should succeed (4th), another should hit limit
            do! fixture.ClearFakeCalls()
            let t1 = fixture.SendUpdate(Tg.dmMessage($"/take {ids[3]}", taker))
            let t2 = fixture.SendUpdate(Tg.dmMessage($"/take {ids[4]}", taker))
            let! _ = Task.WhenAll [| t1 :> Task; t2 :> Task |]

            let! photoCalls = fixture.GetFakeCalls("sendPhoto")
            Assert.Equal(1, photoCalls.Length)
            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls 321L "Нельзя взять больше 4",
                "Expected one of concurrent takes to be rejected by limit")
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

    [<Fact>]
    let ``/feedback forwards next message to admins`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 400L, username = "fb_user", firstName = "FB")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmMessage("/feedback", user))
            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 400L "Следующее твоё сообщение",
                "Expected /feedback instruction message")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("hello admins", user))

            let! fwCalls = fixture.GetFakeCalls("forwardMessage")
            Assert.Equal(2, fwCalls.Length)
            let forwardedChatIds =
                fwCalls
                |> Array.choose (fun c ->
                    try
                        use doc = JsonDocument.Parse(c.Body)
                        Some(doc.RootElement.GetProperty("chat_id").GetInt64())
                    with _ -> None)
                |> Array.sort
            Assert.Equal<int64 array>([| 900L; 901L |], forwardedChatIds)

            let! doneCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText doneCalls 400L "Спасибо",
                "Expected user confirmation after forwarding")
        }

    interface IAssemblyFixture<DefaultCouponHubTestContainers>

