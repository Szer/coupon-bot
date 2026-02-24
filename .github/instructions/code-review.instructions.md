---
applyTo: "**"
excludeAgent: "coding-agent"
---

# Code Review Instructions for coupon-hub-bot

Review code changes against conventions and patterns established in this F# / .NET 10 Telegram bot codebase.

**Reviewer mindset:** Be polite but skeptical. Your job is to help speed the review process for maintainers — find problems the PR author may have missed and question whether the approach is correct. Treat the PR description and linked issues as claims to verify, not facts to accept.

## Review Guidelines

1. **Read the full source files** for every changed file (not just diff hunks) to understand invariants and state flow.
2. **Focus on what matters.** Prioritize bugs, incorrect state transitions, race conditions, missing validation, and convention violations. Do not comment on trivial style.
3. **Be specific and actionable.** Every comment should tell the author exactly what to change and why.
4. **Flag severity clearly:**
   - ❌ **error** — Must fix before merge. Bugs, security issues, convention violations.
   - ⚠️ **warning** — Should fix. Missing validation, inconsistency with patterns.
   - 💡 **suggestion** — Consider changing. Minor improvements.
   - ✅ **verified** — Confirmed correct. Use to show important aspects were checked.
5. **Don't pile on.** If the same issue appears many times, flag it once with a note listing all affected files.
6. **Don't flag what CI catches.** Assume `dotnet build -c Release` with `TreatWarningsAsErrors` and tests run separately.
7. **Verdict must match findings.** If you have ⚠️ findings, don't say LGTM. If uncertain, use "Needs Human Review."

---

## F# Conventions to Check

- **Always `task { }` CE for async**, never `async { }`. Use `let!` for awaiting, never `.Result` or `.Wait()`.
- **`[<CLIMutable>]`** on all database-mapped records (for Dapper).
- **`[<RequireQualifiedAccess>]`** on discriminated unions.
- **Nullable DB columns** use `string | null` annotation, not `string option`.
- **Exhaustive pattern matching** — avoid nested `if/else`, prefer `match`.
- **New `.fs` files** must be added to `.fsproj` in the correct compilation order.
- **No `.Result`, `.Wait()`, `Async.RunSynchronously`** in the request pipeline — causes deadlocks.

## Telegram Bot Patterns to Check

- **Callback data format**: `"action:param1:param2"` (colon-separated). Always validate — users can craft arbitrary callback data.
- **Always answer callback queries** to dismiss the loading indicator.
- **Verify community membership** before processing user actions.
- **Button labels must be in Russian** (Cyrillic).
- **Wizard flows** use `PendingAddFlow` table for state. Each stage validates the previous stage's data.

## Database Conventions to Check

- **Migration naming**: `V{N}__{description}.sql` (sequential number, double underscore, snake_case).
- **Always `GRANT`** for `coupon_hub_bot_service` role on new tables/sequences.
- **Parameterized SQL only** — never string-interpolate user input into queries.
- **`QuerySingle`, `QuerySingleOrDefault`, `Execute`** — correct cardinality for the query.

## Cyrillic / Russian Text Rules

- **All user-facing text must be in Russian.**
- **Never compare raw JSON strings containing Cyrillic.** Always parse with `JsonDocument` or `JsonSerializer` first.
- **In tests, use `findCallWithText` helpers** from `FakeCallHelpers.fs`.

## Testing Patterns to Check

- Tests use **xUnit v3 with assembly fixtures** and Testcontainers (PostgreSQL, Flyway, FakeTgApi, bot).
- Use **`findCallWithText`** for asserting bot messages, **`findCallWithAnyText`** for any-message checks.
- Tests that depend on time must use **`BOT_FIXED_UTC_NOW`** for deterministic behavior.

## Security

- Never commit secrets, tokens, or API keys.
- Validate all Telegram callback data.
- Use parameterized SQL.
- Verify community membership before coupon operations.

---

## Review Output Format

```
## 🤖 Copilot Code Review — PR #<number>

### Holistic Assessment

**Motivation**: <1-2 sentences on whether the PR is justified>

**Approach**: <1-2 sentences on whether the approach is right>

**Summary**: <✅ LGTM / ⚠️ Needs Human Review / ⚠️ Needs Changes / ❌ Reject>. <2-3 sentence summary.>

---

### Detailed Findings

#### ✅/⚠️/❌/💡 <Category> — <Brief description>

<Explanation with specifics. Reference code, line numbers, files.>
```

