---
excludeAgent: "code-review"
---

# Copilot Coding Agent Instructions

You are working on **Coupon Hub Bot**, a Telegram bot written in F# / .NET 10.

## Getting Started

Read [AGENTS.md](../AGENTS.md) first — it is the table of contents for all project documentation.

## Key Documentation

- **Architecture**: `docs/ARCHITECTURE.md` — system overview, module structure
- **Bot Logic**: `docs/TELEGRAM-BOT-LOGIC.md` — commands, wizard flows, callbacks
- **Testing**: `docs/TESTING.md` — how to run tests, container logs, FakeTgApi patterns
- **Database**: `docs/DATABASE.md` — schema, migrations, GRANT conventions
- **Deployment**: `docs/DEPLOYMENT.md` — CI/CD pipeline, ArgoCD, verification

## Development Workflow

1. Create a feature branch from `main`
2. Make changes following the patterns in `docs/`
3. Run `dotnet build -c Release` to verify compilation
4. **Do NOT run `dotnet test`** — Docker-based E2E tests will timeout in the agent environment. The PR CI workflow (`build.yml`) runs tests automatically on push.
5. Create a PR referencing the issue number

## Debugging Deployment Failures

When assigned a `deploy-failure` issue:

1. The `deployment-debugging` skill will guide you through the full investigation
2. You have VPN access to internal services (established by `copilot-setup-steps.yml`)
3. Use the `argocd-status`, `loki-logs`, and `prometheus-metrics` skills to query observability tools
4. Create a fix PR referencing the deploy-failure issue

## Important Rules

- UI text is in **Russian**
- Always parse JSON before comparing Cyrillic strings (see `docs/TESTING.md`)
- New database tables need GRANT for `coupon_hub_bot_service` role
- F# compilation order matters — new files must be added to `.fsproj` in the correct position
- Test fixtures use xUnit v3 assembly fixtures (see `tests/CouponHubBot.Tests/Program.fs`)
