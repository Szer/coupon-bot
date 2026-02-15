# Testing

## Docker Suite (Testcontainers)

E2E tests live in `tests/CouponHubBot.Tests/`. They use Testcontainers to spin up:

- **PostgreSQL 15.6** — application database
- **Flyway** — runs migrations against the test database
- **Bot container** — the application itself (built from `./Dockerfile`)
- **FakeTgApi** — mock Telegram Bot API (built from `tests/FakeTgApi/Dockerfile`)
- **FakeAzureOcrApi** — mock Azure OCR (only for OCR tests, built from `tests/FakeAzureOcrApi/Dockerfile`)

All containers run in a shared Docker network. Tests interact via HTTP against mapped host ports.

### Running Tests

```bash
dotnet test -c Release
```

Requires Docker running locally. Tests are serialized (parallelization disabled).

### Container Logs

On test completion (pass or fail), container logs are automatically dumped to `test-artifacts/` directory:
- `test-artifacts/DefaultCouponHubTestContainers/bot.log`
- `test-artifacts/DefaultCouponHubTestContainers/fake-tg-api.log`
- `test-artifacts/DefaultCouponHubTestContainers/postgres.log`
- `test-artifacts/OcrCouponHubTestContainers/` (same structure, for OCR fixtures)

In test code, use `fixture.GetBotLogs()` or `fixture.GetAllLogs()` for on-demand access.

### Test Fixtures

- `DefaultCouponHubTestContainers` — standard fixture (no OCR)
- `OcrCouponHubTestContainers` — fixture with OCR enabled (includes FakeAzureOcrApi)

Fixtures are registered as assembly-level fixtures (xUnit v3 `AssemblyFixture` attribute) so they're shared across all tests in the assembly.

## FakeTgApi

Mock Telegram Bot API that logs all calls.

### Implemented endpoints
`sendMessage`, `sendPhoto`, `sendMediaGroup`, `forwardMessage`, `answerCallbackQuery`, `getChatMember`, `getFile`, `deleteMessage`

### Test endpoints
- `GET /test/calls?method=sendMessage` — get logged calls filtered by method
- `DELETE /test/calls` — clear logged calls
- `POST /test/mock/chatMember` — mock chat membership: `{ userId, status }`
- `POST /test/mock/file` — mock file download: `{ fileId, contentBase64 }`

### Cyrillic in JSON body
Never do `call.Body.Contains("...русский текст...")` on raw JSON strings: Cyrillic often arrives as `\uXXXX`.
Always parse `call.Body` through `JsonDocument.Parse(...)` and compare strings via `.GetString()` (does unescape).

### File downloads (DownloadFile) — important for ZXing barcode scanning
- `POST /test/mock/file` with `{ fileId, contentBase64 }` stores real bytes in `Store.files`
- `getFile` returns `file_path = photos/{fileId}.jpg`
- `GET /file/bot{token}/photos/{fileId}.jpg` returns those bytes

## FakeAzureOcrApi

Mock Azure Computer Vision OCR API.

- Endpoint: `POST /computervision/imageanalysis:analyze?overload=stream&api-version=2024-02-01&features=read`
- Test endpoints:
  - `POST /test/mock/response` with `{ status, body }` — override response (usually `body` from `tests/CouponHubBot.Ocr.Tests/AzureCache/*.azure.json`)
  - `GET /test/calls` / `DELETE /test/calls` — debug

## OCR Test Suite (no Docker)

`tests/CouponHubBot.Ocr.Tests/` tests `CouponOcrEngine` against images in `Images/`.
Azure OCR responses are cached in `AzureCache/` (git-tracked) and can run cache-only without Azure API calls.
