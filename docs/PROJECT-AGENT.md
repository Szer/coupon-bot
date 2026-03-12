# Project Agent

## Overview

The daily project assessment workflow acts as an **automated project manager**. It runs every day at 04:37 UTC, gathers infrastructure metrics, and creates an orchestration issue. The issue is assigned to a **custom Copilot agent** (`project`) that analyzes the system and maintains a backlog — without making any code changes.

## How It Works

```
┌─────────────────────────────────────────────────────────────────┐
│  project.yml (daily at 04:37 UTC)                               │
│                                                                 │
│  Job 1: cleanup                                                 │
│    - Close stale orchestration issues from previous runs        │
│    - Close & delete Copilot PRs from previous runs (safety net) │
│                                                                 │
│  Job 2: project (needs: cleanup)                                │
│    1. Connect VPN                                               │
│    2. Run gather-metrics.sh → Prometheus + Loki + ArgoCD        │
│    3. Disconnect VPN                                            │
│    4. Ensure labels exist                                       │
│    5. Create orchestration issue with metrics in body           │
│    6. Assign Copilot (project custom agent) via REST API        │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  Copilot (project custom agent)                                 │
│  Tools: read, search, execute (no edit tool)                    │
│                                                                 │
│  1. Read metrics snapshot from issue body                       │
│  2. Deeply analyze the codebase (open-ended, judgment-driven)   │
│  3. Query live Loki/Prometheus if VPN available                 │
│  4. Review existing open issues                                 │
│  5. Create / bump / close project backlog issues                │
│  6. Close orchestration issue with summary                      │
└─────────────────────────────────────────────────────────────────┘
```

## Custom Agent Architecture

The project agent is defined in `.github/agents/project.agent.md`. Key properties:

| Property | Value | Why |
|----------|-------|-----|
| `tools` | `["read", "search", "execute"]` | No `edit` tool; file modifications are prevented by **command allowlist** (prompt-level) and **Copilot PR Manager** (platform-level). `execute` is restricted to read-only and issue management commands (see below). |
| `name` | `project` | Used in REST API `agent_assignment.custom_agent` field |
| Prompt | Analytical, open-ended | Agent reads code and reasons about it rather than following a grep checklist |

### Command Allowlist (Guardrails)

Non-coding agents (project, product) have access to the `execute` tool but are restricted to a **command allowlist** defined in their agent prompt. Only these commands are permitted:

- **Issue management:** `gh issue create/edit/close/list/view/comment`, `gh api` (issues endpoints only)
- **External queries:** `curl` (Loki, Prometheus, ArgoCD)
- **Read-only inspection:** `cat`, `grep`, `head`, `tail`, `wc`, `jq`, `sort`, `uniq`, `find`, `ls`
- **Read-only git:** `git status`, `git branch` (list only), `git log --oneline`, `git show`
- **Utilities:** `date`, `echo` (piping only, not file writing)

Everything else is **FORBIDDEN** — including `git add/commit/push`, `sed`, `gh pr create`, `dotnet build/test`, and any file-modifying command.

Additionally, **Copilot PR Manager** (`copilot-pr-manager.yml`) provides a hard platform-level boundary when the `COPILOT_PR_MANAGER_TOKEN` secret is configured: a cron workflow runs every 5 minutes and auto-closes PRs created by non-coding agents by detecting `Custom agent used: project` or `Custom agent used: product` in the PR body. It also auto-approves pending workflow runs for legitimate Copilot coding agent PRs. If the secret is not configured, the workflow exits early with a warning and no protection is active.

### All Custom Agents

| Agent | Role | Triggered by |
|-------|------|-------------|
| `project` | Project manager — analyze codebase, manage technical backlog | Daily workflow (`project.yml`) |
| `product` | Product manager — triage feedback, analyze usage, create refined tickets | Daily workflow (`product.yml`) + feedback trigger (`feedback-triage.yml`) |
| `sre` | SRE — debug production incidents, rollback, escalate code fixes | Deploy failure (`deploy.yml` notify-failure job) |
| Default coding agent | Write code, fix bugs, create PRs | Auto-fix workflow, manual assignment, SRE escalation |

### Assignment via REST API

These workflows assign Copilot using the REST API with `agent_assignment`:

```bash
# Project workflow — uses custom agent (no edit tool, analysis only)
gh api --method POST /repos/OWNER/REPO/issues/NUMBER/assignees \
  --input - <<< '{
  "assignees": ["copilot-swe-agent[bot]"],
  "agent_assignment": {
    "custom_agent": "project"
  }
}'

# Auto-fix workflow — uses default coding agent (can write code)
gh api --method POST /repos/OWNER/REPO/issues/NUMBER/assignees \
  --input - <<< '{
  "assignees": ["copilot-swe-agent[bot]"],
  "agent_assignment": {}
}'
```

## Components

| Component | Path | Purpose |
|-----------|------|---------|
| Project workflow | `.github/workflows/project.yml` | Daily trigger, VPN, metrics, issue creation |
| Auto-fix workflow | `.github/workflows/auto-fix.yml` | Hourly pickup, mutex check, Copilot assignment |
| Metrics script | `scripts/gather-metrics.sh` | Queries Prometheus, Loki, ArgoCD; outputs markdown |
| Custom agent | `.github/agents/project.agent.md` | Agent profile with tools and analysis instructions |

## Metrics Gathered

### Prometheus
- Process up status and pod readiness
- Memory usage (MB) and CPU usage (%)
- CPU throttling rate
- Container restart count
- HTTP request rate and 5xx error rate
- Pod waiting reasons (CrashLoopBackOff, OOMKilled, etc.)

### Loki
- Error/Fatal log count (last 24h)
- Warning log count (last 24h)
- Top error patterns (counts only, messages redacted)
- Total log volume

### ArgoCD
- Sync status and health status
- Currently deployed image tag
- Conditions/warnings

## Labels

| Label | Color | Purpose |
|-------|-------|---------|
| `project` | Purple (#7057ff) | Applied to all backlog issues created by project agent |
| `infra` | Light purple (#d4c5f9) | Infrastructure issue — cannot be fixed in this repo, skipped by auto-fix |
| `priority-high` | Red (#b60205) | Reserved for user-reported feedback (not used by project agent) |
| `priority-medium` | Yellow (#fbca04) | Bugs, security, performance, significant tech debt |
| `priority-low` | Green (#0e8a16) | Nice-to-have improvements |

## Backlog Management Rules

- **Create**: New issue for a newly discovered problem (labeled `project` + `priority-medium` or `priority-low` + optional `infra`)
- **Bump**: Comment on existing issue if the same problem persists; reassess priority (up to `priority-medium` max)
- **Close**: Close issue if the underlying problem is resolved
- **Never assign**: Backlog issues are left unassigned — the auto-fix workflow picks them up

## Issue Lifecycle

```
[Discovered] → project issue created (unassigned)
     ↓
[Still present next day] → bump comment added
     ↓
[Bumped multiple times] → high priority (future: auto-implemented)
     ↓
[Fixed] → closed by project assessment with resolution comment
```

## Cleanup Mechanism

1. **Stale orchestration issues**: If Copilot fails to close the orchestration issue (e.g., network timeout), the next day's cleanup job closes it automatically.
2. **Stale Copilot PRs (safety net)**: The custom agent has no `edit` tool and shouldn't create PRs. The PR cleanup step is kept as a safety net in case platform behavior changes. In normal operation, this step is a no-op.
3. **False-positive issues**: If a backlog issue was created due to a temporary condition (e.g., Loki briefly unreachable), the next project assessment should close it when the condition resolves.

## Schedule

- **Cron**: `37 4 * * *` (04:37 UTC daily)
- **Rationale**: Off-peak US hours (11:37 PM ET), non-round time avoids clashing with other scheduled jobs
- **Manual trigger**: Available via `workflow_dispatch` in the Actions tab

## Secrets Required

| Secret | Purpose |
|--------|---------|
| `WIREGUARD_CONFIG` | VPN access to internal services |
| `ARGOCD_AUTH_TOKEN` | ArgoCD API authentication |
| `COPILOT_ASSIGN_PAT` | Creating issues and assigning Copilot |

All secrets are configured in the `copilot` environment.

## Auto-Fix Workflow

The auto-fix workflow (`auto-fix.yml`) is the companion to the project agent. It picks up backlog issues and assigns Copilot to implement fixes automatically.

### How It Works

```
┌─────────────────────────────────────────────────────────────────┐
│  auto-fix.yml (hourly at :17)                                   │
│                                                                 │
│  1. Mutex check: any draft PRs by Copilot?                      │
│     (excl. non-coding agent PRs: project/product/self-assess)   │
│     → If yes: skip (Copilot is busy)                            │
│     → If no: proceed                                            │
│                                                                 │
│  2. Pick highest priority issue:                                │
│     - Has label: project                                        │
│     - NOT orchestration issue (project/product/self-assess)     │
│     - NOT labeled: infra                                        │
│     - Sort: priority-high > medium > low > bumps > oldest       │
│                                                                 │
│  3. Assign Copilot (default coding agent) via REST API          │
│     → Copilot creates branch + PR with the fix                  │
│     → PR goes through normal review                             │
└─────────────────────────────────────────────────────────────────┘
```

### Mutex: One Session at a Time

The workflow ensures only one Copilot coding session runs at a time by checking for open **draft PRs** by `app/copilot-swe-agent`. Draft = Copilot is actively working. Ready-for-review = done (doesn't block).

### Priority Sorting

Auto-fix only considers issues labeled `project`. Issues are picked in this order (equivalent to `ORDER BY priority DESC, comments DESC, created_at ASC`):
1. `priority-high` labels first (if a user manually adds both `project` and `priority-high`)
2. `priority-medium` next (most project issues)
3. `priority-low` / unlabeled last
4. Within same priority: most comments first (total GitHub comment count is used as a proxy for bump frequency)
5. Within same comment count: oldest issue first

### Skipped Issues

- **`infra` label**: Infrastructure issues that belong to a different repo (e.g., `my-infra`). These remain in the backlog for human attention.
- **Orchestration issues**: Title matching "project assessment YYYY-MM-DD" — these are workflow artifacts, not fixable issues.

### Schedule

- **Cron**: `17 * * * *` (every hour at :17)
- **Manual trigger**: Available via `workflow_dispatch`

## Full Feedback Loop

```
project (daily 04:37)            auto-fix (hourly :17)
      │                                │
      ▼                                ▼
 Scan codebase ──── creates ────► Backlog issues
 + infra metrics                       │
                                       ▼
                              Pick highest priority
                                       │
                                       ▼
                              Assign Copilot ──► PR with fix
                                                    │
                                                    ▼
                                              Human review
                                                    │
                                                    ▼
                                              Merge ──► Next project run
                                                        detects fix, closes issue
```

## Copilot PR Manager

The Copilot PR Manager (`copilot-pr-manager.yml`) is a scheduled workflow (cron every 5 minutes) that replaces the old event-driven guard workflow. It performs two functions:

1. **Close non-coding agent PRs**: Lists all open PRs authored by the Copilot GitHub App and checks the PR body for `Custom agent used: project` or `Custom agent used: product`. If found, the PR is immediately closed with a comment and the branch deleted.
2. **Approve pending workflow runs**: Lists `action_required` workflow runs and approves those belonging to open Copilot PR branches. This solves the problem where Copilot-created PRs always require manual workflow approval (GitHub treats them as "first-time contributors").

The cron approach is necessary because event-driven workflows (`pull_request: opened`) themselves require manual approval for Copilot PRs — creating a chicken-and-egg problem. A scheduled workflow runs as the repo owner and requires no approval. Uses a PAT (`COPILOT_PR_MANAGER_TOKEN` secret) since `GITHUB_TOKEN` cannot approve other workflow runs.
