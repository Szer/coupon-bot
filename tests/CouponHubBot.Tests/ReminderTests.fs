namespace CouponHubBot.Tests

open System.Threading.Tasks
open System.Text
open System.Net.Http
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

    interface IAssemblyFixture<DefaultCouponHubTestContainers>
