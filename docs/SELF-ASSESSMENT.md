# Self-Assessment

## Overview

The daily self-assessment workflow acts as an **automated product manager**. It runs every day at 04:37 UTC, gathers infrastructure metrics, and creates an orchestration issue. The issue is assigned to a **custom Copilot agent** (`self-assess`) that analyzes the system and maintains a backlog — without making any code changes.

## How It Works

```
┌─────────────────────────────────────────────────────────────────┐
│  self-assess.yml (daily at 04:37 UTC)                           │
│                                                                 │
│  Job 1: cleanup                                                 │
│    - Close stale orchestration issues from previous runs        │
│    - Close & delete Copilot PRs from previous runs (safety net) │
│                                                                 │
│  Job 2: self-assess (needs: cleanup)                            │
│    1. Connect VPN                                               │
│    2. Run gather-metrics.sh → Prometheus + Loki + ArgoCD        │
│    3. Disconnect VPN                                            │
│    4. Ensure labels exist                                       │
│    5. Create orchestration issue with metrics in body           │
│    6. Assign Copilot (self-assess custom agent) via REST API    │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  Copilot (self-assess custom agent)                             │
│  Tools: read, search, execute (no edit tool)                    │
│                                                                 │
│  1. Read metrics snapshot from issue body                       │
│  2. Deeply analyze the codebase (open-ended, judgment-driven)   │
│  3. Query live Loki/Prometheus if VPN available                 │
│  4. Review existing open issues                                 │
│  5. Create / bump / close self-assess backlog issues            │
│  6. Close orchestration issue with summary                      │
└─────────────────────────────────────────────────────────────────┘
```

## Custom Agent Architecture

The self-assess agent is defined in `.github/agents/self-assess.agent.md`. Key properties:

| Property | Value | Why |
|----------|-------|-----|
| `tools` | `["read", "search", "execute"]` | No `edit` tool — agent is instructed not to modify files. `execute` is needed for `gh` CLI and `curl` commands. |
| `name` | `self-assess` | Used in REST API `agent_assignment.custom_agent` field |
| Prompt | Analytical, open-ended | Agent reads code and reasons about it rather than following a grep checklist |

### All Custom Agents

| Agent | Role | Triggered by |
|-------|------|-------------|
| `self-assess` | Product manager — analyze codebase, manage backlog | Daily workflow (`self-assess.yml`) |
| `sre` | SRE — debug production incidents, rollback, escalate code fixes | Deploy failure (`deploy.yml` notify-failure job) |
| Default coding agent | Write code, fix bugs, create PRs | Auto-fix workflow, manual assignment, SRE escalation |

### Assignment via REST API

Both workflows assign Copilot using the REST API with `agent_assignment`:

```bash
# Self-assess workflow — uses custom agent (no edit tool, analysis only)
gh api --method POST /repos/OWNER/REPO/issues/NUMBER/assignees \
  --input - <<< '{
  "assignees": ["copilot-swe-agent[bot]"],
  "agent_assignment": {
    "custom_agent": "self-assess"
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
| Self-assess workflow | `.github/workflows/self-assess.yml` | Daily trigger, VPN, metrics, issue creation |
| Auto-fix workflow | `.github/workflows/auto-fix.yml` | Hourly pickup, mutex check, Copilot assignment |
| Metrics script | `scripts/gather-metrics.sh` | Queries Prometheus, Loki, ArgoCD; outputs markdown |
| Custom agent | `.github/agents/self-assess.agent.md` | Agent profile with tools and analysis instructions |

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
| `self-assess` | Purple (#7057ff) | Applied to all backlog issues created by self-assessment |
| `infra` | Light purple (#d4c5f9) | Infrastructure issue — cannot be fixed in this repo, skipped by auto-fix |
| `priority-high` | Red (#b60205) | Reserved for user-reported feedback (not used by self-assess) |
| `priority-medium` | Yellow (#fbca04) | Bugs, security, performance, significant tech debt |
| `priority-low` | Green (#0e8a16) | Nice-to-have improvements |

## Backlog Management Rules

- **Create**: New issue for a newly discovered problem (labeled `self-assess` + `priority-medium` or `priority-low` + optional `infra`)
- **Bump**: Comment on existing issue if the same problem persists; reassess priority (up to `priority-medium` max)
- **Close**: Close issue if the underlying problem is resolved
- **Never assign**: Backlog issues are left unassigned — the auto-fix workflow picks them up

## Issue Lifecycle

```
[Discovered] → self-assess issue created (unassigned)
     ↓
[Still present next day] → bump comment added
     ↓
[Bumped multiple times] → high priority (future: auto-implemented)
     ↓
[Fixed] → closed by self-assessment with resolution comment
```

## Cleanup Mechanism

1. **Stale orchestration issues**: If Copilot fails to close the orchestration issue (e.g., network timeout), the next day's cleanup job closes it automatically.
2. **Stale Copilot PRs (safety net)**: The custom agent has no `edit` tool and shouldn't create PRs. The PR cleanup step is kept as a safety net in case platform behavior changes. In normal operation, this step is a no-op.
3. **False-positive issues**: If a backlog issue was created due to a temporary condition (e.g., Loki briefly unreachable), the next self-assessment should close it when the condition resolves.

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

The auto-fix workflow (`auto-fix.yml`) is the companion to self-assessment. It picks up backlog issues and assigns Copilot to implement fixes automatically.

### How It Works

```
┌─────────────────────────────────────────────────────────────────┐
│  auto-fix.yml (hourly at :17)                                   │
│                                                                 │
│  1. Mutex check: any draft PRs by Copilot? (excl. self-assess)  │
│     → If yes: skip (Copilot is busy)                            │
│     → If no: proceed                                            │
│                                                                 │
│  2. Pick highest priority issue:                                │
│     - Has label: self-assess                                    │
│     - NOT orchestration issue (title contains "self-assessment") │
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

Auto-fix only considers issues labeled `self-assess`. Issues are picked in this order (equivalent to `ORDER BY priority DESC, comments DESC, created_at ASC`):
1. `priority-high` labels first (if a user manually adds both `self-assess` and `priority-high`)
2. `priority-medium` next (most self-assess issues)
3. `priority-low` / unlabeled last
4. Within same priority: most comments first (total GitHub comment count is used as a proxy for bump frequency)
5. Within same comment count: oldest issue first

### Skipped Issues

- **`infra` label**: Infrastructure issues that belong to a different repo (e.g., `my-infra`). These remain in the backlog for human attention.
- **Orchestration issues**: Title matching "self-assessment YYYY-MM-DD" — these are workflow artifacts, not fixable issues.

### Schedule

- **Cron**: `17 * * * *` (every hour at :17)
- **Manual trigger**: Available via `workflow_dispatch`

## Full Feedback Loop

```
self-assess (daily 04:37)        auto-fix (hourly :17)
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
                                              Merge ──► Next self-assess
                                                        detects fix, closes issue
```
