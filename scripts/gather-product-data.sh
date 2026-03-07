#!/usr/bin/env bash
# gather-product-data.sh — Collects product insights for the product agent.
#
# Required env vars:
#   PROMETHEUS_URL        e.g. http://prometheus.internal:9090
#   DATABASE_URL          e.g. postgresql://user:pass@host:5432/db
#                         Only SELECT access is required. Use a read-only role
#                         or the existing service role — no write queries are run.
#   GITHUB_REPOSITORY     e.g. Szer/coupon-bot (set by GitHub Actions)
#
# Optional env vars:
#   LOKI_URL              e.g. http://loki.internal (for error context)
#   CONTAINER_NAME        container label (default: coupon-bot)
#
# Output: structured markdown report to stdout

set -euo pipefail

: "${PROMETHEUS_URL:?PROMETHEUS_URL is required}"
: "${DATABASE_URL:?DATABASE_URL is required}"

CONTAINER="${CONTAINER_NAME:-coupon-bot}"
REPO="${GITHUB_REPOSITORY:-Szer/coupon-bot}"

log() { echo "[$(date -u +%H:%M:%S)] $*" >&2; }

# Helper: query Prometheus instant endpoint
prom_query() {
    local query="$1"
    local attempt
    for attempt in 1 2 3; do
        if result=$(curl -sf -G \
            --connect-timeout 5 \
            --max-time 20 \
            "${PROMETHEUS_URL}/api/v1/query" \
            --data-urlencode "query=${query}" 2>/dev/null); then
            echo "$result"
            return 0
        fi
        log "Prometheus query failed (attempt ${attempt}/3), retrying..."
        sleep 1
    done
    log "Prometheus query failed after 3 attempts, returning empty result."
    echo '{"data":{"result":[]}}'
}

# Helper: query PostgreSQL via psql
db_query() {
    PGCONNECT_TIMEOUT="${PGCONNECT_TIMEOUT:-5}" \
        psql "${DATABASE_URL}" -v ON_ERROR_STOP=1 -t -A -F $'\t' -c "$1" 2>/dev/null || echo ""
}

# ─── Bot usage metrics (Prometheus) ──────────────────────────────────────────

log "Querying bot usage metrics..."

# Command usage (7 days)
CMD_7D_JSON=$(prom_query "sort_desc(sum by (command)(increase(couponhubbot_command_total[7d])))")
CMD_7D=$(echo "$CMD_7D_JSON" | jq -r '
    [.data.result[] | {cmd: .metric.command, count: (.value[1] | tonumber | floor)}]
    | sort_by(-.count)
    | .[] | "| \(.cmd) | \(.count) |"
' 2>/dev/null)
[ -z "$CMD_7D" ] && CMD_7D="| (no data) | - |"

# Callback actions (7 days)
CB_7D_JSON=$(prom_query "sort_desc(sum by (action)(increase(couponhubbot_callback_total[7d])))")
CB_7D=$(echo "$CB_7D_JSON" | jq -r '
    [.data.result[] | {action: .metric.action, count: (.value[1] | tonumber | floor)}]
    | sort_by(-.count)
    | .[] | "| \(.action) | \(.count) |"
' 2>/dev/null)
[ -z "$CB_7D" ] && CB_7D="| (no data) | - |"

# Feedback count (30 days)
FEEDBACK_30D_JSON=$(prom_query "sum(increase(couponhubbot_feedback_total[30d]))")
FEEDBACK_30D=$(echo "$FEEDBACK_30D_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "0")

# Total interactions (7 days vs previous 7 days for trend)
INTERACTIONS_7D_JSON=$(prom_query "sum(increase(couponhubbot_command_total[7d])) + sum(increase(couponhubbot_callback_total[7d]))")
INTERACTIONS_7D=$(echo "$INTERACTIONS_7D_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "0")

INTERACTIONS_PREV_JSON=$(prom_query "sum(increase(couponhubbot_command_total[14d])) - sum(increase(couponhubbot_command_total[7d])) + sum(increase(couponhubbot_callback_total[14d])) - sum(increase(couponhubbot_callback_total[7d]))")
INTERACTIONS_PREV=$(echo "$INTERACTIONS_PREV_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "0")

# ─── Chat message themes (PostgreSQL) ────────────────────────────────────────

log "Querying chat messages..."

# Message volume by day (last 7 days)
MSG_DAILY=$(db_query "
    SELECT to_char(created_at, 'YYYY-MM-DD') AS day,
           COUNT(*) AS total,
           COUNT(*) FILTER (WHERE text IS NOT NULL) AS with_text,
           COUNT(DISTINCT user_id) AS unique_users
    FROM chat_message
    WHERE created_at >= NOW() - INTERVAL '7 days'
    GROUP BY day
    ORDER BY day DESC;
")

MSG_DAILY_TABLE=""
if [ -n "$MSG_DAILY" ]; then
    while IFS=$'\t' read -r day total with_text users; do
        MSG_DAILY_TABLE="${MSG_DAILY_TABLE}| ${day} | ${total} | ${with_text} | ${users} |
"
    done <<< "$MSG_DAILY"
else
    MSG_DAILY_TABLE="| (no messages) | - | - | - |
"
fi

# Total messages in last 7 days
MSG_TOTAL_7D=$(db_query "SELECT COUNT(*) FROM chat_message WHERE created_at >= NOW() - INTERVAL '7 days';")
[ -z "$MSG_TOTAL_7D" ] && MSG_TOTAL_7D="0"

# Unique active chatters in last 7 days
CHATTERS_7D=$(db_query "SELECT COUNT(DISTINCT user_id) FROM chat_message WHERE created_at >= NOW() - INTERVAL '7 days';")
[ -z "$CHATTERS_7D" ] && CHATTERS_7D="0"

# ─── User feedback (PostgreSQL) ──────────────────────────────────────────────

log "Querying user feedback..."

# Recent feedback entries (last 30 days, anonymized — no user IDs)
FEEDBACK_ENTRIES=$(db_query "
    SELECT uf.id,
           CASE WHEN uf.feedback_text IS NOT NULL
                THEN REPLACE(REPLACE(REPLACE(LEFT(uf.feedback_text, 200), E'\n', ' '), E'\t', ' '), '|', '/')
                ELSE '(media only)'
           END AS preview,
           uf.has_media,
           uf.github_issue_number,
           to_char(uf.created_at, 'YYYY-MM-DD HH24:MI') AS created
    FROM user_feedback uf
    WHERE uf.created_at >= NOW() - INTERVAL '30 days'
    ORDER BY uf.created_at DESC
    LIMIT 20;
")

FEEDBACK_TABLE=""
if [ -n "$FEEDBACK_ENTRIES" ]; then
    while IFS=$'\t' read -r id preview has_media issue_num created; do
        issue_ref=""
        if [ "$issue_num" != "" ] && [ "$issue_num" != "\\N" ]; then
            issue_ref="#${issue_num}"
        else
            issue_ref="—"
        fi
        media_flag=""
        if [ "$has_media" = "t" ]; then
            media_flag=" 📎"
        fi
        FEEDBACK_TABLE="${FEEDBACK_TABLE}| ${id} | ${preview}${media_flag} | ${issue_ref} | ${created} |
"
    done <<< "$FEEDBACK_ENTRIES"
else
    FEEDBACK_TABLE="| (no feedback) | - | - | - |
"
fi

# ─── Error context (Loki, optional) ──────────────────────────────────────────

if [ -n "${LOKI_URL:-}" ]; then
    log "Querying Loki for user-facing errors..."

    ERROR_COUNT_JSON=$(curl -sf -G \
        --connect-timeout 5 --max-time 20 \
        "${LOKI_URL}/loki/api/v1/query" \
        --data-urlencode "query=sum(count_over_time({container=\"${CONTAINER}\"} | json | level=~\"Error|Fatal\"[7d]))" \
        2>/dev/null || echo '{"data":{"result":[]}}')
    ERROR_COUNT_7D=$(echo "$ERROR_COUNT_JSON" | jq -r '[.data.result[].value[1] | tonumber | floor] | add // 0' 2>/dev/null || echo "0")
else
    ERROR_COUNT_7D="N/A (Loki not configured)"
fi

# ─── GitHub issues context ────────────────────────────────────────────────────

log "Querying GitHub issues..."

OPEN_FEEDBACK=$(gh issue list --repo "$REPO" --label "user-feedback" --state open -L 1000 --json number,title,createdAt \
    --jq 'length' 2>/dev/null || echo "0")

OPEN_FEATURES=$(gh issue list --repo "$REPO" --label "feature-request" --state open -L 1000 --json number,title \
    --jq 'length' 2>/dev/null || echo "0")

OPEN_BUGS=$(gh issue list --repo "$REPO" --label "bug" --state open -L 1000 --json number,title \
    --jq 'length' 2>/dev/null || echo "0")

# ─── Output markdown report ──────────────────────────────────────────────────

log "Generating product data report..."

cat <<EOF
## Bot Usage (7-day, Prometheus)

### Engagement Summary

| Metric | Value |
|--------|-------|
| Total Interactions (7d) | ${INTERACTIONS_7D} |
| Previous Period (7d) | ${INTERACTIONS_PREV} |
| Feedback Submissions (30d) | ${FEEDBACK_30D} |
| Errors (7d) | ${ERROR_COUNT_7D} |

### Command Usage (7 days)

| Command | Count |
|---------|-------|
${CMD_7D}

### Callback Actions (7 days)

| Action | Count |
|--------|-------|
${CB_7D}

## Community Chat Activity (7-day)

### Summary

| Metric | Value |
|--------|-------|
| Total Messages | ${MSG_TOTAL_7D} |
| Unique Chatters | ${CHATTERS_7D} |

### Daily Breakdown

| Date | Total | With Text | Unique Users |
|------|-------|-----------|--------------|
${MSG_DAILY_TABLE}

## User Feedback (last 30 days)

| ID | Preview | GitHub Issue | Date |
|----|---------|-------------|------|
${FEEDBACK_TABLE}

## Open Issues

| Category | Count |
|----------|-------|
| Unprocessed Feedback | ${OPEN_FEEDBACK} |
| Feature Requests | ${OPEN_FEATURES} |
| Bugs | ${OPEN_BUGS} |
EOF

log "Product data report generated successfully."
