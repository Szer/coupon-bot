---
name: deployment-debugging
description: Orchestrates debugging of failed deployments. Use when a deploy-failure issue is created, when verify-deploy fails, or when asked to investigate a deployment problem. Guides you through reading CI logs, querying ArgoCD, Loki, and Prometheus to find root cause.
---

# Deployment Debugging

When a deployment verification fails, follow this process to diagnose and fix the issue.

## Prerequisites

- VPN is pre-established via `copilot-setup-steps.yml` (WireGuard to `*.internal` hosts)
- `$ARGOCD_AUTH_TOKEN` is available from the `copilot` environment

## Step 0: Verify the Image Exists in GHCR

Before investigating infrastructure, confirm the Docker image was actually pushed:

```bash
gh api users/Szer/packages/container/coupon-bot/versions --jq '.[0].metadata.container.tags[]' | head -5
```

If the expected tag (commit SHA) is missing, the build/push step failed — check the `deploy` job logs, not ArgoCD.

## Step 1: Read the Failed Workflow Logs

Use the GitHub MCP tools to understand what phase failed:

1. Call `list_workflow_runs` for the repository to find the failed deploy workflow run
2. Call `get_job_logs` for the `verify-deploy` job to read the `verify-deploy.sh` output

The script has 3 phases — identify which one failed:

| Phase | Log marker | Meaning |
|-------|-----------|---------|
| Phase 1 | `FAILED: Timed out waiting for ArgoCD sync` | ArgoCD did not pick up the new image within 10 minutes |
| Phase 2 | `FAILED: Pod is not healthy after` | Pod readiness probes failed beyond the 3-minute grace period |
| Phase 3 (Loki) | `FAILED: Error logs detected` | Application is producing Error/Fatal log entries |
| Phase 3 (Prometheus) | `FAILED: 5xx error rate is non-zero` | Application is returning HTTP 5xx responses |

## Step 2: Query Observability Services Directly

Based on which phase failed, run the appropriate queries. VPN access is already established.

### If Phase 1 failed (ArgoCD sync)

Check why the image was not picked up:

```bash
# Check current app status and image
curl -s http://argo.internal/api/v1/applications/coupon-bot \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" | jq '{
    sync: .status.sync.status,
    health: .status.health.status,
    images: (.status.summary.images // []),
    conditions: [.status.conditions[]? | {type, message}]
  }'
```

```bash
# Check sync operation state for errors
curl -s http://argo.internal/api/v1/applications/coupon-bot \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" \
  | jq '.status.operationState'
```

Common causes: image tag mismatch in ArgoCD app manifest, GHCR push failure, image-reloader not configured.

### If Phase 2 failed (pod health)

Check pod conditions, events, and container status:

```bash
# Get detailed resource status including health info
curl -s http://argo.internal/api/v1/applications/coupon-bot/resource-tree \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" \
  | jq '.nodes[] | select(.kind == "Pod") | {name, health: .health, info: .info}'
```

```bash
# Check for stale replicasets (old RS lingering, holding resources)
curl -s http://argo.internal/api/v1/applications/coupon-bot/resource-tree \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" \
  | jq '.nodes[] | select(.kind == "ReplicaSet") | {name, health: .health}'
```

```bash
# Check restart count
curl -s -G 'http://prometheus.internal:9090/api/v1/query' \
  --data-urlencode 'query=kube_pod_container_status_restarts_total{container="coupon-bot"}' \
  | jq '.data.result[].value[1]'
```

```bash
# Check for CrashLoopBackOff, ImagePullBackOff, OOMKilled, etc.
curl -s -G 'http://prometheus.internal:9090/api/v1/query' \
  --data-urlencode 'query=kube_pod_container_status_waiting_reason{container="coupon-bot"}' \
  | jq '.data.result[] | {reason: .metric.reason, value: .value[1]}'
```

```bash
# Check memory usage (detect OOM pressure)
curl -s -G 'http://prometheus.internal:9090/api/v1/query' \
  --data-urlencode 'query=container_memory_working_set_bytes{container="coupon-bot"}' \
  | jq '.data.result[].value[1]'
```

Common causes: crash loop (check application logs), OOMKilled (check memory usage vs limits), resource limits, missing config/secrets.

### If Phase 3 failed (Loki errors)

Get the actual error log entries:

```bash
START=$(date -u -d '10 minutes ago' +%Y-%m-%dT%H:%M:%SZ)
curl -s -G http://loki.internal/loki/api/v1/query_range \
  --data-urlencode 'query={container="coupon-bot"} | json | level=~"Error|Fatal"' \
  --data-urlencode "start=$START" \
  --data-urlencode 'limit=50' \
  | jq '.data.result[].values[] | .[1]'
```

Read the error messages to understand the application-level failure.

### If Phase 3 failed (Prometheus 5xx)

Check which endpoints are returning errors:

```bash
curl -s -G 'http://prometheus.internal:9090/api/v1/query' \
  --data-urlencode 'query=sum by (http_route) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"5..",job="coupon-bot"}[5m]))' \
  | jq '.data.result[]'
```

## Step 3: Determine Root Cause and Fix

Based on the diagnostic data:

1. Identify the root cause (code bug, config issue, infrastructure problem)
2. If it is a code or config issue you can fix, create a PR with the fix
3. If it is an infrastructure issue (e.g., database unreachable, GHCR auth failure), document the finding in a comment on the issue
4. Reference the original deploy-failure issue in your fix PR

## Notes

- The image reloader in ArgoCD polls every ~5 minutes; sync delays up to 10 minutes are normal
- Readiness probes may fail for up to 3 minutes after deployment — this is expected
- Restart count is cumulative; a single restart after deployment may be acceptable
- Always check both Loki (application logs) AND Prometheus (infrastructure metrics) for a complete picture
- If old ReplicaSets are lingering, you can delete them via the `argocd-status` skill's "Delete an old replicaset" operation
