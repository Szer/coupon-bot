# Deployment

## Pipeline

1. Push to `main` triggers `.github/workflows/deploy.yml`
2. Tests run (`dotnet test -c Release`)
3. WireGuard VPN connects to production network
4. Flyway migrations applied to production PostgreSQL
5. Docker image built for `linux/arm64` and pushed to GHCR:
   - `ghcr.io/szer/coupon-bot:latest`
   - `ghcr.io/szer/coupon-bot:<git-sha>`
6. Deployment verification job runs (see below)

## ArgoCD Integration

ArgoCD image-reloader polls GHCR every ~5 minutes. When a new image is detected:
1. Image reloader updates the image tag in the IaC private repo
2. ArgoCD detects the change and syncs the application
3. New pod rolls out with the updated image

## Deployment Verification (`verify-deploy` job)

After GHCR push, a separate job verifies the deployment over VPN:

**Phase 1 — ArgoCD sync** (up to 10 min):
- Polls `http://argo.internal/api/v1/applications/coupon-bot` every 30s
- Waits for sync status = `Synced` and image tag containing the git SHA

**Phase 2 — Readiness grace period** (3 min):
- Some pods have startup processes; readiness probes may fail initially
- Polls health status every 15s; if `Healthy` before 3 min, proceeds early

**Phase 3 — Health verification**:
- ArgoCD health must be `Healthy`
- Loki (`http://loki.internal`): no Error/Fatal log entries in recent 2 minutes
- Prometheus (`http://prometheus.internal:9090`): zero 5xx error rate

Script: `scripts/verify-deploy.sh` (parameterized via env vars, reusable across repos)

## Automated Failure Response

When any job in the deploy pipeline fails, the workflow automatically:
1. Creates a GitHub issue labeled `deploy-failure` with the workflow run link and commit SHA
2. Assigns the issue to the **SRE custom agent** (`sre`) via REST API (requires `COPILOT_ASSIGN_PAT` secret)
3. SRE agent classifies incident severity (P1/P2/P3) and assesses production impact
4. If production is impacted (P1), SRE agent rolls back via ArgoCD before investigating
5. SRE agent queries ArgoCD, Loki, and Prometheus via VPN to diagnose root cause
6. If a code fix is needed, SRE agent creates a `priority-high` issue and assigns the coding agent
7. SRE agent closes the deploy-failure issue with a structured incident report

See `.github/agents/sre.agent.md` for the full incident response runbook.

## Manual Deploy

`.github/workflows/just-deploy.yml` — skips tests, builds and pushes image only.

## Rollback

**Important:** ArgoCD has auto-sync enabled from the `Szer/my-infra` IaC repo. Any rollback will be overwritten unless auto-sync is disabled first. The SRE agent handles this automatically for P1 incidents.

Roll back by:
- **SRE agent (P1 only)**: Disables ArgoCD auto-sync, rolls back to the previous known-good image via ArgoCD rollback API, then escalates to coding agent. Auto-sync must be re-enabled after the fix is deployed.
- **Manual**: Disable ArgoCD auto-sync, re-deploy the previous git SHA or use ArgoCD UI to sync to a previous revision, then re-enable auto-sync.
