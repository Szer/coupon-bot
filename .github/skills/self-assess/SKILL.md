---
name: self-assess
description: Daily automated self-assessment of codebase and infrastructure. Analyzes metrics snapshot, codebase quality, and existing issues. Creates, bumps, or closes backlog issues labeled 'self-assess'. Use when an orchestration issue titled 'Daily self-assessment' is assigned.
---

# Daily Self-Assessment

You are acting as an **automated product manager**. Your job is to analyze the system holistically and maintain a clean, prioritized backlog of improvements. You must be thorough but avoid noise — only create issues for things that genuinely matter.

## Prerequisites

- VPN is pre-established via `copilot-setup-steps.yml` (WireGuard to `*.internal` hosts)
- `$ARGOCD_AUTH_TOKEN` is available from the `copilot` environment
- The orchestration issue body contains a metrics snapshot from `gather-metrics.sh`

## Phase 1: Read the Metrics Snapshot

The issue body contains structured metrics from Prometheus, Loki, and ArgoCD. Parse it to understand:

- Is the pod healthy and ready?
- Is memory or CPU usage concerning?
- Are there container restarts?
- Are there 5xx errors?
- Are there Error/Fatal log entries? What are the top error messages?
- Is ArgoCD synced and healthy?

Take notes on anything abnormal. These will become issue candidates.

## Phase 2: Analyze the Codebase

Search the codebase for quality issues. Focus on **actionable findings** — skip trivial style matters.

### 2a. Tech Debt Markers

Search for TODO, FIXME, HACK, WORKAROUND comments:

```bash
grep -rn 'TODO\|FIXME\|HACK\|WORKAROUND' src/ --include='*.fs' --include='*.fsproj'
```

Each of these is a potential issue. Check if it already has a corresponding `self-assess` issue.

### 2b. Test Coverage Gaps

Compare source files against test files:

```bash
# List all F# source files in the main project
find src/CouponHubBot/Services/ -name '*.fs' | sort

# List all test files
find tests/ -name '*.fs' | sort
```

Identify source modules that have no corresponding test coverage. Missing tests for critical business logic (coupon operations, wizard flows, reminders) are high-priority findings.

### 2c. Large/Complex Files

Find files that may need refactoring:

```bash
# F# files over 300 lines
find src/ -name '*.fs' -exec awk 'END{if(NR>300)print FILENAME": "NR" lines"}' {} \;
```

Large files suggest the module might benefit from being split. Only flag if the file genuinely has multiple concerns.

### 2d. Documentation Freshness

Check if docs reference features, patterns, or file paths that no longer exist:

- Read each file in `docs/` and verify key claims against the actual codebase
- Check if new features added recently are documented
- Focus on `ARCHITECTURE.md`, `TELEGRAM-BOT-LOGIC.md`, `DATABASE.md`

### 2e. Security Scan

Look for potential security concerns:

```bash
# Hardcoded values that might be secrets
grep -rn 'password\|secret\|token\|api.key' src/ --include='*.fs' -i

# String interpolation in SQL (should use parameterized queries)
grep -rn 'sprintf.*SELECT\|sprintf.*INSERT\|sprintf.*UPDATE\|sprintf.*DELETE\|$".*SELECT\|$".*INSERT' src/ --include='*.fs'
```

### 2f. Error Handling

Look for patterns that might cause issues:

```bash
# .Result or .Wait() calls (cause deadlocks in ASP.NET Core)
grep -rn '\.Result\b\|\.Wait()' src/ --include='*.fs'

# Missing error handling
grep -rn 'ignore\b' src/ --include='*.fs'
```

## Phase 3: Analyze Infrastructure Metrics

Based on the metrics snapshot from Phase 1, identify operational concerns:

- **High memory**: If memory usage is above 256 MB, investigate potential memory leaks
- **Restarts**: If restart count increased since last assessment, investigate the cause via Loki
- **5xx errors**: Any non-zero 5xx rate needs investigation
- **Error logs**: Recurring error patterns may indicate bugs. Query Loki for details:

```bash
START=$(date -u -d '24 hours ago' +%Y-%m-%dT%H:%M:%SZ)
curl -s -G http://loki.internal/loki/api/v1/query_range \
  --data-urlencode 'query={container="coupon-bot"} | json | level=~"Error|Fatal"' \
  --data-urlencode "start=$START" \
  --data-urlencode 'limit=50' \
  | jq '.data.result[].values[] | .[1]'
```

- **CPU throttling**: Non-zero throttling may indicate resource limits need adjusting

If metrics are nominal, note this — it's still valuable information.

## Phase 4: Review Existing Issues

List all open issues, paying special attention to `self-assess` labeled ones:

```bash
gh issue list --repo Szer/coupon-bot --state open --json number,title,labels,body,comments --limit 100
```

Also list recently closed issues to understand what was fixed:

```bash
gh issue list --repo Szer/coupon-bot --state closed --json number,title,labels,closedAt --limit 20
```

Build a mental map of what's already tracked.

## Phase 5: Manage the Backlog

For each finding from Phases 2-3, decide: **create**, **bump**, or **skip**.

### Rules

1. **Search before creating**: Always search existing open issues (especially `self-assess` labeled) for a matching issue before creating a new one
2. **Bump if exists**: If a similar issue is already open, add a comment like:
   ```
   🔄 **Self-assessment bump (YYYY-MM-DD)**

   This issue is still relevant. [Updated context: specific details about current state]
   ```
3. **Create if new**: Use this template for new issues:
   ```
   gh issue create \
     --repo Szer/coupon-bot \
     --title "Brief descriptive title" \
     --label "self-assess" \
     --body "## Problem

   [Clear description of the issue]

   ## Evidence

   [Specific evidence: log entries, code locations, metric values]

   ## Suggested Approach

   [How this could be fixed]

   ## Priority Context

   [Why this matters: reliability, security, performance, maintainability]"
   ```
4. **Close if resolved**: For each open `self-assess` issue, check if the underlying problem is still present. If it's fixed, close it:
   ```
   gh issue close NUMBER --repo Szer/coupon-bot \
     --comment "✅ **Resolved** (YYYY-MM-DD self-assessment)

   [Explanation of how/when this was fixed]"
   ```
5. **Never assign**: Do not assign anyone (including Copilot) to backlog issues
6. **Quality over quantity**: Only create issues for things that genuinely matter:
   - Bugs or potential bugs
   - Security vulnerabilities
   - Performance problems
   - Missing critical test coverage
   - Significant tech debt
   - Documentation that's actively misleading
   - Infrastructure concerns
7. **Do NOT create issues for**:
   - Style preferences or minor formatting
   - Speculative improvements with no clear benefit
   - Things that are working correctly and don't need changes
   - Duplicate issues (always check first!)

## Phase 6: Close the Orchestration Issue

After completing all phases, close the orchestration issue (the one you were assigned to) with a summary:

```
gh issue close ISSUE_NUMBER --repo Szer/coupon-bot \
  --comment "## Self-Assessment Summary (YYYY-MM-DD)

### Metrics Overview
- Pod healthy: yes/no
- Memory: X MB | CPU: X% | Restarts: N
- 5xx rate: X | Error logs (24h): N

### Actions Taken
- **New issues created**: N
  - #X: title
  - #Y: title
- **Existing issues bumped**: N
  - #X: title (bump reason)
- **Issues closed as resolved**: N
  - #X: title (resolution)

### Key Observations
- [Notable findings, even if no issue was created]

### Backlog Summary
- Total open self-assess issues: N
- Most-bumped issue: #X (N bumps) — [title]"
```

## Notes

- The bot's user-facing text is in Russian (Cyrillic) — this is expected, not a bug
- `TreatWarningsAsErrors` is enabled — compiler warnings are already treated as errors
- F# compilation order matters — this is by design, not a problem to flag
- The `copilot` environment has all necessary secrets (ARGOCD_AUTH_TOKEN, etc.)
- If VPN services are unreachable, note it in the summary but don't fail — the metrics snapshot in the issue body is the primary data source
