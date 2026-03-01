# Self-Assessment

## Overview

The daily self-assessment workflow acts as an **automated product manager**. It runs every day at 04:37 UTC, gathers infrastructure metrics, and creates an orchestration issue for Copilot to analyze the system and maintain a backlog.

## How It Works

```
┌─────────────────────────────────────────────────────────────────┐
│  self-assess.yml (daily at 04:37 UTC)                           │
│                                                                 │
│  Job 1: cleanup                                                 │
│    - Close stale orchestration issues from previous runs        │
│    - Close & delete Copilot PRs from previous runs              │
│                                                                 │
│  Job 2: self-assess (needs: cleanup)                            │
│    1. Connect VPN                                               │
│    2. Run gather-metrics.sh → Prometheus + Loki + ArgoCD        │
│    3. Create orchestration issue with metrics in body           │
│    4. Assign @copilot                                           │
│    5. Disconnect VPN                                            │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  Copilot (self-assess skill)                                    │
│                                                                 │
│  1. Read metrics snapshot from issue body                       │
│  2. Analyze codebase (TODOs, test gaps, large files, security)  │
│  3. Query live Loki/Prometheus if needed                        │
│  4. Review existing open issues                                 │
│  5. Create / bump / close self-assess backlog issues            │
│  6. Close orchestration issue with summary                      │
│                                                                 │
│  Note: Copilot may auto-create an empty PR (platform behavior)  │
│  — this is cleaned up by the next day's cleanup job             │
└─────────────────────────────────────────────────────────────────┘
```

## Components

| Component | Path | Purpose |
|-----------|------|---------|
| Workflow | `.github/workflows/self-assess.yml` | Scheduled trigger, VPN, metrics collection, issue creation |
| Metrics script | `scripts/gather-metrics.sh` | Queries Prometheus, Loki, ArgoCD; outputs markdown report |
| Skill | `.github/skills/self-assess/SKILL.md` | Instructions for Copilot on how to analyze and manage backlog |
| Label | `self-assess` | Applied to all backlog issues created by this flow |

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

## Backlog Management Rules

- **Create**: New issue for a newly discovered problem (labeled `self-assess`)
- **Bump**: Comment on existing issue if the same problem persists
- **Close**: Close issue if the underlying problem is resolved
- **Never assign**: Backlog issues are left unassigned — a future companion workflow will pick the most-bumped issue for implementation

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

The Copilot coding agent has a platform-level behavior: when assigned to an issue, it automatically creates a branch and PR. This cannot be disabled via instructions. The cleanup job handles this:

1. **Stale orchestration issues**: If Copilot fails to close the orchestration issue (e.g., network timeout), the next day's cleanup job closes it automatically.
2. **Empty PRs**: Copilot creates a PR from `copilot/daily-self-assessment-*` branches even when told not to make code changes. The cleanup job closes these PRs and deletes the branches.
3. **False-positive issues**: If a backlog issue was created due to a temporary condition (e.g., Loki briefly unreachable), the next self-assessment should close it when the condition resolves.

The cleanup runs at the start of each workflow invocation, giving Copilot ~24 hours to complete its analysis before artifacts are cleaned up.

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

## Future: Auto-Implementation Companion

A planned companion workflow will:
1. Run daily after self-assessment completes
2. Query open `self-assess` issues
3. Rank by number of bump comments (most-bumped = highest priority)
4. Assign Copilot to the top issue for automatic implementation
5. This creates a **self-improving codebase** feedback loop
