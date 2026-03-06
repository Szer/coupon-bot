---
name: pr-review
description: Address unresolved PR review comments. Fetches unresolved threads, assesses each (fix code or skip with explanation), commits fixes, replies to every comment, and resolves all threads. Invoke with a PR number or URL.
---

# PR Review Comment Handler

When invoked, follow this exact workflow to address all unresolved review comments on a pull request.

## Input

The user provides a PR number or URL (e.g., `#156` or `https://github.com/Szer/coupon-bot/pull/156`). Extract the PR number.

## Workflow

### Step 1: Fetch unresolved review threads

Use the `github-mcp-server-pull_request_read` tool with method `get_review_comments` to get all review threads. Filter to only unresolved threads (`is_resolved: false`).

### Step 2: Assess each comment

For each unresolved comment, decide:

- **Fix**: The comment points out a real bug, inconsistency, missing grant, defensive coding improvement, or convention violation. Make the code/doc change.
- **Skip (with explanation)**: The comment suggests something that is intentional by design, deferred to a future PR, or not applicable. Provide a clear reason.

**Assessment rules:**
- Owner (human) comments always take priority — fix unless clearly already addressed.
- Copilot reviewer comments: fix if they identify real issues (bugs, missing error handling, inconsistencies, security). Skip if they repeat already-addressed concerns, suggest stylistic changes, or request tests that are planned for a later PR.
- Comments proposing changes in Russian for English docs — skip (docs stay in English per project convention).
- Comments requesting E2E tests — typically deferred to a later PR (check the plan).
- Comments about env var handling — check whether the owner has already confirmed the behavior.

### Step 3: Make fixes

If any comments require code/doc changes:

1. Make all the edits needed
2. Run `dotnet build -c Release` to verify compilation (0 errors, only the pre-existing FakeTgApi FS3511 warning is acceptable)
3. Commit with a message listing what was fixed
4. Push to the PR branch

### Step 4: Reply to every comment

For EVERY unresolved comment (both fixed and skipped), post a reply:

- **Fixed**: `"Fixed in SHORTHASH — <brief description of what changed>."`
- **Skipped**: A clear explanation of why (e.g., "Intentional per owner's requirement", "Will be addressed in PR N", "Already handled by X").

Use the GitHub API to reply:
```
gh api repos/OWNER/REPO/pulls/PR/comments -F body="REPLY_TEXT" -F in_reply_to=COMMENT_DATABASE_ID --silent
```

The `COMMENT_DATABASE_ID` is extracted from the comment's `html_url` — it's the number after `discussion_r` in the URL fragment.

### Step 5: Resolve all threads

Fetch unresolved thread IDs via GraphQL and resolve them:

```graphql
# Fetch
query {
  repository(owner: "OWNER", name: "REPO") {
    pullRequest(number: PR_NUMBER) {
      reviewThreads(first: 50) {
        nodes { id, isResolved }
      }
    }
  }
}

# Resolve each
mutation {
  resolveReviewThread(input: {threadId: "THREAD_ID"}) {
    thread { isResolved }
  }
}
```

### Step 6: Report results

Summarize what was done:
- How many comments were fixed vs skipped
- What code changes were made (if any)
- Any comments that need human attention

## Repository Context

- **Language:** F# / .NET 10, `TreatWarningsAsErrors` is enabled
- **Build command:** `dotnet build -c Release`
- **Repo:** `Szer/coupon-bot` (owner: `Szer`)
- **Branch convention:** PR branches are already checked out locally
- **Docs language:** English (ignore review suggestions to write docs in Russian)
- **UI text language:** Russian (Cyrillic) — this is correct, don't change it
- **Pre-existing warning:** FakeTgApi FS3511 is expected, not from our changes

## Notes

- Always check out the correct branch before making changes (`git branch --show-current`)
- The `gh api` calls use `-F` (not `-f`) for `in_reply_to` so it's sent as a number
- Reply to ALL unresolved comments, even if skipping — never leave a thread without a response
- Resolve ALL threads after replying — never leave threads hanging
