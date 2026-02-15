---
name: argocd-status
description: Query ArgoCD application status, sync state, deployed image tags, and failure details. Manage resources (restart pods, delete replicasets). Use when verifying deployments, debugging ArgoCD sync issues, or checking what version is currently running.
---

# ArgoCD Status Queries

## Prerequisites

- WireGuard VPN must be connected (established by `copilot-setup-steps.yml`)
- `$ARGOCD_AUTH_TOKEN` must be set (available from the `copilot` environment)
- API base: `http://argo.internal`
- All requests need header: `Authorization: Bearer $ARGOCD_AUTH_TOKEN`

## Verify Connectivity

Always run this first. If it fails, VPN is likely down or the token is invalid.

```bash
curl -sf http://argo.internal/api/v1/applications \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" > /dev/null \
  && echo "ArgoCD reachable" || echo "ERROR: cannot reach ArgoCD (VPN down?)"
```

## Common Operations

### Check app status (sync + health + images)

```bash
curl -s http://argo.internal/api/v1/applications/coupon-bot \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" | jq '{
    sync: .status.sync.status,
    health: .status.health.status,
    images: (.status.summary.images // []),
    conditions: [.status.conditions[]? | {type, message}]
  }'
```

Expected healthy state: `sync: "Synced"`, `health: "Healthy"`.

### Get current deployed image tag

```bash
curl -s http://argo.internal/api/v1/applications/coupon-bot \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" \
  | jq -r '.status.summary.images[]'
```

### Check sync operation state (for debugging failures)

```bash
curl -s http://argo.internal/api/v1/applications/coupon-bot \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" \
  | jq '.status.operationState'
```

### Get resource tree (pod-level details)

```bash
curl -s http://argo.internal/api/v1/applications/coupon-bot/resource-tree \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" \
  | jq '.nodes[] | select(.kind == "Pod") | {name, health: .health}'
```

### List managed resources (pods, replicasets, deployments)

Use this to find exact resource names before deleting or restarting.

```bash
curl -s "http://argo.internal/api/v1/applications/coupon-bot/managed-resources" \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" \
  | jq '.items[] | select(.kind == "ReplicaSet" or .kind == "Pod") | {kind, name: .name, namespace}'
```

### List all applications

```bash
curl -s http://argo.internal/api/v1/applications \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" \
  | jq '.items[].metadata.name'
```

## Write Operations

### Trigger a manual sync

```bash
curl -s -X POST http://argo.internal/api/v1/applications/coupon-bot/sync \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{}'
```

### Delete a specific pod (triggers restart via ReplicaSet)

First use "List managed resources" above to find the exact pod name.

```bash
curl -s -X DELETE "http://argo.internal/api/v1/applications/coupon-bot/resource" \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" \
  -G --data-urlencode "namespace=coupon-bot" \
  --data-urlencode "resourceName=POD_NAME" \
  --data-urlencode "kind=Pod" \
  --data-urlencode "version=v1"
```

### Delete an old replicaset

First use "List managed resources" above to find the exact replicaset name.

```bash
curl -s -X DELETE "http://argo.internal/api/v1/applications/coupon-bot/resource" \
  -H "Authorization: Bearer $ARGOCD_AUTH_TOKEN" \
  -G --data-urlencode "namespace=coupon-bot" \
  --data-urlencode "resourceName=REPLICASET_NAME" \
  --data-urlencode "kind=ReplicaSet" \
  --data-urlencode "version=v1" \
  --data-urlencode "group=apps"
```

## Error Codes

| HTTP Code | Meaning |
|-----------|---------|
| `401` | Token expired or invalid |
| `403` | RBAC insufficient for this operation |
| `404` | Application name is wrong |
| Connection refused | VPN not connected or ArgoCD server down |

## Notes

- The app name for this project is `coupon-bot`
- Image reloader polls every ~5 minutes; after pushing a new image to GHCR, allow up to 10 minutes for sync
- Some pods have startup processes; readiness probes may fail for up to 3 minutes after deployment
- `conditions` array contains warnings/errors — check it when status is not Healthy/Synced
- Use "List managed resources" to discover exact names before any delete operation
