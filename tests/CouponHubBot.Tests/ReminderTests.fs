namespace CouponHubBot.Tests

open System.Net
open System.Threading.Tasks
open System.Text
open System.Net.Http
open Dapper
open Npgsql
open Xunit
open Xunit.Extensions.AssemblyFixture

type ReminderTests(fixture: DefaultCouponHubTestContainers) =

    [<Fact>]
    let ``Reminder sends message to group when coupons expire today`` () =
        task {
            do! fixture.ClearFakeCalls()

            // Seed an expiring coupon
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            //language=postgresql
            do! conn.ExecuteAsync("INSERT INTO \"user\"(id, username, first_name, created_at, updated_at) VALUES (500,'owner','Owner',NOW(),NOW()) ON CONFLICT DO NOTHING;") :> Task
            do! conn.ExecuteAsync("INSERT INTO coupon(owner_id, photo_file_id, value, expires_at, status) VALUES (500,'seed-photo',10.00,CURRENT_DATE,'available');") :> Task

            // Trigger reminder via test endpoint
            use body = new StringContent("", Encoding.UTF8, "application/json")
            let! _ = fixture.Bot.PostAsync("/test/run-reminder", body)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(calls |> Array.exists (fun c -> c.Body.Contains("\"chat_id\":-42") && c.Body.Contains("Сегодня истекают")))
        }

    interface IAssemblyFixture<DefaultCouponHubTestContainers>

