---
applyTo: "**"
excludeAgent: "coding-agent"
---

# Code Review Instructions for coupon-hub-bot

Review code changes against conventions and patterns in this F# / .NET 10 Telegram bot codebase. Prioritize bugs, security issues, missing validation, and convention violations over style.

## F# Conventions

- Always use `task { }` CE for async, never `async { }`. Use `let!` for awaiting, never `.Result` or `.Wait()` — they cause deadlocks in ASP.NET Core.
- All database-mapped records must have `[<CLIMutable>]` attribute for Dapper compatibility.
- Use `[<RequireQualifiedAccess>]` on discriminated unions to prevent name collisions.
- Nullable database columns use `string | null` annotation, not `string option` (for Dapper compatibility).
- Prefer exhaustive `match` expressions over nested `if/else`.
- New `.fs` files must be added to `.fsproj` in correct compilation order — F# compiles files sequentially and a file can only reference types defined in files listed above it.

## Telegram Bot Patterns

- Callback data uses colon-separated format: `"action:param1:param2"`. Always validate callback data parameters — users can craft arbitrary callback data.
- Always answer callback queries to dismiss the Telegram loading indicator.
- Verify community membership before processing coupon operations.
- All button labels and user-facing messages must be in Russian (Cyrillic).
- Wizard flows persist state in `PendingAddFlow` table. Each stage must validate the previous stage's data before proceeding.

## Database Conventions

- Migration files follow naming: `V{N}__{description}.sql` (sequential number, double underscore, snake_case).
- New tables and sequences must include `GRANT` for the `coupon_hub_bot_service` role.
- Use parameterized SQL queries only — never string-interpolate user input into SQL.

## Cyrillic / Russian Text

- All user-facing text must be in Russian.
- Never compare raw JSON strings containing Cyrillic — always parse with `JsonDocument` or `JsonSerializer` first, then compare parsed values.
- In tests, use `findCallWithText` helpers from `FakeCallHelpers.fs` which handle JSON parsing correctly.

```fsharp
// Correct — parsed comparison
Assert.True(findCallWithText calls 200L "Добавил купон", "Expected confirmation message")

// Wrong — raw string comparison will fail on Cyrillic
Assert.Contains("\"text\":\"Добавил купон\"", responseBody)
```

## Testing Patterns

- Tests use xUnit v3 with assembly fixtures and Testcontainers (PostgreSQL, Flyway, FakeTgApi, bot).
- Use `findCallWithText` to assert the bot sent a specific message to a specific chat.
- Use `findCallWithAnyText` when checking for any message to a chat without specific text matching.
- Time-dependent tests must use `BOT_FIXED_UTC_NOW` environment variable for deterministic behavior.

## Security

- Never commit secrets, tokens, or API keys — use environment variables.
- Validate all Telegram callback data — it can be crafted by malicious clients.
- Use parameterized SQL — never interpolate user input into queries.
- Verify community membership before allowing access to coupon operations.


