namespace CouponHubBot.Tests

open System.Net
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

