# Architecture

## System Overview

Coupon Hub Bot is a Telegram bot running as an ASP.NET Core webhook server. Users interact via private messages; notifications go to a community group chat.

## Key Modules

```
src/CouponHubBot/
├── Program.fs            # Entry point, DI, webhook setup
├── Types.fs              # Domain types (Coupon, User, PendingAddFlow, etc.)
├── Utils.fs              # Shared utilities (date parsing, pluralization)
├── Telemetry.fs          # OpenTelemetry configuration
└── Services/
    ├── BotCommandsSetup.fs   # Registers bot command menu with Telegram
    ├── BotHelpers.fs         # UI helpers, keyboards, formatting utilities
    ├── BotService.fs         # Main update dispatcher (messages, callbacks)
    ├── CallbackHandler.fs    # Inline button callback query handling
    ├── CommandHandler.fs     # Bot command routing and handling
    ├── CouponFlowHandler.fs  # /add wizard state machine
    ├── CouponOcrEngine.fs    # Barcode + text OCR processing
    ├── DbService.fs          # PostgreSQL access via Dapper
    ├── GitHubService.fs      # GitHub API client (feedback issues, agent assignment)
    ├── MembershipService.fs  # Community membership verification and caching
    ├── NotificationService.fs # Notifications to community group chat
    ├── OcrService.fs         # Azure Computer Vision OCR client
    └── ReminderService.fs    # Scheduled reminders (expiring coupons, weekly stats)
```

## Data Flow

1. Telegram sends webhook POST to `/bot`
2. Bot authenticates via `X-Telegram-Bot-Api-Secret-Token` header
3. Update is routed to the appropriate handler (command, callback, message)
4. Handler interacts with PostgreSQL via `DbService` and replies via Telegram API
5. Notifications (coupon added/taken/returned) are sent to the community group

## Infrastructure

- **Database**: PostgreSQL 15.6 with Flyway migrations
- **Container**: Docker image pushed to GHCR (`ghcr.io/szer/coupon-bot`)
- **Orchestration**: ArgoCD with image-reloader (polls GHCR every ~5 min)
- **Observability**: Serilog → Loki, OpenTelemetry → Prometheus

## Copilot Custom Agents

Three AI agents automate different aspects of the project lifecycle:

| Agent | Role | Trigger | Tools |
|-------|------|---------|-------|
| **project** (project manager) | Backlog management, codebase quality analysis | Daily schedule (`project.yml`) | Read-only: search, execute (command allowlist), GitHub CLI |
| **product** (product manager) | User feedback triage, feature prioritization | Daily schedule (`product.yml`) + `user-feedback` label (`feedback-triage.yml`) | Read-only: search, execute (command allowlist), GitHub CLI, Prometheus, Loki |
| **sre** | Production incident response, deploy failure debugging | `deploy-failure` label (`deploy.yml`) | Read-only: ArgoCD, Loki, Prometheus, GitHub CLI |

### Product Agent Data Flow

```
User Signals:
  /feedback command ──→ user_feedback table ──→ GitHub issue (user-feedback label)
  Community chat ──────→ chat_message table ──→ Daily analysis report
  Bot interactions ────→ Prometheus counters ─→ Daily analysis report

Product Agent Processing:
  1. Reads PRODUCT-VISION.md (human-curated authority)
  2. Analyzes signals: feedback, chat themes, usage metrics
  3. Decision: discard (close) or create refined issue (bug/feature-request)

Output:
  Refined issues with clear problem statements, evidence, and priority labels
  → Picked up by coding agent or project manager
```

### Agent Isolation

The coding agent is guardrailed from raw user signals — it only sees refined tickets (`bug`, `feature-request`). Labels `user-feedback`, `product`, `project`, and `deploy-failure` are reserved for their respective agents.

Non-coding agents (project, product) are restricted by three layers of defense:
1. **Command allowlist** in their agent prompts — only `gh issue`, `curl`, and read-only commands are permitted
2. **No path-specific instruction files** — `.github/instructions/` must NOT contain behavioral instructions (workflow steps, branch creation, test commands) because `excludeAgent` only supports `"code-review"` and `"coding-agent"` values, not custom agent names. Any instructions placed there will leak into custom agents and cause them to break character. Coding-agent-specific instructions belong in `.github/copilot-instructions.md` (which custom agents receive too, but their own `.agent.md` prompt overrides it).
3. **Copilot PR Manager** (`copilot-pr-manager.yml`) — a cron workflow (every 5 min) that auto-closes any PR from non-coding agents by detecting `Custom agent used: project/product` in the PR body (skipping in-flight `[WIP]` PRs), and re-runs pending workflow runs for legitimate Copilot coding agent PRs
