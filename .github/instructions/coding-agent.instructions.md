---
applyTo: "**"
excludeAgent: "code-review"
---

# Coding Agent Instructions

## Development Workflow

1. Read relevant `docs/` file for the domain you're changing
2. Create a feature branch from `main`
3. Write/update E2E tests in `tests/CouponHubBot.Tests/`
4. Run `dotnet build -c Release` to verify compilation
5. **Do NOT run `dotnet test`** — Docker-based E2E tests will timeout in the agent environment. The PR CI workflow (`build.yml`) runs tests automatically on proper runners.
6. If adding a new table, add migration in `src/migrations/` with GRANT
7. Update the relevant `docs/` file if behavior changed
8. Create a PR referencing the issue number

## CI/CD

- **PR builds**: `.github/workflows/build.yml` — runs tests, uploads results + container logs
- **Deploy**: `.github/workflows/deploy.yml` — tests → Flyway migrations → GHCR push → deployment verification
- **Test results**: `.github/workflows/test-results.yml` — publishes test report after CI
- **Self-assess**: `.github/workflows/self-assess.yml` — daily self-assessment, gathers metrics, creates orchestration issue for Copilot
- **Agent env**: `.github/workflows/copilot-setup-steps.yml` — sets up .NET SDK, VPN, dependencies

## Agent Skills (`.github/skills/`)

| Skill | When used |
|-------|-----------|
| `deployment-debugging` | Debugging a failed `verify-deploy` step or `deploy-failure` issue |
| `argocd-status` | Checking ArgoCD sync/health status, deployed image tags |
| `loki-logs` | Querying application logs via Loki for errors or patterns |
| `prometheus-metrics` | Checking pod restarts, 5xx rates, health metrics |
| `self-assess` | Daily self-assessment of codebase and infrastructure, backlog management |

The agent has VPN access to `*.internal` hosts (established by `copilot-setup-steps.yml`).

## Debugging Deployment Failures

When assigned a `deploy-failure` issue:

1. Use the `deployment-debugging` skill — it walks through the full investigation flow
2. Read the failed workflow logs to identify which verification phase failed
3. Query ArgoCD, Loki, and Prometheus via the corresponding skills
4. Create a fix PR referencing the `deploy-failure` issue

## Debugging Test Failures

1. Check `test-artifacts/` directory for container logs (bot.log, fake-tg-api.log, postgres.log)
2. Use `fixture.GetBotLogs()` or `fixture.GetAllLogs()` in test code for on-demand log access
3. See [docs/TESTING.md](../../docs/TESTING.md) for Testcontainers architecture and FakeTgApi endpoints

## Important Rules

- Test fixtures use xUnit v3 assembly fixtures (see `tests/CouponHubBot.Tests/Program.fs`)
- Test parallelization is disabled (`DisableTestParallelization = true`) due to shared container state
- CI and agent environments run on Ubuntu (bash). Scripts in `scripts/` use bash.
