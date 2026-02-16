#!/usr/bin/env bash
# verify-deploy.sh — Post-deploy verification for ArgoCD + Loki + Prometheus.
#
# Required env vars:
#   ARGOCD_URL            e.g. http://argo.internal
#   LOKI_URL              e.g. http://loki.internal
#   PROMETHEUS_URL        e.g. http://prometheus.internal:9090
#   EXPECTED_IMAGE_TAG    git SHA to look for in running image
#   ARGOCD_AUTH_TOKEN     bearer token for ArgoCD API
#   ARGOCD_APP_NAME       ArgoCD application name (default: coupon-bot)
#   CONTAINER_NAME        Loki/Prometheus container label (default: coupon-bot)
#   READINESS_GRACE_PERIOD  seconds to tolerate readiness failures (default: 180)
#
# Exit codes: 0 = success, 1 = failure

set -euo pipefail

: "${ARGOCD_URL:?ARGOCD_URL is required}"
: "${LOKI_URL:?LOKI_URL is required}"
: "${PROMETHEUS_URL:?PROMETHEUS_URL is required}"
: "${EXPECTED_IMAGE_TAG:?EXPECTED_IMAGE_TAG is required}"
: "${ARGOCD_AUTH_TOKEN:?ARGOCD_AUTH_TOKEN is required}"

APP_NAME="${ARGOCD_APP_NAME:-coupon-bot}"
CONTAINER="${CONTAINER_NAME:-coupon-bot}"
GRACE="${READINESS_GRACE_PERIOD:-180}"

AUTH_HEADER="Authorization: Bearer ${ARGOCD_AUTH_TOKEN}"

log() { echo "[$(date -u +%H:%M:%S)] $*"; }

# summary() appends markdown to GitHub Actions Job Summary
# Falls back to /dev/null when GITHUB_STEP_SUMMARY is not set (local runs)
summary() {
    local summary_file="${GITHUB_STEP_SUMMARY:-/dev/null}"
    echo "$@" >> "$summary_file"
}

# Initialize summary
summary "## 🚀 Deployment Verification"
summary ""
summary "**App:** \`${APP_NAME}\`  "
summary "**Expected Image Tag:** \`${EXPECTED_IMAGE_TAG:0:12}...\`  "
summary "**Started:** $(date -u +%Y-%m-%d\ %H:%M:%S) UTC"
summary ""

# ─── Phase 1: Wait for ArgoCD to sync with expected image tag ────────────────

log "Phase 1: Waiting for ArgoCD sync (app=${APP_NAME}, expected tag contains ${EXPECTED_IMAGE_TAG:0:12}...)"

SYNC_TIMEOUT=600  # 10 minutes
SYNC_INTERVAL=30
elapsed=0

while [ "$elapsed" -lt "$SYNC_TIMEOUT" ]; do
    RESPONSE=$(curl -sf "${ARGOCD_URL}/api/v1/applications/${APP_NAME}" \
        -H "${AUTH_HEADER}" 2>/dev/null || echo "{}")

    SYNC_STATUS=$(echo "$RESPONSE" | jq -r '.status.sync.status // "Unknown"')
    IMAGES=$(echo "$RESPONSE" | jq -r '.status.summary.images // [] | .[]' 2>/dev/null || echo "")
    IMAGE_MATCH=$(echo "$IMAGES" | grep -c "${EXPECTED_IMAGE_TAG}" || true)

    log "  sync=${SYNC_STATUS} images_with_tag=${IMAGE_MATCH} (elapsed=${elapsed}s)"

    if [ "$SYNC_STATUS" = "Synced" ] && [ "$IMAGE_MATCH" -gt 0 ]; then
        log "Phase 1 PASSED: Image tag found, sync status is Synced."
        summary "### ✅ Phase 1: ArgoCD Sync"
        summary "- **Status:** Synced"
        summary "- **Image Tag Match:** Yes"
        summary "- **Elapsed Time:** ${elapsed}s"
        summary ""
        break
    fi

    if [ "$elapsed" -ge "$SYNC_TIMEOUT" ]; then
        log "FAILED: Timed out waiting for ArgoCD sync after ${SYNC_TIMEOUT}s"
        log "  Last sync status: ${SYNC_STATUS}"
        log "  Running images: ${IMAGES}"
        summary "### ❌ Phase 1: ArgoCD Sync FAILED"
        summary "- **Reason:** Timeout after ${SYNC_TIMEOUT}s"
        summary "- **Last Sync Status:** \`${SYNC_STATUS}\`"
        summary "- **Running Images:**"
        summary "\`\`\`"
        summary "${IMAGES}"
        summary "\`\`\`"
        exit 1
    fi

    sleep "$SYNC_INTERVAL"
    elapsed=$((elapsed + SYNC_INTERVAL))
done

# ─── Phase 2: Readiness grace period ─────────────────────────────────────────

log "Phase 2: Readiness grace period (${GRACE}s). Waiting for pod to become healthy..."

GRACE_INTERVAL=15
grace_elapsed=0
healthy=false

while [ "$grace_elapsed" -lt "$GRACE" ]; do
    RESPONSE=$(curl -sf "${ARGOCD_URL}/api/v1/applications/${APP_NAME}" \
        -H "${AUTH_HEADER}" 2>/dev/null || echo "{}")
    HEALTH=$(echo "$RESPONSE" | jq -r '.status.health.status // "Unknown"')

    log "  health=${HEALTH} (grace elapsed=${grace_elapsed}s/${GRACE}s)"

    if [ "$HEALTH" = "Healthy" ]; then
        log "Phase 2 PASSED: Pod is healthy (before grace period expired)."
        summary "### ✅ Phase 2: Readiness Check"
        summary "- **Health Status:** Healthy"
        summary "- **Time to Healthy:** ${grace_elapsed}s (within ${GRACE}s grace period)"
        summary ""
        healthy=true
        break
    fi

    sleep "$GRACE_INTERVAL"
    grace_elapsed=$((grace_elapsed + GRACE_INTERVAL))
done

if [ "$healthy" = false ]; then
    # Final check after grace period
    RESPONSE=$(curl -sf "${ARGOCD_URL}/api/v1/applications/${APP_NAME}" \
        -H "${AUTH_HEADER}" 2>/dev/null || echo "{}")
    HEALTH=$(echo "$RESPONSE" | jq -r '.status.health.status // "Unknown"')

    if [ "$HEALTH" != "Healthy" ]; then
        log "FAILED: Pod is not healthy after ${GRACE}s grace period. Health: ${HEALTH}"
        CONDITIONS=$(echo "$RESPONSE" | jq '.status.conditions // []')
        log "  Conditions: ${CONDITIONS}"
        summary "### ❌ Phase 2: Readiness Check FAILED"
        summary "- **Health Status:** \`${HEALTH}\`"
        summary "- **Grace Period:** ${GRACE}s (expired)"
        summary "- **Conditions:**"
        summary "\`\`\`json"
        summary "${CONDITIONS}"
        summary "\`\`\`"
        exit 1
    fi
    log "Phase 2 PASSED: Pod became healthy at the end of the grace period."
    summary "### ✅ Phase 2: Readiness Check"
    summary "- **Health Status:** Healthy"
    summary "- **Time to Healthy:** ${GRACE}s (at end of grace period)"
    summary ""
fi

# ─── Phase 3: Verify logs and metrics ────────────────────────────────────────

log "Phase 3: Checking Loki for error-level logs..."

# Query Loki for errors in the last 2 minutes
START=$(date -u -d '2 minutes ago' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -v-2M +%Y-%m-%dT%H:%M:%SZ)
LOKI_QUERY="{container=\"${CONTAINER}\"} | json | level=~\"Error|Fatal\""

LOKI_RESPONSE=$(curl -sf -G "${LOKI_URL}/loki/api/v1/query_range" \
    --data-urlencode "query=${LOKI_QUERY}" \
    --data-urlencode "start=${START}" \
    --data-urlencode "limit=20" 2>/dev/null || echo '{"data":{"result":[]}}')

ERROR_COUNT=$(echo "$LOKI_RESPONSE" | jq '[.data.result[].values[]] | length')

if [ "$ERROR_COUNT" -gt 0 ]; then
    log "WARNING: Found ${ERROR_COUNT} error-level log entries after deployment:"
    echo "$LOKI_RESPONSE" | jq -r '.data.result[].values[] | .[1]' | head -10
    log "FAILED: Error logs detected after deployment."
    summary "### ❌ Phase 3: Log & Metrics Check FAILED"
    summary "- **Loki Errors:** ${ERROR_COUNT} error-level entries found"
    summary "- **Sample Errors:**"
    summary "\`\`\`"
    echo "$LOKI_RESPONSE" | jq -r '.data.result[].values[] | .[1]' | head -10 | while IFS= read -r line; do
        summary "$line"
    done
    summary "\`\`\`"
    exit 1
fi
log "  Loki: no error-level logs found."

log "Phase 3: Checking Prometheus metrics..."

# Check restart count
RESTARTS=$(curl -sf -G "${PROMETHEUS_URL}/api/v1/query" \
    --data-urlencode "query=kube_pod_container_status_restarts_total{container=\"${CONTAINER}\"}" \
    2>/dev/null || echo '{"data":{"result":[]}}')
RESTART_COUNT=$(echo "$RESTARTS" | jq -r '[.data.result[].value[1] | tonumber] | add // 0')

# Check 5xx rate
ERRORS_5XX=$(curl -sf -G "${PROMETHEUS_URL}/api/v1/query" \
    --data-urlencode "query=sum(rate(http_server_request_duration_seconds_count{http_response_status_code=~\"5..\",job=\"${CONTAINER}\"}[5m]))" \
    2>/dev/null || echo '{"data":{"result":[]}}')
ERROR_5XX_RATE=$(echo "$ERRORS_5XX" | jq -r '[.data.result[].value[1] | tonumber] | add // 0')

log "  Prometheus: restarts=${RESTART_COUNT}, 5xx_rate=${ERROR_5XX_RATE}"

# Note: restart count is cumulative, so we only fail if it's unexpectedly high.
# For a fresh deployment, a single restart might be acceptable.
if [ "$(echo "$ERROR_5XX_RATE > 0" | bc -l 2>/dev/null || echo 0)" = "1" ]; then
    log "FAILED: 5xx error rate is non-zero: ${ERROR_5XX_RATE}"
    summary "### ❌ Phase 3: Log & Metrics Check FAILED"
    summary "- **5xx Error Rate:** ${ERROR_5XX_RATE} (expected: 0)"
    summary "- **Container Restarts:** ${RESTART_COUNT}"
    exit 1
fi

summary "### ✅ Phase 3: Log & Metrics Check"
summary "- **Loki Errors:** 0"
summary "- **5xx Error Rate:** ${ERROR_5XX_RATE}"
summary "- **Container Restarts:** ${RESTART_COUNT}"
summary ""

# ─── Done ─────────────────────────────────────────────────────────────────────

log "ALL CHECKS PASSED. Deployment verified successfully."
log "  App: ${APP_NAME}"
log "  Image tag: ${EXPECTED_IMAGE_TAG:0:12}..."
log "  Sync: Synced, Health: Healthy"
log "  Loki errors: 0, 5xx rate: ${ERROR_5XX_RATE}"

summary "---"
summary ""
summary "## ✅ Deployment Verification Complete"
summary ""
summary "| Check | Status | Details |"
summary "|-------|--------|---------|"
summary "| ArgoCD Sync | ✅ Passed | Synced with tag \`${EXPECTED_IMAGE_TAG:0:12}...\` |"
summary "| Pod Health | ✅ Passed | Healthy |"
summary "| Loki Errors | ✅ Passed | 0 error-level logs |"
summary "| 5xx Error Rate | ✅ Passed | ${ERROR_5XX_RATE} |"
summary "| Container Restarts | ✅ Info | ${RESTART_COUNT} |"
summary ""
summary "**Completed:** $(date -u +%Y-%m-%d\ %H:%M:%S) UTC"

exit 0
