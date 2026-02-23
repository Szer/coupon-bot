namespace CouponHubBot.Tests

open System.Threading.Tasks
open System.Text
open System.Net.Http
open System.Text.Json
open Dapper
open Npgsql
open Xunit
open FakeCallHelpers

type ReminderTests(fixture: DefaultCouponHubTestContainers) =

    [<Theory>]
    [<InlineData(1, "Сегодня истекает 1 купон на сумму")>]
    [<InlineData(2, "Сегодня истекает 2 купона на сумму")>]
    [<InlineData(7, "Сегодня истекает 7 купонов на сумму")>]
    let ``Reminder uses correct Russian plural form for expiring coupons`` (couponCount: int, expectedText: string) =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do! conn.ExecuteAsync("INSERT INTO \"user\"(id, username, first_name, created_at, updated_at) VALUES (500,'owner','Owner',NOW(),NOW()) ON CONFLICT DO NOTHING;") :> Task

            let todayIso = fixture.FixedToday.ToString("yyyy-MM-dd")
            for i in 1..couponCount do
                do! conn.ExecuteAsync(
                    "INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status) VALUES (500,@photoId,10.00,50.00,@today::date,'available');",
                    {| photoId = $"seed-photo-plural-{couponCount}-{i}"; today = todayIso |}) :> Task

            use body = new StringContent("", Encoding.UTF8, "application/json")
            let! _ = fixture.Bot.PostAsync("/test/run-reminder", body)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls -42L expectedText,
                $"Expected reminder to group -42 with '{expectedText}'. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``Monthly all-time stats are posted on first Monday of month`` () =
        task {
            do! fixture.ClearFakeCalls()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)

            // Seed users + coupons + events (older than 7 days — all-time stats must include them).
            do!
                conn.ExecuteAsync(
                    """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (501,'u501','U501',NOW(),NOW()),
       (502,'u502','U502',NOW(),NOW())
ON CONFLICT (id) DO NOTHING;

INSERT INTO coupon(id, owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES
  (7101,501,'p-7101',10.00,50.00,'2026-02-01','used'),
  (7102,501,'p-7102',10.00,50.00,'2026-02-01','used'),
  (7103,502,'p-7103',10.00,50.00,'2026-02-01','available')
ON CONFLICT (id) DO NOTHING;

INSERT INTO coupon_event(coupon_id, user_id, event_type, created_at)
VALUES
  (7101,501,'used','2026-01-18T10:00:00Z'),
  (7102,501,'used','2026-01-18T11:00:00Z'),
  (7103,502,'added','2026-01-18T12:00:00Z');
"""
                )
                :> Task

            // 2026-02-02 is the first Monday of February 2026; 10:00 UTC = 10:00 Dublin (winter/GMT).
            use body = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = fixture.Bot.PostAsync("/test/run-reminder?nowUtc=2026-02-02T10:00:00Z", body)
            if not resp.IsSuccessStatusCode then
                let! text = resp.Content.ReadAsStringAsync()
                failwith $"Expected 2xx from /test/run-reminder, got {resp.StatusCode}. Body: {text}"

            let! calls = fixture.GetFakeCalls("sendMessage")

            let tryGetText (body: string) =
                try
                    use doc = JsonDocument.Parse(body)
                    match doc.RootElement.TryGetProperty("text") with
                    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                    | _ -> None
                with _ -> None

            let has (s: string) =
                calls
                |> Array.choose (fun c -> tryGetText c.Body)
                |> Array.exists (fun t -> t.Contains(s))

            Assert.True(has "Статистика за всё время (использовано/добавлено)",
                "Expected all-time stats message in group on first Monday of month")
        }

    [<Fact>]
    let ``Monthly stats are NOT posted on second Monday`` () =
        task {
            do! fixture.ClearFakeCalls()

            // 2026-01-12 is the second Monday of January 2026 (day 12 > 7).
            use body = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = fixture.Bot.PostAsync("/test/run-reminder?nowUtc=2026-01-12T10:00:00Z", body)
            if not resp.IsSuccessStatusCode then
                let! text = resp.Content.ReadAsStringAsync()
                failwith $"Expected 2xx from /test/run-reminder, got {resp.StatusCode}. Body: {text}"

            let! calls = fixture.GetFakeCalls("sendMessage")

            let tryGetText (body: string) =
                try
                    use doc = JsonDocument.Parse(body)
                    match doc.RootElement.TryGetProperty("text") with
                    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                    | _ -> None
                with _ -> None

            let hasStats =
                calls
                |> Array.choose (fun c -> tryGetText c.Body)
                |> Array.exists (fun t -> t.Contains("Статистика за всё время"))

            Assert.False(hasStats, "Expected NO stats message on the second Monday of month")
        }

    [<Fact>]
    let ``Monthly stats are NOT posted on non-Monday first week`` () =
        task {
            do! fixture.ClearFakeCalls()

            // 2026-01-07 is a Wednesday (day = 7, first week, but not Monday).
            use body = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = fixture.Bot.PostAsync("/test/run-reminder?nowUtc=2026-01-07T10:00:00Z", body)
            if not resp.IsSuccessStatusCode then
                let! text = resp.Content.ReadAsStringAsync()
                failwith $"Expected 2xx from /test/run-reminder, got {resp.StatusCode}. Body: {text}"

            let! calls = fixture.GetFakeCalls("sendMessage")

            let tryGetText (body: string) =
                try
                    use doc = JsonDocument.Parse(body)
                    match doc.RootElement.TryGetProperty("text") with
                    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                    | _ -> None
                with _ -> None

            let hasStats =
                calls
                |> Array.choose (fun c -> tryGetText c.Body)
                |> Array.exists (fun t -> t.Contains("Статистика за всё время"))

            Assert.False(hasStats, "Expected NO stats message on a non-Monday first-week day")
        }

    [<Fact>]
    let ``Overdue taken coupons trigger one DM reminder per user`` () =
        task {
            do! fixture.ClearFakeCalls()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do!
                conn.ExecuteAsync(
                    """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (601,'u601','U601',NOW(),NOW())
ON CONFLICT (id) DO NOTHING;

-- Two overdue taken coupons for same user (should still only get 1 DM)
INSERT INTO coupon(id, owner_id, photo_file_id, value, min_check, expires_at, status, taken_by, taken_at)
VALUES
  (8101,601,'p-8101',10.00,50.00,'2026-02-01','taken',601,'2026-01-17T08:00:00Z'),
  (8102,601,'p-8102',10.00,50.00,'2026-01-10','taken',601,'2026-01-17T09:00:00Z')
ON CONFLICT (id) DO NOTHING;
"""
                )
                :> Task

            use body = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = fixture.Bot.PostAsync("/test/run-reminder?nowUtc=2026-01-19T08:00:00Z", body)
            if not resp.IsSuccessStatusCode then
                let! text = resp.Content.ReadAsStringAsync()
                failwith $"Expected 2xx from /test/run-reminder, got {resp.StatusCode}. Body: {text}"

            let! calls = fixture.GetFakeCalls("sendMessage")
            let dmCallsToUser =
                calls
                |> Array.filter (fun c ->
                    match parseCallBody c.Body with
                    | Some p -> p.ChatId = Some 601L
                    | _ -> false)

            Assert.Equal(1, dmCallsToUser.Length)
            Assert.True(dmCallsToUser[0].Body.Contains("\"text\""), "Expected DM call to include text")
        }

    [<Fact>]
    let ``User who used coupon yesterday but did not add gets reminder`` () =
        task {
            do! fixture.ClearFakeCalls()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do!
                conn.ExecuteAsync(
                    """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (701,'u701','U701',NOW(),NOW())
ON CONFLICT (id) DO NOTHING;

-- Coupon for seeding event
INSERT INTO coupon(id, owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES (9101,701,'p-9101',10.00,50.00,'2026-02-01','used')
ON CONFLICT (id) DO NOTHING;

-- User used a coupon yesterday
INSERT INTO coupon_event(coupon_id, user_id, event_type, created_at)
VALUES (9101,701,'used','2026-01-18T10:00:00Z');
"""
                )
                :> Task

            // Run reminder for 2026-01-19 (yesterday = 2026-01-18)
            use body = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = fixture.Bot.PostAsync("/test/run-reminder?nowUtc=2026-01-19T08:00:00Z", body)
            if not resp.IsSuccessStatusCode then
                let! text = resp.Content.ReadAsStringAsync()
                failwith $"Expected 2xx from /test/run-reminder, got {resp.StatusCode}. Body: {text}"

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls 701L "Не забудь добавить купоны в бота",
                "Expected add-coupon reminder DM to user 701")
        }

    [<Fact>]
    let ``User who used and added yesterday does not get reminder`` () =
        task {
            do! fixture.ClearFakeCalls()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do!
                conn.ExecuteAsync(
                    """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (702,'u702','U702',NOW(),NOW())
ON CONFLICT (id) DO NOTHING;

-- Coupons for seeding events
INSERT INTO coupon(id, owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES
  (9201,702,'p-9201',10.00,50.00,'2026-02-01','used'),
  (9202,702,'p-9202',10.00,50.00,'2026-02-01','available')
ON CONFLICT (id) DO NOTHING;

-- User used a coupon AND added a coupon yesterday
INSERT INTO coupon_event(coupon_id, user_id, event_type, created_at)
VALUES
  (9201,702,'used','2026-01-18T10:00:00Z'),
  (9202,702,'added','2026-01-18T11:00:00Z');
"""
                )
                :> Task

            // Run reminder for 2026-01-19 (yesterday = 2026-01-18)
            use body = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = fixture.Bot.PostAsync("/test/run-reminder?nowUtc=2026-01-19T08:00:00Z", body)
            if not resp.IsSuccessStatusCode then
                let! text = resp.Content.ReadAsStringAsync()
                failwith $"Expected 2xx from /test/run-reminder, got {resp.StatusCode}. Body: {text}"

            let! calls = fixture.GetFakeCalls("sendMessage")
            
            // User 702 should NOT receive the add-coupon reminder
            let dmCallsToUser702 =
                calls
                |> Array.filter (fun c ->
                    match parseCallBody c.Body with
                    | Some p -> p.ChatId = Some 702L && p.Text = Some "Не забудь добавить купоны в бота"
                    | _ -> false)

            Assert.Equal(0, dmCallsToUser702.Length)
        }

    [<Fact>]
    let ``User who used late at night and added next day does not get reminder`` () =
        task {
            do! fixture.ClearFakeCalls()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            do!
                conn.ExecuteAsync(
                    """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (703,'u703','U703',NOW(),NOW())
ON CONFLICT (id) DO NOTHING;

-- Coupons for seeding events
INSERT INTO coupon(id, owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES
  (9301,703,'p-9301',10.00,50.00,'2026-02-01','used'),
  (9302,703,'p-9302',10.00,50.00,'2026-02-01','available')
ON CONFLICT (id) DO NOTHING;

-- User used a coupon at 23:30 yesterday, then added a coupon at 01:30 today
INSERT INTO coupon_event(coupon_id, user_id, event_type, created_at)
VALUES
  (9301,703,'used','2026-01-18T23:30:00Z'),
  (9302,703,'added','2026-01-19T01:30:00Z');
"""
                )
                :> Task

            // Run reminder for 2026-01-19 at 08:00 (yesterday = 2026-01-18)
            use body = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = fixture.Bot.PostAsync("/test/run-reminder?nowUtc=2026-01-19T08:00:00Z", body)
            if not resp.IsSuccessStatusCode then
                let! text = resp.Content.ReadAsStringAsync()
                failwith $"Expected 2xx from /test/run-reminder, got {resp.StatusCode}. Body: {text}"

            let! calls = fixture.GetFakeCalls("sendMessage")
            
            // User 703 should NOT receive the add-coupon reminder because they added after using
            let dmCallsToUser703 =
                calls
                |> Array.filter (fun c ->
                    match parseCallBody c.Body with
                    | Some p -> p.ChatId = Some 703L && p.Text = Some "Не забудь добавить купоны в бота"
                    | _ -> false)

            Assert.Equal(0, dmCallsToUser703.Length)
        }



