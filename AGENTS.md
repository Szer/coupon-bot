# Coupon Hub Bot — Agent Guide

Telegram-бот для совместного управления купонами Dunnes в закрытом сообществе.
Язык интерфейса — русский.

## Tech Stack

- **F# / .NET 10**, ASP.NET Core (webhook), PostgreSQL, Dapper
- **Telegram.Bot 22.8.1** — bot framework
- Flyway — database migrations
- Docker — containerization, Testcontainers for E2E tests
- OpenTelemetry — traces and metrics, Serilog — structured logging

## Documentation Map

| Topic | File | Summary |
|-------|------|---------|
| Architecture | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | System overview, data flow, key modules |
| Bot Logic | [docs/TELEGRAM-BOT-LOGIC.md](docs/TELEGRAM-BOT-LOGIC.md) | Commands, wizard flows, callback patterns |
| Testing | [docs/TESTING.md](docs/TESTING.md) | Testcontainers setup, fakes, how to debug |
| Database | [docs/DATABASE.md](docs/DATABASE.md) | Schema, migrations, GRANT conventions |
| OCR | [docs/OCR.md](docs/OCR.md) | OCR pipeline, Azure integration, caching |
| Deployment | [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | CI/CD pipeline, ArgoCD, verification |
| Observability | [docs/OBSERVABILITY.md](docs/OBSERVABILITY.md) | Logging, metrics, tracing |

## Key Conventions

- UI text is in **Russian** (кириллица). Never compare raw JSON for Cyrillic — always parse with `JsonDocument`.
- F# idioms: `task { }` CE for async, records with `[<CLIMutable>]` for Dapper, `option` types.
- Database role: `coupon_hub_bot_service`. Always add `GRANT` in migrations for new tables.
- CI and agent environments run on Ubuntu (bash). Scripts in `scripts/` use bash.

## CI/CD

- **PR builds**: `.github/workflows/build.yml` — runs tests, uploads results + container logs
- **Deploy**: `.github/workflows/deploy.yml` — tests → Flyway migrations → GHCR push → deployment verification
- **Test results**: `.github/workflows/test-results.yml` — publishes test report after CI
- **Agent env**: `.github/workflows/copilot-setup-steps.yml` — sets up .NET SDK, VPN, dependencies for Copilot coding agent

## Agent Skills (`.github/skills/`)

These skills are automatically loaded by the Copilot coding agent when relevant:

| Skill | When used |
|-------|-----------|
| `deployment-debugging` | Debugging a failed `verify-deploy` step or `deploy-failure` issue |
| `argocd-status` | Checking ArgoCD sync/health status, deployed image tags |
| `loki-logs` | Querying application logs via Loki for errors or patterns |
| `prometheus-metrics` | Checking pod restarts, 5xx rates, health metrics |

The agent has VPN access to `*.internal` hosts (established by `copilot-setup-steps.yml`).

## When Adding a New Feature

1. Read relevant `docs/` file for the domain you're changing
2. Write/update E2E tests in `tests/CouponHubBot.Tests/`
3. Run `dotnet build -c Release` to verify compilation
4. **Do NOT run `dotnet test`** — Docker-based E2E tests will timeout in the agent environment. The PR CI workflow (`build.yml`) runs tests automatically on proper runners.
5. If adding a new table, add migration in `src/migrations/` with GRANT
6. Update the relevant `docs/` file if behavior changed

## When Debugging a Test Failure

1. Check `test-artifacts/` directory for container logs (bot.log, fake-tg-api.log, postgres.log)
2. Use `fixture.GetBotLogs()` or `fixture.GetAllLogs()` in test code for on-demand log access
3. See [docs/TESTING.md](docs/TESTING.md) for Testcontainers architecture and FakeTgApi endpoints

## When Debugging a Deployment Failure

1. Use the `deployment-debugging` skill — it walks through the full investigation flow
2. Read the failed workflow logs to identify which verification phase failed
3. Query ArgoCD (`argocd-status` skill), Loki (`loki-logs` skill), and Prometheus (`prometheus-metrics` skill)
4. Create a fix PR referencing the `deploy-failure` issue
