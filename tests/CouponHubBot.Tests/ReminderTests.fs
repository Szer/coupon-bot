namespace CouponHubBot.Tests

open System.Threading.Tasks
open System.Text
open System.Net.Http
open System.Text.Json
open Dapper
open Npgsql
open Xunit
open Xunit.Extensions.AssemblyFixture
open FakeCallHelpers

type ReminderTests(fixture: DefaultCouponHubTestContainers) =

    [<Fact>]
    let ``Reminder sends message to group when coupons expire today`` () =
        task {
            do! fixture.ClearFakeCalls()

            // Seed an expiring coupon
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            //language=postgresql
            do! conn.ExecuteAsync("INSERT INTO \"user\"(id, username, first_name, created_at, updated_at) VALUES (500,'owner','Owner',NOW(),NOW()) ON CONFLICT DO NOTHING;") :> Task
            //language=postgresql
            let todayIso = fixture.FixedToday.ToString("yyyy-MM-dd")
            do! conn.ExecuteAsync("INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status) VALUES (500,'seed-photo-500',10.00,50.00,@today::date,'available');", {| today = todayIso |}) :> Task

            // Trigger reminder via test endpoint
            use body = new StringContent("", Encoding.UTF8, "application/json")
            let! _ = fixture.Bot.PostAsync("/test/run-reminder", body)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls -42L "Сегодня истекает",
                $"Expected reminder to group -42 with 'Сегодня истекает'. Got %d{calls.Length} calls")
        }

    [<Fact>]
    let ``Weekly stats are posted on Monday`` () =
        task {
            do! fixture.ClearFakeCalls()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)

            // Seed users + coupons + events within last 7 days window.
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

            // Trigger reminder via test endpoint on Monday morning.
            use body = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = fixture.Bot.PostAsync("/test/run-reminder?nowUtc=2026-01-19T08:00:00Z", body)
            if not resp.IsSuccessStatusCode then
                let! text = resp.Content.ReadAsStringAsync()
                failwith $"Expected 2xx from /test/run-reminder, got {resp.StatusCode}. Body: {text}"

            let! calls = fixture.GetFakeCalls("sendMessage")

            // FakeTgApi stores request bodies as JSON, where Cyrillic may be escaped as \\uXXXX.
            // Parse JSON to get human-readable text.
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

            Assert.True(has "Статистика за последние 7 дней (использовано/добавлено)",
                "Expected weekly stats message in group on Monday")
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

    interface IAssemblyFixture<DefaultCouponHubTestContainers>
