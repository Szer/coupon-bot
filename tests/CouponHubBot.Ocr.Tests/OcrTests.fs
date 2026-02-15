namespace CouponHubBot.Ocr.Tests

open System
open System.Globalization
open System.IO
open System.Net
open System.Net.Http
open System.Text
open Microsoft.Extensions.Logging
open Xunit

open CouponHubBot
open CouponHubBot.Services

module private XUnitLogging =
    type private NoopScope() =
        interface IDisposable with
            member _.Dispose() = ()

    type XUnitLogger<'T>(output: ITestOutputHelper, sink: ResizeArray<string>) =
        let category = typeof<'T>.FullName

        interface ILogger<'T>

        interface ILogger with
            member _.BeginScope<'TState>(_state: 'TState) = new NoopScope() :> IDisposable

            member _.IsEnabled(_logLevel: LogLevel) = true

            member _.Log<'TState>(logLevel: LogLevel, _eventId: EventId, state: 'TState, ex: exn, formatter: Func<'TState, exn, string>) =
                try
                    let msg =
                        if isNull formatter then
                            if isNull (box state) then "" else state.ToString()
                        else
                            formatter.Invoke(state, ex)

                    let exText =
                        if isNull ex then ""
                        else "\n" + ex.ToString()

                    let line = $"[{logLevel}] {category}: {msg}{exText}"
                    sink.Add(line)
                    output.WriteLine(line)
                with _ ->
                    // Never let logging break tests
                    ()

module private AzureCache =
    let private tryFindProjectRoot () =
        // Walk up from AppContext.BaseDirectory until we find the test project file.
        let mutable dir = DirectoryInfo(AppContext.BaseDirectory)
        let mutable found: DirectoryInfo option = None
        while not (isNull dir) && found.IsNone do
            let probe = Path.Combine(dir.FullName, "CouponHubBot.Ocr.Tests.fsproj")
            if File.Exists(probe) then
                found <- Some dir
            else
                dir <- dir.Parent
        found

    let getCacheDir () =
        // Prefer placing cache next to the test project (tracked in git).
        match tryFindProjectRoot () with
        | Some root -> Path.Combine(root.FullName, "AzureCache")
        | None ->
            // Fallback: put it under base directory (still works, but won't be in repo root).
            Path.Combine(AppContext.BaseDirectory, "AzureCache")

    let cachePathForImageFileName (cacheDir: string) (imageFileName: string) =
        let safeName = Path.GetFileName(imageFileName)
        Path.Combine(cacheDir, safeName + ".azure.json")

type private AzureOcrCachingHandler(cachePath: string, allowNetwork: bool, log: string -> unit) =
    inherit HttpMessageHandler()

    let invoker = new HttpMessageInvoker(new HttpClientHandler())

    override _.SendAsync(request: HttpRequestMessage, cancellationToken: Threading.CancellationToken) =
        task {
            let dir = Path.GetDirectoryName(cachePath)
            if not (String.IsNullOrWhiteSpace dir) then
                Directory.CreateDirectory(dir) |> ignore

            if File.Exists(cachePath) then
                log $"Azure OCR cache hit: {Path.GetFileName(cachePath)}"
                let json: string = File.ReadAllText(cachePath, Encoding.UTF8)
                let resp = new HttpResponseMessage(HttpStatusCode.OK)
                resp.Content <- new StringContent(json, Encoding.UTF8, "application/json")
                return resp
            else
                if not allowNetwork then
                    return (failwithf
                        "Azure OCR cache miss (%s) and AZURE_OCR_ENDPOINT/AZURE_OCR_KEY are not set. Either set env vars to fetch & populate cache, or add the cache file to git."
                        (Path.GetFileName(cachePath)))
                else
                    // Buffer request content so we can both cache and forward.
                    let! bodyBytes = request.Content.ReadAsByteArrayAsync(cancellationToken)
                    let newContent = new ByteArrayContent(bodyBytes)
                    // Preserve content headers (especially Content-Type).
                    for h: Collections.Generic.KeyValuePair<string, Collections.Generic.IEnumerable<string>> in request.Content.Headers do
                        newContent.Headers.TryAddWithoutValidation(h.Key, h.Value) |> ignore
                    request.Content <- newContent

                    log $"Azure OCR cache miss: {Path.GetFileName(cachePath)} (fetching from Azure)"
                    use! response = invoker.SendAsync(request, cancellationToken)
                    let! json: string = response.Content.ReadAsStringAsync(cancellationToken)

                    // Cache raw Azure JSON response for future runs.
                    File.WriteAllText(cachePath, json, Encoding.UTF8)
                    log $"Azure OCR cached: {Path.GetFileName(cachePath)}"

                    // Replace content so downstream can read it.
                    let outResp = new HttpResponseMessage(response.StatusCode)
                    outResp.ReasonPhrase <- response.ReasonPhrase
                    for h: Collections.Generic.KeyValuePair<string, Collections.Generic.IEnumerable<string>> in response.Headers do
                        outResp.Headers.TryAddWithoutValidation(h.Key, h.Value) |> ignore
                    outResp.Content <- new StringContent(json, Encoding.UTF8, "application/json")
                    return outResp
        }

module private Parsing =
    // OCR test filenames may omit year (MM-dd). Make it deterministic by fixing "current year" to 2026.
    // This matches the repo's current test data and avoids dependence on real current time.
    let private fixedNowUtc = DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)

    let parseDecimalInvariant (s: string) =
        let s2 = s.Trim().Replace(',', '.')
        Decimal.Parse(s2, NumberStyles.Number, CultureInfo.InvariantCulture)

    let parseDate (s: string) =
        let raw = s.Trim()
        // Supported in filenames:
        // - yyyy-MM-dd (preferred)
        // - MM-dd (year inferred from current UTC year; screenshot-style)
        if raw.Length = 10 && raw[4] = '-' then
            DateTime.ParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None).Date
        else
            let md = DateTime.ParseExact(raw, [| "MM-dd"; "M-d" |], CultureInfo.InvariantCulture, DateTimeStyles.None)
            DateTime(fixedNowUtc.Year, md.Month, md.Day, 0, 0, 0, DateTimeKind.Utc).Date

    type Expected =
        { CouponValue: decimal
          MinCheck: decimal
          ValidFrom: DateTime
          ValidTo: DateTime
          Barcode: string }

    let parseExpectedFromFileName (fileNameNoExt: string) =
        // Format: [couponValue]_[minCheck]_[validFrom]_[validTo]_[barcode]
        let parts = fileNameNoExt.Split([| '_' |], StringSplitOptions.RemoveEmptyEntries)
        if parts.Length <> 5 then
            failwithf
                "Invalid OCR test filename '%s'. Expected 5 underscore-separated parts: value_minCheck_validFrom_validTo_barcode"
                fileNameNoExt

        { CouponValue = parseDecimalInvariant parts[0]
          MinCheck = parseDecimalInvariant parts[1]
          ValidFrom = parseDate parts[2]
          ValidTo = parseDate parts[3]
          Barcode = parts[4] }

type OcrTests(output: ITestOutputHelper) =

    let buildEngine (imageFileName: string) =
        let logs = ResizeArray<string>()
        let endpoint = Environment.GetEnvironmentVariable("AZURE_OCR_ENDPOINT")
        let key = Environment.GetEnvironmentVariable("AZURE_OCR_KEY")
        let allowNetwork = (not (String.IsNullOrWhiteSpace endpoint) && not (String.IsNullOrWhiteSpace key))

        let effectiveEndpoint = if String.IsNullOrWhiteSpace endpoint then "https://example.com" else endpoint
        let effectiveKey = if String.IsNullOrWhiteSpace key then "cache-only" else key

        let botConf: BotConfiguration =
            { BotToken = "test"
              SecretToken = "test"
              CommunityChatId = 0L
              TelegramApiBaseUrl = null
              ReminderHourUtc = 8
              ReminderRunOnStart = false
              // Keep OCR enabled so we always go through HttpClient (cache handler may short-circuit).
              OcrEnabled = true
              OcrMaxFileSizeBytes = 50L * 1024L * 1024L
              AzureOcrEndpoint = effectiveEndpoint
              AzureOcrKey = effectiveKey
              FeedbackAdminIds = [||]
              TestMode = true
              MaxTakenCoupons = 4 }

        let cacheDir = AzureCache.getCacheDir ()
        let cachePath = AzureCache.cachePathForImageFileName cacheDir imageFileName
        let handler = new AzureOcrCachingHandler(cachePath, allowNetwork, fun s -> output.WriteLine(s))
        let http = new HttpClient(handler)

        let azure = AzureOcrService(http, botConf, XUnitLogging.XUnitLogger<AzureOcrService>(output, logs)) :> IAzureTextOcr

        // Freeze "now" to 2026 so year inference is stable.
        let timeProvider =
            CouponHubBot.Time.FixedTimeProvider(DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)) :> TimeProvider

        let engine: CouponOcrEngine =
            CouponOcrEngine(azure, XUnitLogging.XUnitLogger<CouponOcrEngine>(output, logs), timeProvider)
        engine, http, logs

    [<Theory>]
    [<InlineData("10_50_01-04_01-13_2706602781191.jpg")>]
    [<InlineData("10_50_01-06_01-15_2706643333717.jpg")>]
    [<InlineData("10_50_01-12_01-21_2706513420233.jpg")>]
    [<InlineData("10_50_01-12_01-21_2706530490622.jpg")>]
    [<InlineData("10_50_01-14_01-23_2706658654210.jpg")>]
    [<InlineData("10_50_01-19_01-28_2706613152454.jpg")>]
    [<InlineData("10_50_01-21_01-30_2706616470579.jpg")>]
    [<InlineData("5_25_01-15_01-21_2706528422291.jpg")>]
    [<InlineData("5_25_01-15_01-21_2706666377231.jpg")>]
    [<InlineData("5_25_01-15_01-21_2706684806638.jpg")>]
    [<InlineData("5_25_2026-02-08_2026-02-14_2706726228947.jpg")>]
    [<InlineData("10_50_2026-01-11_2026-01-20_2706678568818.jpg")>]
    [<InlineData("10_50_2026-01-17_2026-01-26_2706688198838.jpg")>]
    [<InlineData("10_50_2026-01-17_2026-01-26_2706688198845.jpg")>]
    [<InlineData("10_50_2026-01-17_2026-01-26_2706688198821.jpg")>]
    member _.``OCR engine recognizes coupon from file``(fileName: string) =
        task {
            let imagesDir = Path.Combine(AppContext.BaseDirectory, "Images")
            if not (Directory.Exists imagesDir) then
                failwithf "Images folder not found at '%s'. Place test images under tests/CouponHubBot.Ocr.Tests/Images/." imagesDir

            let path = Path.Combine(imagesDir, fileName)
            if not (File.Exists(path)) then
                failwithf "Image file not found: '%s' (looked in '%s')" fileName imagesDir

            let engine, http, logs = buildEngine fileName
            use _http = http

            let nameNoExt = Path.GetFileNameWithoutExtension(path)
            let expected = Parsing.parseExpectedFromFileName nameNoExt

            let bytes = File.ReadAllBytes(path)
            let! res = engine.Recognize(ReadOnlyMemory<byte>(bytes))

            // couponValue
            Assert.True(res.couponValue.HasValue, $"Expected couponValue from '{nameNoExt}'")
            Assert.Equal(expected.CouponValue, res.couponValue.Value)

            // minCheck
            Assert.True(res.minCheck.HasValue, $"Expected minCheck from '{nameNoExt}'")
            Assert.Equal(expected.MinCheck, res.minCheck.Value)

            // validFrom
            Assert.True(res.validFrom.HasValue, $"Expected validFrom from '{nameNoExt}'")
            Assert.Equal(expected.ValidFrom, res.validFrom.Value.Date)

            // validTo
            Assert.True(res.validTo.HasValue, $"Expected validTo from '{nameNoExt}'")
            Assert.Equal(expected.ValidTo, res.validTo.Value.Date)

            // barcode
            if String.IsNullOrWhiteSpace res.barcode then
                let dump = String.concat "\n" (Seq.toList logs)
                failwithf "Expected barcode from '%s'\n\nLogs:\n%s" nameNoExt dump
            Assert.Equal(expected.Barcode, res.barcode)
        }

    [<Theory>]
    // Low-quality images: assert only fields that are reliable for this file.
    // Pass null for fields we intentionally do not assert.
    [<InlineData("5_25_2026-01-11_2026-01-17_2706653336241.jpg", "5", "25", "2026-01-11", null, null)>]
    [<InlineData("5_25_2026-01-20_2026-01-26_2706680353051.jpg", "5", "25", "2026-01-20", "2026-01-26", null)>]
    member _.``OCR engine recognizes coupon from low quality file``(
        fileName: string,
        expectedCouponValue: string,
        expectedMinCheck: string,
        expectedValidFrom: string,
        expectedValidTo: string,
        expectedBarcode: string
    ) =
        task {
            let imagesDir = Path.Combine(AppContext.BaseDirectory, "Images")
            if not (Directory.Exists imagesDir) then
                failwithf "Images folder not found at '%s'. Place test images under tests/CouponHubBot.Ocr.Tests/Images/." imagesDir

            let path = Path.Combine(imagesDir, fileName)
            if not (File.Exists(path)) then
                failwithf "Image file not found: '%s' (looked in '%s')" fileName imagesDir

            let engine, http, logs = buildEngine fileName
            use _http = http

            let bytes = File.ReadAllBytes(path)
            let! res = engine.Recognize(ReadOnlyMemory<byte>(bytes))

            let assertMoney (label: string) (expected: string) (actual: Nullable<decimal>) =
                let exp = Decimal.Parse(expected, NumberStyles.Number, CultureInfo.InvariantCulture)
                Assert.True(actual.HasValue, $"Expected {label} from '{fileName}'")
                Assert.Equal(exp, actual.Value)

            let assertDate (label: string) (expected: string) (actual: Nullable<DateTime>) =
                let exp = Parsing.parseDate expected
                Assert.True(actual.HasValue, $"Expected {label} from '{fileName}'")
                Assert.Equal(exp, actual.Value.Date)

            // couponValue + minCheck always asserted for low-quality list
            assertMoney "couponValue" expectedCouponValue res.couponValue
            assertMoney "minCheck" expectedMinCheck res.minCheck

            // validFrom/validTo: only if expected is provided (non-null)
            if not (isNull expectedValidFrom) then
                assertDate "validFrom" expectedValidFrom res.validFrom

            if not (isNull expectedValidTo) then
                assertDate "validTo" expectedValidTo res.validTo

            // barcode: only assert if expected is provided (non-null)
            if not (isNull expectedBarcode) then
                if String.IsNullOrWhiteSpace res.barcode then
                    let dump = String.concat "\n" (Seq.toList logs)
                    failwithf "Expected barcode from '%s'\n\nLogs:\n%s" fileName dump
                Assert.Equal(expectedBarcode, res.barcode)
        }
