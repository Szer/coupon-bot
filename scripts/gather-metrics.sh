#!/usr/bin/env bash
# gather-metrics.sh — Collects infrastructure metrics for daily self-assessment.
#
# Required env vars:
#   PROMETHEUS_URL        e.g. http://prometheus.internal:9090
#   LOKI_URL              e.g. http://loki.internal
#   ARGOCD_URL            e.g. http://argo.internal
#   ARGOCD_AUTH_TOKEN     bearer token for ArgoCD API
#   CONTAINER_NAME        container label (default: coupon-bot)
#
# Output: structured markdown report to stdout

set -euo pipefail

: "${PROMETHEUS_URL:?PROMETHEUS_URL is required}"
: "${LOKI_URL:?LOKI_URL is required}"
: "${ARGOCD_URL:?ARGOCD_URL is required}"
: "${ARGOCD_AUTH_TOKEN:?ARGOCD_AUTH_TOKEN is required}"

CONTAINER="${CONTAINER_NAME:-coupon-bot}"
APP_NAME="${ARGOCD_APP_NAME:-coupon-bot}"
AUTH_HEADER="Authorization: Bearer ${ARGOCD_AUTH_TOKEN}"

log() { echo "[$(date -u +%H:%M:%S)] $*" >&2; }

# Helper: query Prometheus instant endpoint, return raw JSON
prom_query() {
    curl -sf -G "${PROMETHEUS_URL}/api/v1/query" \
        --data-urlencode "query=$1" 2>/dev/null || echo '{"data":{"result":[]}}'
}

# Helper: extract scalar value from Prometheus response (first result)
prom_value() {
    echo "$1" | jq -r '[.data.result[].value[1]] | first // "N/A"'
}

# ─── Verify connectivity ─────────────────────────────────────────────────────

log "Verifying connectivity..."

PROM_OK=$(curl -sf "${PROMETHEUS_URL}/-/healthy" 2>/dev/null && echo "yes" || echo "no")
LOKI_OK=$(curl -sf "${LOKI_URL}/ready" 2>/dev/null && echo "yes" || echo "no")
ARGO_OK=$(curl -sf "${ARGOCD_URL}/api/v1/applications" -H "${AUTH_HEADER}" -o /dev/null 2>/dev/null && echo "yes" || echo "no")

log "Prometheus: ${PROM_OK}, Loki: ${LOKI_OK}, ArgoCD: ${ARGO_OK}"

# ─── Prometheus metrics ───────────────────────────────────────────────────────

log "Querying Prometheus metrics..."

# ── Current status (instant gauges) ──

PROCESS_UP_JSON=$(prom_query "up{job=\"${CONTAINER}\"}")
PROCESS_UP=$(prom_value "$PROCESS_UP_JSON")

POD_READY_JSON=$(prom_query "min(kube_pod_status_ready{pod=~\"${CONTAINER}.*\",condition=\"true\"})")
POD_READY=$(prom_value "$POD_READY_JSON")

MEMORY_JSON=$(prom_query "sum(container_memory_working_set_bytes{container=\"${CONTAINER}\"})")
MEMORY_BYTES=$(prom_value "$MEMORY_JSON")
if [ "$MEMORY_BYTES" != "N/A" ]; then
    MEMORY_MB=$(echo "$MEMORY_BYTES" | awk '{printf "%.1f", $1/1048576}')
else
    MEMORY_MB="N/A"
fi

RESTARTS_JSON=$(prom_query "sum(kube_pod_container_status_restarts_total{container=\"${CONTAINER}\"})")
RESTART_COUNT=$(prom_value "$RESTARTS_JSON")

WAITING_JSON=$(prom_query "kube_pod_container_status_waiting_reason{container=\"${CONTAINER}\"}")
WAITING_REASONS=$(echo "$WAITING_JSON" | jq -r '[.data.result[] | select((.value[1] | tonumber) > 0) | .metric.reason + "=" + .value[1]] | join(", ")' 2>/dev/null)
[ -z "$WAITING_REASONS" ] && WAITING_REASONS="none"

# ── 24h trends ──

MEMORY_MAX_JSON=$(prom_query "max_over_time(sum(container_memory_working_set_bytes{container=\"${CONTAINER}\"})[24h:5m])")
MEMORY_MAX_BYTES=$(prom_value "$MEMORY_MAX_JSON")
if [ "$MEMORY_MAX_BYTES" != "N/A" ]; then
    MEMORY_MAX_MB=$(echo "$MEMORY_MAX_BYTES" | awk '{printf "%.1f", $1/1048576}')
else
    MEMORY_MAX_MB="N/A"
fi

CPU_AVG_JSON=$(prom_query "avg_over_time(sum(rate(container_cpu_usage_seconds_total{container=\"${CONTAINER}\"}[5m]))[24h:5m])")
CPU_AVG=$(echo "$CPU_AVG_JSON" | jq -r '[.data.result[].value[1] | tonumber] | add // 0' 2>/dev/null || echo "N/A")
if [ "$CPU_AVG" != "N/A" ] && [ "$CPU_AVG" != "0" ]; then
    CPU_AVG_PERCENT=$(echo "$CPU_AVG" | awk '{printf "%.2f%%", $1*100}')
else
    CPU_AVG_PERCENT="${CPU_AVG}"
fi

CPU_MAX_JSON=$(prom_query "max_over_time(sum(rate(container_cpu_usage_seconds_total{container=\"${CONTAINER}\"}[5m]))[24h:5m])")
CPU_MAX=$(echo "$CPU_MAX_JSON" | jq -r '[.data.result[].value[1] | tonumber] | add // 0' 2>/dev/null || echo "N/A")
if [ "$CPU_MAX" != "N/A" ] && [ "$CPU_MAX" != "0" ]; then
    CPU_MAX_PERCENT=$(echo "$CPU_MAX" | awk '{printf "%.2f%%", $1*100}')
else
    CPU_MAX_PERCENT="${CPU_MAX}"
fi

TOTAL_REQUESTS_JSON=$(prom_query "sum(increase(http_server_request_duration_seconds_count{job=\"${CONTAINER}\"}[24h]))")
TOTAL_REQUESTS=$(echo "$TOTAL_REQUESTS_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "N/A")

TOTAL_5XX_JSON=$(prom_query "sum(increase(http_server_request_duration_seconds_count{http_response_status_code=~\"5..\",job=\"${CONTAINER}\"}[24h]))")
TOTAL_5XX=$(echo "$TOTAL_5XX_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "N/A")

THROTTLE_JSON=$(prom_query "sum(increase(container_cpu_cfs_throttled_periods_total{container=\"${CONTAINER}\"}[24h]))")
THROTTLE_TOTAL=$(echo "$THROTTLE_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "N/A")

# ─── Loki logs ────────────────────────────────────────────────────────────────

log "Querying Loki logs (last 24h)..."

START_24H=$(date -u -d '24 hours ago' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -v-24H +%Y-%m-%dT%H:%M:%SZ)

# Error/Fatal count
ERROR_LOGS_RESPONSE=$(curl -sf -G "${LOKI_URL}/loki/api/v1/query_range" \
    --data-urlencode "query={container=\"${CONTAINER}\"} | json | level=~\"Error|Fatal\"" \
    --data-urlencode "start=${START_24H}" \
    --data-urlencode "limit=200" 2>/dev/null || echo '{"data":{"result":[]}}')
ERROR_LOG_COUNT=$(echo "$ERROR_LOGS_RESPONSE" | jq '[.data.result[].values[]] | length')

# Top unique error messages (deduplicated, counts only — messages redacted for public issues)
TOP_ERRORS_COUNT=$(echo "$ERROR_LOGS_RESPONSE" | jq -r '
    [.data.result[].values[] | .[1]] |
    map(. as $line | try (fromjson | .RenderedMessage // .message // .msg // $line) catch $line) |
    group_by(.) |
    map({count: length}) |
    sort_by(-.count) |
    .[:10] |
    .[] |
    "  - \(.count) occurrence(s)"
' 2>/dev/null || echo "  - (parse error)")

# Warning count
WARN_LOGS_RESPONSE=$(curl -sf -G "${LOKI_URL}/loki/api/v1/query_range" \
    --data-urlencode "query={container=\"${CONTAINER}\"} | json | level=\"Warning\"" \
    --data-urlencode "start=${START_24H}" \
    --data-urlencode "limit=200" 2>/dev/null || echo '{"data":{"result":[]}}')
WARN_LOG_COUNT=$(echo "$WARN_LOGS_RESPONSE" | jq '[.data.result[].values[]] | length')

# Total log volume (last 5m rate extrapolated)
LOG_VOLUME_JSON=$(curl -sf -G "${LOKI_URL}/loki/api/v1/query" \
    --data-urlencode "query=count_over_time({container=\"${CONTAINER}\"}[24h])" \
    2>/dev/null || echo '{"data":{"result":[]}}')
LOG_VOLUME=$(echo "$LOG_VOLUME_JSON" | jq -r '[.data.result[].value[1] | tonumber] | add // 0' 2>/dev/null || echo "N/A")

# ─── ArgoCD status ────────────────────────────────────────────────────────────

log "Querying ArgoCD status..."

ARGO_RESPONSE=$(curl -sf "${ARGOCD_URL}/api/v1/applications/${APP_NAME}" \
    -H "${AUTH_HEADER}" 2>/dev/null || echo "{}")

SYNC_STATUS=$(echo "$ARGO_RESPONSE" | jq -r '.status.sync.status // "Unknown"')
HEALTH_STATUS=$(echo "$ARGO_RESPONSE" | jq -r '.status.health.status // "Unknown"')
DEPLOYED_IMAGES=$(echo "$ARGO_RESPONSE" | jq -r '.status.summary.images // [] | .[]' 2>/dev/null || echo "Unknown")
ARGO_CONDITIONS=$(echo "$ARGO_RESPONSE" | jq -r '[.status.conditions[]? | "\(.type): \(.message)"] | join("\n")' 2>/dev/null)
[ -z "$ARGO_CONDITIONS" ] && ARGO_CONDITIONS="none"

# ─── Output markdown report ──────────────────────────────────────────────────

log "Generating report..."

cat <<EOF
## Infrastructure Health (Prometheus)

### Current Status

| Metric | Value |
|--------|-------|
| Process Up | ${PROCESS_UP} |
| Pod Ready | ${POD_READY} |
| Memory Usage | ${MEMORY_MB} MB |
| Container Restarts (total) | ${RESTART_COUNT} |
| Waiting Reasons | ${WAITING_REASONS} |

### 24h Trends

| Metric | Value |
|--------|-------|
| Peak Memory | ${MEMORY_MAX_MB} MB |
| Avg CPU | ${CPU_AVG_PERCENT} |
| Peak CPU | ${CPU_MAX_PERCENT} |
| Total Requests | ${TOTAL_REQUESTS} |
| Total 5xx Errors | ${TOTAL_5XX} |
| CPU Throttled Periods | ${THROTTLE_TOTAL} |

### Connectivity

| Service | Reachable |
|---------|-----------|
| Prometheus | ${PROM_OK} |
| Loki | ${LOKI_OK} |
| ArgoCD | ${ARGO_OK} |

## Error Logs (24h via Loki)

- **Error/Fatal entries**: ${ERROR_LOG_COUNT}
- **Warning entries**: ${WARN_LOG_COUNT}
- **Total log volume (24h)**: ${LOG_VOLUME} lines

### Top Error Patterns (counts only — query Loki for details)
${TOP_ERRORS_COUNT:-  - (none)}

## Deployment Status (ArgoCD)

| Field | Value |
|-------|-------|
| Sync Status | ${SYNC_STATUS} |
| Health Status | ${HEALTH_STATUS} |
| Deployed Image | ${DEPLOYED_IMAGES} |

### Conditions
${ARGO_CONDITIONS}
EOF

log "Report generated successfully."
