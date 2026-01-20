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
            do! conn.ExecuteAsync("INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status) VALUES (500,'seed-photo',10.00,50.00,CURRENT_DATE,'available');") :> Task

            // Trigger reminder via test endpoint
            use body = new StringContent("", Encoding.UTF8, "application/json")
            let! _ = fixture.Bot.PostAsync("/test/run-reminder", body)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls -42L "Сегодня истекают",
                $"Expected reminder to group -42 with 'Сегодня истекают'. Got %d{calls.Length} calls")
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
  (7101,501,'p',10.00,50.00,'2026-02-01','used'),
  (7102,501,'p',10.00,50.00,'2026-02-01','used'),
  (7103,502,'p',10.00,50.00,'2026-02-01','available')
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

            Assert.True(has "Статистика за последние 7 дней",
                "Expected weekly stats message in group on Monday")
            Assert.True(has "Использовано купонов",
                "Expected used section")
            Assert.True(has "Добавлено купонов",
                "Expected added section")
        }

    interface IAssemblyFixture<DefaultCouponHubTestContainers>
