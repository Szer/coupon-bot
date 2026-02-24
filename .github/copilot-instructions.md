# Coupon Hub Bot

Telegram bot for collaborative coupon management in a private community. Written in **F# / .NET 10**.

## Tech Stack

- **F# / .NET 10**, ASP.NET Core (webhook), PostgreSQL, Dapper
- **Telegram.Bot 22.8.1** — bot framework
- Flyway — database migrations
- Docker — containerization, Testcontainers for E2E tests
- OpenTelemetry — traces and metrics, Serilog — structured logging

## Documentation Map

| Topic | File |
|-------|------|
| Architecture | [docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md) |
| Bot Logic | [docs/TELEGRAM-BOT-LOGIC.md](../docs/TELEGRAM-BOT-LOGIC.md) |
| Testing | [docs/TESTING.md](../docs/TESTING.md) |
| Database | [docs/DATABASE.md](../docs/DATABASE.md) |
| OCR | [docs/OCR.md](../docs/OCR.md) |
| Deployment | [docs/DEPLOYMENT.md](../docs/DEPLOYMENT.md) |
| Observability | [docs/OBSERVABILITY.md](../docs/OBSERVABILITY.md) |

## Key Conventions

- UI text is in **Russian** (кириллица). Never compare raw JSON for Cyrillic — always parse with `JsonDocument`.
- F# idioms: `task { }` CE for async, records with `[<CLIMutable>]` for Dapper, `option` types.
- Database role: `coupon_hub_bot_service`. Always add `GRANT` in migrations for new tables.
- F# compilation order matters — new `.fs` files must be added to `.fsproj` in the correct position.
- `TreatWarningsAsErrors` is enabled — all warnings are errors.

---

## Code Review Rules

When reviewing pull requests, check the following project-specific conventions. Prioritize bugs, security issues, missing validation, and convention violations over style.

### F# Conventions

- Always use `task { }` CE for async, never `async { }`. Use `let!` for awaiting, never `.Result` or `.Wait()` — they cause deadlocks in ASP.NET Core.
- All database-mapped records must have `[<CLIMutable>]` attribute for Dapper compatibility.
- Use `[<RequireQualifiedAccess>]` on discriminated unions to prevent name collisions.
- Nullable database columns use `string | null` annotation, not `string option` (for Dapper compatibility).
- Prefer exhaustive `match` expressions over nested `if/else`.
- New `.fs` files must be added to `.fsproj` in correct compilation order — F# compiles files sequentially and a file can only reference types defined in files listed above it.

### Telegram Bot Patterns

- Callback data uses colon-separated format: `"action:param1:param2"`. Always validate callback data parameters — users can craft arbitrary callback data.
- Always answer callback queries to dismiss the Telegram loading indicator.
- Verify community membership before processing coupon operations.
- All button labels and user-facing messages must be in Russian (Cyrillic).
- Wizard flows persist state in `PendingAddFlow` table. Each stage must validate the previous stage's data before proceeding.

### Database Conventions

- Migration files follow naming: `V{N}__{description}.sql` (sequential number, double underscore, snake_case).
- New tables and sequences must include `GRANT` for the `coupon_hub_bot_service` role.
- Use parameterized SQL queries only — never string-interpolate user input into SQL.

### Cyrillic / Russian Text

- All user-facing text must be in Russian.
- Never compare raw JSON strings containing Cyrillic — always parse with `JsonDocument` or `JsonSerializer` first, then compare parsed values.
- In tests, use `findCallWithText` helpers from `FakeCallHelpers.fs` which handle JSON parsing correctly.

```fsharp
// Correct — parsed comparison
Assert.True(findCallWithText calls 200L "Добавил купон", "Expected confirmation message")

// Wrong — raw string comparison will fail on Cyrillic
Assert.Contains("\"text\":\"Добавил купон\"", responseBody)
```

### Testing Patterns

- Tests use xUnit v3 with assembly fixtures and Testcontainers (PostgreSQL, Flyway, FakeTgApi, bot).
- Use `findCallWithText` to assert the bot sent a specific message to a specific chat.
- Use `findCallWithAnyText` when checking for any message to a chat without specific text matching.
- Time-dependent tests must use `BOT_FIXED_UTC_NOW` environment variable for deterministic behavior.

### Security

- Never commit secrets, tokens, or API keys — use environment variables.
- Validate all Telegram callback data — it can be crafted by malicious clients.
- Use parameterized SQL — never interpolate user input into queries.
- Verify community membership before allowing access to coupon operations.
