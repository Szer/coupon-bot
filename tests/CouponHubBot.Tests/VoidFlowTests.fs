namespace CouponHubBot.Tests

open System.Net
open System.Text.Json
open Dapper
open Npgsql
open Xunit
open FakeCallHelpers

type VoidFlowTests(fixture: DefaultCouponHubTestContainers) =

    let getLatestCouponId () =
        task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            let sql = "SELECT id FROM coupon ORDER BY id DESC LIMIT 1"
            return! conn.QuerySingleAsync<int>(sql)
        }

    let getCouponStatus (couponId: int) =
        fixture.QuerySingle<string>("SELECT status FROM coupon WHERE id = @id", {| id = couponId |})

    [<Fact>]
    let ``Owner can void available coupon via /void`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 700L, username = "void_owner", firstName = "VoidOwner")
            do! fixture.SetChatMemberStatus(owner.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/void {couponId}", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls owner.Id "аннулирован",
                "Expected confirmation that coupon was voided")

            let! status = getCouponStatus couponId
            Assert.Equal("voided", status)
        }

    [<Fact>]
    let ``Owner can void taken coupon and taker gets notified`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 701L, username = "void_taken_owner", firstName = "Owner")
            let taker = Tg.user(id = 702L, username = "void_taken_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            // Taker takes the coupon
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))

            do! fixture.ClearFakeCalls()
            // Owner voids
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/void {couponId}", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            // Owner should see confirmation
            Assert.True(findCallWithText calls owner.Id "аннулирован",
                "Expected confirmation that coupon was voided")

            // Taker should be notified
            Assert.True(findCallWithText calls taker.Id "аннулирован",
                "Expected taker to be notified about voided coupon")

            let! status = getCouponStatus couponId
            Assert.Equal("voided", status)
        }

    [<Fact>]
    let ``Cannot void used coupon`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 703L, username = "void_used_owner", firstName = "Owner")
            let taker = Tg.user(id = 704L, username = "void_used_taker", firstName = "Taker")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(taker.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            let! _ = fixture.SendUpdate(Tg.dmMessage($"/take {couponId}", taker))
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/used {couponId}", taker))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/void {couponId}", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls owner.Id "Не удалось аннулировать",
                "Expected error message about void failure")

            let! status = getCouponStatus couponId
            Assert.Equal("used", status)
        }

    [<Fact>]
    let ``Non-owner cannot void coupon`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 705L, username = "void_no_owner", firstName = "Owner")
            let stranger = Tg.user(id = 706L, username = "void_stranger", firstName = "Stranger")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(stranger.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/void {couponId}", stranger))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls stranger.Id "Не удалось аннулировать",
                "Expected error message for non-owner void attempt")

            let! status = getCouponStatus couponId
            Assert.Equal("available", status)
        }

    [<Fact>]
    let ``Owner can void coupon via callback button`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 707L, username = "void_cb_owner", firstName = "CbOwner")
            do! fixture.SetChatMemberStatus(owner.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmCallback($"void:{couponId}", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls owner.Id "аннулирован",
                "Expected confirmation via callback")

            let! status = getCouponStatus couponId
            Assert.Equal("voided", status)
        }

    [<Fact>]
    let ``/added shows voidable coupons with Аннулировать buttons`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 708L, username = "added_owner", firstName = "AddedOwner")
            do! fixture.SetChatMemberStatus(owner.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))

            // Set a known barcode so we can assert the last 4 digits appear in /added output
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do! conn.OpenAsync()
            let testBarcode = "1234567891191" // last 4 digits: 1191
            let! _ = conn.ExecuteAsync(
                "UPDATE coupon SET barcode_text = @barcode WHERE owner_id = @owner_id",
                {| barcode = testBarcode; owner_id = owner.Id |})

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/added", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls owner.Id "Мои добавленные купоны",
                "Expected /added listing message")
            Assert.True(calls |> Array.exists (fun c -> c.Body.Contains("void:")),
                "Expected void callback button in /added response")
            // Check full barcode suffix format (···XXXX) appears in the message text
            Assert.True(findCallWithText calls owner.Id "···1191",
                "Expected barcode suffix '···1191' in /added output")
            // Check reply_markup contains Аннулировать via JSON parsing (Cyrillic may be escaped in raw JSON)
            Assert.True(
                calls |> Array.exists (fun c ->
                    try
                        use doc = JsonDocument.Parse(c.Body)
                        let root = doc.RootElement
                        match root.TryGetProperty("reply_markup") with
                        | true, rm ->
                            let json = rm.GetRawText()
                            json.Contains("Аннулировать") ||
                            (try
                                use rmDoc = JsonDocument.Parse(json)
                                // Traverse inline_keyboard to find button text
                                match rmDoc.RootElement.TryGetProperty("inline_keyboard") with
                                | true, kb ->
                                    kb.EnumerateArray() |> Seq.exists (fun row ->
                                        row.EnumerateArray() |> Seq.exists (fun btn ->
                                            match btn.TryGetProperty("text") with
                                            | true, t -> t.GetString().Contains("Аннулировать")
                                            | _ -> false))
                                | _ -> false
                             with _ -> false)
                        | _ -> false
                    with _ -> false),
                "Expected Аннулировать button label in inline keyboard")
        }

    [<Fact>]
    let ``/added with no active coupons shows empty message`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(id = 709L, username = "added_empty", firstName = "Empty")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/added", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "нет активных",
                "Expected empty /added message")
        }

    [<Fact>]
    let ``/ad alias works for /added`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(id = 710L, username = "ad_alias", firstName = "Ad")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/ad", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "нет активных",
                "Expected empty /added message via /ad alias")
        }

    [<Fact>]
    let ``/stats shows voided count`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 711L, username = "stats_voided", firstName = "Stats")
            do! fixture.SetChatMemberStatus(owner.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()
            let! _ = fixture.SendUpdate(Tg.dmMessage($"/void {couponId}", owner))

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/stats", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls owner.Id "Аннулировано",
                "Expected voided count in stats")
        }

    [<Fact>]
    let ``/my shows Мои добавленные button`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let user = Tg.user(id = 712L, username = "my_btn", firstName = "MyBtn")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! resp = fixture.SendUpdate(Tg.dmMessage("/my", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(calls |> Array.exists (fun c -> c.Body.Contains("myAdded")),
                "Expected myAdded callback button in /my response")
        }

    [<Fact>]
    let ``Admin can void any available coupon via /void`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 713L, username = "void_adm_owner", firstName = "Owner")
            // Admin user ID 900 is configured in FEEDBACK_ADMINS
            let admin = Tg.user(id = 900L, username = "admin_void", firstName = "Admin")
            do! fixture.SetChatMemberStatus(owner.Id, "member")
            do! fixture.SetChatMemberStatus(admin.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))
            let! couponId = getLatestCouponId ()

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage($"/void {couponId}", admin))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls admin.Id "аннулирован",
                "Expected admin to see confirmation that coupon was voided")

            let! status = getCouponStatus couponId
            Assert.Equal("voided", status)
        }

    [<Fact>]
    let ``/added shows only non-expired coupons`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            let owner = Tg.user(id = 714L, username = "added_exp", firstName = "AddedExp")
            do! fixture.SetChatMemberStatus(owner.Id, "member")

            // Add a future coupon (will be visible): 10€/50€
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("/add 10 50 2026-01-25", owner))

            // Manually insert an expired coupon directly in DB: 5€/25€ expired Dec 2025
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do! conn.OpenAsync()
            //language=postgresql
            let insertExpired =
                """
INSERT INTO coupon (owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES (@owner_id, @photo_file_id, 5.00, 25.00, '2025-12-01'::date, 'available');
"""
            let! _ = conn.ExecuteAsync(insertExpired, {| owner_id = owner.Id; photo_file_id = "expired-photo-added" |})

            do! fixture.ClearFakeCalls()
            let! resp = fixture.SendUpdate(Tg.dmMessage("/added", owner))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            // Should show the listing header
            Assert.True(findCallWithText calls owner.Id "Мои добавленные купоны",
                "Expected /added listing message")
            // Should contain the future coupon (10€ из 50€)
            Assert.True(findCallWithText calls owner.Id "10",
                "Expected future coupon value to be listed")
            // Should NOT contain the expired coupon (5€/25€)
            Assert.False(findCallWithText calls owner.Id "5€",
                "Expected expired coupon NOT to be listed in /added")
        }
