namespace CouponHubBot.Tests

open System
open System.IO
open System.Net
open System.Threading.Tasks
open DotNet.Testcontainers.Configurations
open Xunit
open Xunit.Extensions.AssemblyFixture
open FakeCallHelpers
open DotNet.Testcontainers.Builders

type OcrAddFlowTests(fixture: OcrCouponHubTestContainers) =

    let solutionDirPath = CommonDirectoryPath.GetSolutionDirectory().DirectoryPath

    let readImageBytes (fileName: string) =
        File.ReadAllBytes(Path.Combine(solutionDirPath, "tests", "CouponHubBot.Ocr.Tests", "Images", fileName))

    let readAzureCacheJson (fileName: string) =
        File.ReadAllText(Path.Combine(solutionDirPath, "tests", "CouponHubBot.Ocr.Tests", "AzureCache", fileName + ".azure.json"))

    let getLatestExpiresIso () =
        fixture.QuerySingle<string>("SELECT expires_at::text FROM coupon ORDER BY id DESC LIMIT 1", null)

    let getLatestValue () =
        fixture.QuerySingle<decimal>("SELECT value FROM coupon ORDER BY id DESC LIMIT 1", null)

    let getLatestMinCheck () =
        fixture.QuerySingle<decimal>("SELECT min_check FROM coupon ORDER BY id DESC LIMIT 1", null)

    [<Fact>]
    let ``/add wizard OCR: fully recognized -> confirm -> coupon created`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let user = Tg.user(id = 600L, username = "ocr_ok", firstName = "OCR")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let fileName = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            let fileId = "ocr-photo-ok"

            do! fixture.SetTelegramFile(fileId, readImageBytes fileName)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson fileName)

            let! resp1 = fixture.SendUpdate(Tg.dmMessage("/add", user))
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode)

            let! calls1 = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls1 user.Id "Пришли фото купона", "Expected wizard to ask for photo")

            do! fixture.ClearFakeCalls()
            let! resp2 = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId))
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode)

            let! calls2 = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls2 user.Id "Я распознал:", "Expected OCR prefill message")
            Assert.True(calls2 |> Array.exists (fun c -> c.Body.Contains("addflow:ocr:yes") && c.Body.Contains("addflow:ocr:no")),
                "Expected OCR yes/no buttons")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:ocr:yes", user))
            let! calls3 = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls3 user.Id "Подтвердить добавление купона", "Expected confirm screen after OCR yes")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:confirm", user))
            let! calls4 = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls4 user.Id "Добавил купон", "Expected success message after confirm")

            let! v = getLatestValue ()
            let! mc = getLatestMinCheck ()
            let! expiresIso = getLatestExpiresIso ()
            Assert.Equal(10m, v)
            Assert.Equal(50m, mc)
            Assert.Equal("2026-01-26", expiresIso)
        }

    [<Fact>]
    let ``/add wizard OCR: fully recognized but user overrides -> manual values -> coupon created`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let user = Tg.user(id = 601L, username = "ocr_override", firstName = "OCR")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let fileName = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            let fileId = "ocr-photo-override"

            do! fixture.SetTelegramFile(fileId, readImageBytes fileName)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson fileName)

            let! _ = fixture.SendUpdate(Tg.dmMessage("/add", user))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:ocr:no", user))
            let! callsNo = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsNo user.Id "выбери скидку", "Expected wizard to go to manual discount after OCR no")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:disc:5:25", user))
            let! callsDisc = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsDisc user.Id "дату истечения", "Expected wizard to ask for date after discount")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:date:other", user))
            let! callsAsk = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsAsk user.Id "Пришли дату", "Expected custom date prompt")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("2026-02-01", user))
            let! callsConfirm = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsConfirm user.Id "Подтвердить добавление купона", "Expected confirm after manual date")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:confirm", user))
            let! callsDone = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsDone user.Id "Добавил купон", "Expected success message after confirm")

            let! v = getLatestValue ()
            let! mc = getLatestMinCheck ()
            let! expiresIso = getLatestExpiresIso ()
            Assert.Equal(5m, v)
            Assert.Equal(25m, mc)
            Assert.Equal("2026-02-01", expiresIso)
        }

    [<Fact>]
    let ``/add wizard OCR: partial (no date) -> asks date -> user completes -> coupon created`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let user = Tg.user(id = 602L, username = "ocr_partial", firstName = "OCR")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let fileName = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            let fileId = "ocr-photo-partial"

            do! fixture.SetTelegramFile(fileId, readImageBytes fileName)
            // Only provide amounts, no dates -> OCR should prefill discount/min_check, then ask for expiry date.
            let partialBody =
                """{"readResult":{"content":"SAVE €10\nWhen you spend €50\n","blocks":[]}}"""
            do! fixture.SetAzureOcrResponse(200, partialBody)

            let! _ = fixture.SendUpdate(Tg.dmMessage("/add", user))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId))

            let! calls2 = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls2 user.Id "выбери дату", "Expected wizard to ask for date when OCR has no date")
            Assert.False(calls2 |> Array.exists (fun c -> c.Body.Contains("addflow:ocr:yes")),
                "Did not expect OCR yes/no step when expiry date is missing")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:date:other", user))
            let! callsAsk = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsAsk user.Id "Пришли дату", "Expected custom date prompt")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("2026-02-03", user))
            let! callsConfirm = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsConfirm user.Id "Подтвердить добавление купона", "Expected confirm after providing date")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:confirm", user))
            let! callsDone = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsDone user.Id "Добавил купон", "Expected success message after confirm")

            let! v = getLatestValue ()
            let! mc = getLatestMinCheck ()
            let! expiresIso = getLatestExpiresIso ()
            Assert.Equal(10m, v)
            Assert.Equal(50m, mc)
            Assert.Equal("2026-02-03", expiresIso)
        }

    interface IAssemblyFixture<OcrCouponHubTestContainers>

