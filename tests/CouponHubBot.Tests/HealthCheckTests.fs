namespace CouponHubBot.Tests

open System.Net
open System.Net.Http
open System.Text
open Xunit

type HealthCheckTests(fixture: DefaultCouponHubTestContainers) =

    [<Fact>]
    let ``GET /health returns 200 OK`` () =
        task {
            let! resp = fixture.Bot.GetAsync("/health")
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            Assert.Equal("OK", body)
        }

    [<Fact>]
    let ``GET /healthz returns 200 OK`` () =
        task {
            let! resp = fixture.Bot.GetAsync("/healthz")
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            let! body = resp.Content.ReadAsStringAsync()
            Assert.Equal("OK", body)
        }

    [<Fact>]
    let ``POST /bot with null body returns 400`` () =
        task {
            use content = new StringContent("null", Encoding.UTF8, "application/json")
            let! resp = fixture.Bot.PostAsync("/bot", content)
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode)
        }

