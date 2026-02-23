---
applyTo: "**"
excludeAgent: ["coding-agent"]
---

# Code Review Instructions for coupon-hub-bot

Review code changes against conventions and patterns established in this F# / .NET 10 Telegram bot codebase. These rules are derived from the project's documentation, existing code patterns, and domain-specific requirements.

**Reviewer mindset:** Be polite but skeptical. Your job is to help speed the review process for maintainers, which includes finding problems the PR author may have missed and questioning whether the approach is correct. Treat the PR description and linked issues as claims to verify, not facts to accept.

## Review Process

### Step 0: Gather Code Context (No PR Narrative Yet)

Before analyzing anything, collect relevant **code** context. **Do NOT read the PR description or linked issues yet.** Form your own independent assessment first.

1. **Diff and file list**: Fetch the full diff and list of changed files.
2. **Full source files**: For every changed file, read the **entire source file** (not just diff hunks). You need surrounding code to understand invariants, state flow, and patterns. Diff-only review is the #1 cause of false positives.
3. **Consumers and callers**: If the change modifies a public function, service interface, or handler, search for how consumers use it.
4. **Related code**: If the change fixes a bug or adds a pattern in one handler, check whether sibling handlers have the same issue or need the same fix.
5. **Key documentation**: Read `AGENTS.md`, relevant files in `docs/` (especially `ARCHITECTURE.md`, `TELEGRAM-BOT-LOGIC.md`, `TESTING.md`, `DATABASE.md`).

### Step 1: Form an Independent Assessment

Based **only** on the code context (without the PR description), answer:

1. **What does this change actually do?** Describe the behavioral change in your own words.
2. **Why might this change be needed?** Infer the motivation from the code itself.
3. **Is this the right approach?** Would a simpler alternative be more consistent with the codebase?
4. **What problems do you see?** Identify bugs, edge cases, missing validation, and anything else that concerns you.

Write down your independent assessment before proceeding.

### Step 2: Incorporate PR Narrative and Reconcile

Now read the PR description, labels, linked issues, and existing review comments. Treat all of this as **claims to verify**.

1. **PR metadata**: Fetch the PR description, linked issues, and author.
2. **Existing review comments**: Check if there are already review comments to avoid duplicating feedback.
3. **Reconcile**: Where your independent reading disagrees with the PR description, investigate further — do not simply defer to the author's framing.

### Step 3: Detailed Analysis

1. **Focus on what matters.** Prioritize bugs, incorrect state transitions, race conditions, missing validation, and convention violations. Do not comment on trivial style unless it violates an explicit rule below.
2. **Be specific and actionable.** Every comment should tell the author exactly what to change and why.
3. **Flag severity clearly:**
   - ❌ **error** — Must fix before merge. Bugs, security issues, convention violations.
   - ⚠️ **warning** — Should fix. Missing validation, inconsistency with patterns.
   - 💡 **suggestion** — Consider changing. Minor improvements.
   - ✅ **verified** — Confirmed correct. Use to show important aspects were checked.
4. **Don't pile on.** If the same issue appears many times, flag it once with a note listing all affected files. Do NOT leave separate comments for each occurrence.
5. **Don't flag what CI catches.** Assume the build (`dotnet build -c Release` with `TreatWarningsAsErrors`) and tests will run separately.
6. **Avoid false positives.** Verify concerns against the full context, not just the diff. If unsure, say so explicitly rather than asserting.
7. **Verdict must match findings.** If you have ⚠️ findings, don't say LGTM. If uncertain, use "Needs Human Review."

---

## F# Idioms & Conventions

### Task Computation Expression

- **Always use `task { }` CE for async operations**, not `async { }`. This project uses `task { }` exclusively.
- **Use `let!` for awaiting tasks** inside `task { }` blocks. Never use `.Result` or `.Wait()` which causes deadlocks in ASP.NET Core.
- **Return `Task<unit>` from handlers**, not `Task<'T>` unless a value is needed by the caller.

```fsharp
// ✅ Correct
let handleSomething (chatId: int64) =
    task {
        let! result = db.GetSomething()
        do! sendText chatId "Done"
    }

// ❌ Wrong — async CE
let handleSomething (chatId: int64) =
    async {
        let! result = db.GetSomething() |> Async.AwaitTask
        do! sendText chatId "Done" |> Async.AwaitTask
    }
```

### Option Types & Pattern Matching

- **Use `option` types for nullable domain values**, not null.
- **Pattern match with `match ... with`** for multi-case handling. Prefer exhaustive matches.
- **Use `TryParse` pattern** returning tuples for parsing: `match Decimal.TryParse(...) with | true, v -> Some v | _ -> None`.
- **Avoid nested `if/else`** — prefer pattern matching or early returns via `match`.

### Records & Discriminated Unions

- **All database-mapped records must use `[<CLIMutable>]`** for Dapper compatibility.
- **Use `[<RequireQualifiedAccess>]`** on discriminated unions to prevent name collisions.
- **Nullable database columns** should use `string | null` annotation (not `string option`) for Dapper compatibility.

```fsharp
// ✅ Correct for DB types
[<CLIMutable>]
type DbUser =
    { id: int64
      username: string | null
      first_name: string | null }

// ✅ Correct for result types
[<RequireQualifiedAccess>]
type TakeCouponResult = Taken of Coupon | NotFoundOrNotAvailable | LimitReached
```

### F# Compilation Order

- **New `.fs` files must be added to the `.fsproj` in the correct position.** F# compiles files in order; a file can only reference types defined in files listed above it.
- **Verify the file is in the `<Compile Include="..." />` list** in the correct position relative to its dependencies.

---

## Telegram Bot Patterns

### Command Handlers

- **Commands are pure functions returning `Task<unit>`** that compose bot API calls.
- **Always verify community membership** before processing user actions (coupons, etc.).
- **Use `sendText` helper** for simple messages, `sendTextWithKeyboard` for interactive responses.

### Callback Query Handlers

- **Callback data uses colon-separated format**: `"action:param1:param2"` (e.g., `"take:42"`, `"addflow:disc:5:25"`).
- **Always answer callback queries** to dismiss the loading indicator on Telegram clients.
- **Pattern match callback data** to route to the correct handler.
- **Validate callback data parameters** — never assume the data is well-formed (users can craft arbitrary callback data).

### Wizard Flows (Multi-Step State Machines)

- **Use `PendingAddFlow` table** to persist wizard state across messages.
- **Each stage should validate the previous stage's data** before proceeding.
- **Stage transitions**: `awaiting_photo` → `awaiting_discount_choice` → `awaiting_date_choice` → `awaiting_ocr_confirm` → confirm.
- **Clean up pending flows** on completion or cancellation.

### Inline Keyboards

- **Compose keyboards with `InlineKeyboardButton.WithCallbackData(label, data)`**.
- **Labels are in Russian** — ensure proper Cyrillic text.
- **Keep keyboard rows focused** — don't overcrowd with too many buttons per row.

---

## Database Conventions

### Migrations

- **Naming**: `V{N}__{description}.sql` (sequential number, double underscore, snake_case description).
- **Always include GRANT** for the `coupon_hub_bot_service` role when creating new tables, sequences, or schema objects.
- **Grant pattern**: `GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.{table} TO coupon_hub_bot_service;`
- **Sequence grants**: `GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO coupon_hub_bot_service;`

### Dapper Usage

- **Use parameterized queries** — never string-interpolate user input into SQL.
- **Records for query results must have `[<CLIMutable>]`** attribute.
- **Use `QuerySingle`, `QuerySingleOrDefault`, `Execute`** as appropriate for the expected result cardinality.

---

## Russian UI Text & Cyrillic Handling

- **All user-facing text is in Russian.** Verify that new messages, button labels, and error messages are in Russian.
- **Never compare raw JSON strings containing Cyrillic text.** Always parse with `System.Text.Json.JsonDocument` or `JsonSerializer` first, then compare parsed values.
- **In tests, use `findCallWithText` helpers** from `FakeCallHelpers.fs` which handle JSON parsing correctly.

```fsharp
// ✅ Correct — parsed comparison
Assert.True(findCallWithText calls 200L "Добавил купон", "Expected confirmation message")

// ❌ Wrong — raw string comparison
Assert.Contains("\"text\":\"Добавил купон\"", responseBody)
```

---

## Testing Patterns

### General

- **Tests use xUnit v3 with assembly fixtures** (`AssemblyFixture` attribute in `Program.fs`).
- **Test parallelization is disabled** (`DisableTestParallelization = true`) due to shared container state.
- **Tests are Docker-based E2E tests** using Testcontainers — they spin up PostgreSQL, Flyway, FakeTgApi, and the bot.

### FakeTgApi

- **FakeTgApi stores `ApiCallLog` records** (method, body, timestamp) for assertion.
- **Use fixture methods**: `SetChatMemberStatus`, `SetTelegramFile`, `GetFakeCalls`, `SendUpdate`.
- **Use `findCallWithText`** to assert bot sent the expected message to the expected chat.
- **Use `findCallWithAnyText`** when checking for any message to a chat without specific text matching.

### Test Structure

- **Arrange**: Set up Telegram state (member status, files) via fixture methods.
- **Act**: Send a Telegram update via `fixture.SendUpdate(...)`.
- **Assert**: Query `fixture.GetFakeCalls()` and use `FakeCallHelpers` to verify responses.
- **Direct DB queries**: Use Dapper `QuerySingle<'t>` for verifying database state directly.

### Deterministic Time

- **Tests freeze time** with `BOT_FIXED_UTC_NOW` environment variable for deterministic coupon expiry behavior.
- **When testing time-dependent logic**, ensure the frozen time is appropriate for the test scenario.

---

## Security

- **Never commit secrets, tokens, or API keys** in code. Use environment variables.
- **Validate all Telegram callback data** — callback data can be crafted by malicious clients.
- **Use parameterized SQL** — never interpolate user input into queries.
- **Verify community membership** before allowing access to coupon operations.

---

## Performance & Async Patterns

- **Never block on async code** — no `.Result`, `.Wait()`, or `Async.RunSynchronously` in the request pipeline.
- **Use `task { }` CE**, which avoids the overhead of `async { }` state machines.
- **Prefer `ValueTask` where appropriate** for hot paths that often complete synchronously.
- **Pre-allocate collections** when size is known (pass capacity to constructors).
- **Avoid closures capturing large state** in hot paths — prefer explicit state passing.

---

## Code Style

- **Unix line endings** (`end_of_line = lf`) and final newline (`insert_final_newline = true`) as per `.editorconfig`.
- **`TreatWarningsAsErrors` is enabled** — all warnings are errors. Do not suppress warnings without justification.
- **Follow existing patterns in modified files.** The file's current style takes precedence over general guidelines.
- **Keep functions small and focused.** Extract helpers for complex logic.
- **Use descriptive names** — avoid abbreviations except well-known ones (`db`, `msg`, `cfg`).

---

## Review Output Format

### Structure

```
## 🤖 Copilot Code Review — PR #<number>

### Holistic Assessment

**Motivation**: <1-2 sentences on whether the PR is justified and the problem is real>

**Approach**: <1-2 sentences on whether the fix/change takes the right approach>

**Summary**: <✅ LGTM / ⚠️ Needs Human Review / ⚠️ Needs Changes / ❌ Reject>. <2-3 sentence summary.>

---

### Detailed Findings

#### ✅/⚠️/❌/💡 <Category Name> — <Brief description>

<Explanation with specifics. Reference code, line numbers, files.>
```

### Guidelines

- **Holistic Assessment** comes first: Motivation, Approach, Summary.
- **Detailed Findings** uses emoji-prefixed category headers:
  - ✅ for things verified correct
  - ⚠️ for warnings (should fix or follow-up)
  - ❌ for errors (must fix before merge)
  - 💡 for minor suggestions
- **Summary verdict must be consistent with findings:**
  - Only use "LGTM" when all findings are ✅ or 💡 and you are confident
  - Use "Needs Human Review" when uncertain
  - Use "Needs Changes" when there are blocking issues
- Keep the review concise but thorough. Every claim should be backed by evidence from the code.

