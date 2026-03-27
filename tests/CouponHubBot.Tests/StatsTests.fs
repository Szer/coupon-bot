namespace CouponHubBot.Tests

open System.Net
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit
open FakeCallHelpers

type StatsTests(fixture: DefaultCouponHubTestContainers) =

    let seedUser (conn: NpgsqlConnection) (userId: int64) (username: string) =
        conn.ExecuteAsync(
            """
INSERT INTO "user"(id, username, first_name, created_at, updated_at)
VALUES (@id, @username, @username, NOW(), NOW())
ON CONFLICT (id) DO NOTHING;
""",
            {| id = userId; username = username |}
        )

    [<Fact>]
    let ``Stats shows personal outcome breakdown with correct counts and utilization`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            let userId = 9100L
            let! _ = seedUser conn userId "stats_user_9100"

            // 2 used, 2 expired (1 available + 1 taken, past date), 1 active, 1 voided
            do!
                conn.ExecuteAsync(
                    """
INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES
  (9100, 'stats-p1', 10, 50, '2025-06-01', 'used'),
  (9100, 'stats-p2', 10, 50, '2025-06-01', 'used'),
  (9100, 'stats-p3', 10, 50, '2025-06-01', 'available'),
  (9100, 'stats-p4', 10, 50, '2025-06-01', 'taken'),
  (9100, 'stats-p5', 10, 50, '2026-06-01', 'available'),
  (9100, 'stats-p6', 10, 50, '2026-06-01', 'voided');
"""
                )
                :> Task

            let user = Tg.user(id = userId, username = "stats_user_9100", firstName = "StatsUser")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let! resp = fixture.SendUpdate(Tg.dmMessage("/stats", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")

            Assert.True(findCallWithText calls userId "Судьба моих купонов",
                "Expected personal outcomes section header")
            Assert.True(findCallWithText calls userId "Использовано: 2",
                "Expected 2 used in personal section")
            Assert.True(findCallWithText calls userId "Истекло неиспользованными: 2",
                "Expected 2 expired in personal section")
            Assert.True(findCallWithText calls userId "Сейчас активны: 1",
                "Expected 1 active in personal section")
            Assert.True(findCallWithText calls userId "Аннулировано: 1",
                "Expected 1 voided in personal section")
            // 2 used / (2 used + 2 expired) = 50%
            Assert.True(findCallWithText calls userId "Утилизация: 50%",
                "Expected 50% utilization rate")
        }

    [<Fact>]
    let ``Stats shows global community section`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            let userId = 9101L
            let! _ = seedUser conn userId "stats_user_9101"

            do!
                conn.ExecuteAsync(
                    """
INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES
  (9101, 'global-p1', 10, 50, '2025-06-01', 'used'),
  (9101, 'global-p2', 10, 50, '2026-06-01', 'available');
"""
                )
                :> Task

            let user = Tg.user(id = userId, username = "stats_user_9101", firstName = "Global")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let! resp = fixture.SendUpdate(Tg.dmMessage("/stats", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")

            Assert.True(findCallWithText calls userId "Сообщество (всего)",
                "Expected global stats section header")
            Assert.True(findCallWithText calls userId "Добавлено: 2",
                "Expected global total_count = 2")
        }

    [<Fact>]
    let ``Stats shows 100 percent utilization when all added coupons were used`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            let userId = 9102L
            let! _ = seedUser conn userId "stats_user_9102"

            do!
                conn.ExecuteAsync(
                    """
INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES
  (9102, 'full-p1', 10, 50, '2025-06-01', 'used'),
  (9102, 'full-p2', 10, 50, '2025-06-01', 'used'),
  (9102, 'full-p3', 10, 50, '2025-06-01', 'used');
"""
                )
                :> Task

            let user = Tg.user(id = userId, username = "stats_user_9102", firstName = "FullRate")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let! resp = fixture.SendUpdate(Tg.dmMessage("/stats", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")

            Assert.True(findCallWithText calls userId "Утилизация: 100%",
                "Expected 100% utilization when all coupons were used")
        }

    [<Fact>]
    let ``Stats shows dash for utilization when user has no terminal coupons`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            let userId = 9103L
            let! _ = seedUser conn userId "stats_user_9103"

            // One active coupon (not expired, not used) — denominator is 0
            do!
                conn.ExecuteAsync(
                    """
INSERT INTO coupon(owner_id, photo_file_id, value, min_check, expires_at, status)
VALUES (9103, 'dash-p1', 10, 50, '2026-06-01', 'available');
"""
                )
                :> Task

            let user = Tg.user(id = userId, username = "stats_user_9103", firstName = "NoTerminal")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let! resp = fixture.SendUpdate(Tg.dmMessage("/stats", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")

            Assert.True(findCallWithText calls userId "Утилизация: —",
                "Expected dash when no used or expired coupons exist")
            Assert.True(findCallWithText calls userId "Сейчас активны: 1",
                "Expected 1 active coupon")
        }

    [<Fact>]
    let ``Stats works for user who has never added any coupons`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            let userId = 9104L
            let! _ = seedUser conn userId "stats_user_9104"

            let user = Tg.user(id = userId, username = "stats_user_9104", firstName = "NoCoupons")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let! resp = fixture.SendUpdate(Tg.dmMessage("/stats", user))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")

            Assert.True(findCallWithText calls userId "Утилизация: —",
                "Expected dash for user with no coupons at all")
            Assert.True(findCallWithText calls userId "Сейчас активны: 0",
                "Expected zero active coupons")
        }
