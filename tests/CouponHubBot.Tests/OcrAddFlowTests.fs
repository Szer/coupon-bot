namespace CouponHubBot.Tests

open System
open System.IO
open System.Net
open System.Text.Json
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

    let getCouponCount () =
        fixture.QuerySingle<int64>("SELECT COUNT(*)::bigint FROM coupon", null)

    [<Fact>]
    let ``Implicit /add: photo with no pending flows starts OCR wizard`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let user = Tg.user(id = 603L, username = "ocr_implicit", firstName = "OCR")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let fileName = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            let fileId = "ocr-photo-implicit"

            do! fixture.SetTelegramFile(fileId, readImageBytes fileName)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson fileName)

            let! resp = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "Я распознал:", "Expected OCR prefill message after implicit add")
            Assert.True(calls |> Array.exists (fun c -> c.Body.Contains("addflow:ocr:yes") && c.Body.Contains("addflow:ocr:no")),
                "Expected OCR yes/no buttons")

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:ocr:yes", user))
            let! callsDone = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText callsDone user.Id "Добавил купон", "Expected coupon to be created after OCR yes")
        }

    [<Fact>]
    let ``OCR add: duplicate barcode (non-expired) is rejected (different photo ids)`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let user = Tg.user(id = 650L, username = "ocr_dup_barcode", firstName = "OCR")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let fileName = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            let azure = readAzureCacheJson fileName
            let bytes = readImageBytes fileName

            // First add (barcode should be decoded from real image bytes).
            let fileId1 = "ocr-dup-barcode-1"
            do! fixture.SetTelegramFile(fileId1, bytes)
            do! fixture.SetAzureOcrResponse(200, azure)
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId1))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:ocr:yes", user))

            let! count1 = getCouponCount ()
            Assert.Equal(1L, count1)

            // Second add with a different photo_file_id, but the same underlying image => same barcode.
            do! fixture.ClearFakeCalls()
            let fileId2 = "ocr-dup-barcode-2"
            do! fixture.SetTelegramFile(fileId2, bytes)
            do! fixture.SetAzureOcrResponse(200, azure)
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId2))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:ocr:yes", user))

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithAnyText calls user.Id [| "штрихкод"; "штрихкодом" |],
                "Expected duplicate barcode rejection message")

            let! count2 = getCouponCount ()
            Assert.Equal(1L, count2)
        }

    [<Fact>]
    let ``OCR add: if barcode is not recognized (NULL), barcode dedupe does not block`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let user = Tg.user(id = 651L, username = "ocr_no_barcode", firstName = "OCR")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let fileName = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"
            let azure = readAzureCacheJson fileName

            // Provide invalid bytes so ZXing/ImageSharp fails -> barcode = null,
            // while FakeAzureOcrApi still returns a deterministic OCR response.
            let bytes = [| byte 0x00; byte 0x01; byte 0x02; byte 0x03 |]

            let fileId1 = "ocr-no-barcode-1"
            do! fixture.SetTelegramFile(fileId1, bytes)
            do! fixture.SetAzureOcrResponse(200, azure)
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId1))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:ocr:yes", user))

            let! count1 = getCouponCount ()
            Assert.Equal(1L, count1)

            do! fixture.ClearFakeCalls()
            let fileId2 = "ocr-no-barcode-2"
            do! fixture.SetTelegramFile(fileId2, bytes)
            do! fixture.SetAzureOcrResponse(200, azure)
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = fileId2))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmCallback("addflow:ocr:yes", user))

            let! calls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText calls user.Id "Добавил купон", "Expected second coupon to be created when barcode is NULL")

            let! count2 = getCouponCount ()
            Assert.Equal(2L, count2)
        }

    [<Fact>]
    let ``Implicit /add: does not start while /feedback pending (photo is forwarded)`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()

            let user = Tg.user(id = 604L, username = "fb_blocks_implicit", firstName = "FB")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! _ = fixture.SendUpdate(Tg.dmMessage("/feedback", user))

            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = "any-photo-id"))

            let! fwCalls = fixture.GetFakeCalls("forwardMessage")
            Assert.Equal(2, fwCalls.Length)

            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls user.Id "Спасибо", "Expected user confirmation after forwarding photo feedback")

            // IMPORTANT: FakeTgApi stores JSON with \uXXXX escapes for Cyrillic.
            // Always parse JSON and read strings via GetString() to compare Russian text.
            let texts =
                msgCalls
                |> Array.choose (fun c ->
                    try
                        use doc = JsonDocument.Parse(c.Body)
                        let root = doc.RootElement
                        match root.TryGetProperty("text") with
                        | true, t when t.ValueKind = JsonValueKind.String -> Some(t.GetString())
                        | _ -> None
                    with _ ->
                        None)

            Assert.False(
                texts |> Array.exists (fun t -> not (isNull t) && t.Contains("Я распознал:")),
                "Did not expect implicit /add OCR flow during feedback"
            )

            let hasAddFlowButtons =
                msgCalls
                |> Array.exists (fun c ->
                    try
                        use doc = JsonDocument.Parse(c.Body)
                        let root = doc.RootElement
                        match root.TryGetProperty("reply_markup") with
                        | true, rm ->
                            match rm.TryGetProperty("inline_keyboard") with
                            | true, kb when kb.ValueKind = JsonValueKind.Array ->
                                kb.EnumerateArray()
                                |> Seq.exists (fun row ->
                                    row.EnumerateArray()
                                    |> Seq.exists (fun btn ->
                                        match btn.TryGetProperty("callback_data") with
                                        | true, cd when cd.ValueKind = JsonValueKind.String ->
                                            let s = cd.GetString()
                                            not (isNull s) && s.StartsWith("addflow:")
                                        | _ -> false))
                            | _ -> false
                        | _ -> false
                    with _ ->
                        false)

            Assert.False(hasAddFlowButtons, "Did not expect implicit /add flow buttons during feedback (message should be forwarded only)")
        }

    [<Fact>]
    let ``/add wizard OCR: fully recognized -> user confirms -> coupon created`` () =
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
            Assert.True(findCallWithText calls3 user.Id "Добавил купон", "Expected success message right after OCR yes")

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

