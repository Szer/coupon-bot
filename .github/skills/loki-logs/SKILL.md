---
name: loki-logs
description: Query application logs via Grafana Loki API using LogQL. Use when debugging runtime errors, investigating deployment failures, checking application behavior, or searching for specific log patterns.
---

# Loki Log Queries

## Prerequisites

- WireGuard VPN must be connected (established by `copilot-setup-steps.yml`)
- API base: `http://loki.internal/loki/api/v1/`
- No auth required (Loki has `auth_enabled: false` on the internal network)

## Verify Connectivity

Always run this first. If it fails, VPN is likely down.

```bash
curl -sf http://loki.internal/ready && echo "Loki OK" || echo "ERROR: cannot reach Loki (VPN down?)"
```

## API Endpoints

### Range query (logs over time period)

```bash
START=$(date -u -d '10 minutes ago' +%Y-%m-%dT%H:%M:%SZ)
curl -s -G http://loki.internal/loki/api/v1/query_range \
  --data-urlencode 'query={container="coupon-bot"}' \
  --data-urlencode "start=$START" \
  --data-urlencode 'limit=100' \
  | jq '.data.result[].values[] | .[1]'
```

### Instant query (latest matching logs)

```bash
curl -s -G http://loki.internal/loki/api/v1/query \
  --data-urlencode 'query={container="coupon-bot"} | json | level="Error"' \
  --data-urlencode 'limit=50' \
  | jq '.data.result[].values[] | .[1]'
```

## Common LogQL Patterns

| Purpose | LogQL |
|---------|-------|
| All logs from bot | `{container="coupon-bot"}` |
| Errors and fatals | `{container="coupon-bot"} \| json \| level=~"Error\|Fatal"` |
| By specific level | `{container="coupon-bot"} \| json \| level="Error"` |
| Crash/unhandled | `{container="coupon-bot"} \|~ "Fatal\|Unhandled\|crash"` |
| Text search | `{container="coupon-bot"} \|= "search text here"` |
| Exclude noise | `{container="coupon-bot"} != "health" \| json` |

## Post-Deployment Error Check

After deploying, check for errors in the last few minutes:

```bash
START=$(date -u -d '5 minutes ago' +%Y-%m-%dT%H:%M:%SZ)
curl -s -G http://loki.internal/loki/api/v1/query_range \
  --data-urlencode 'query={container="coupon-bot"} | json | level=~"Error|Fatal"' \
  --data-urlencode "start=$START" \
  --data-urlencode 'limit=100' \
  | jq '.data.result[].values[] | .[1]'
```

If this returns empty, the deployment has no error-level logs.

## Log Volume Check

Useful for detecting log storms or unusual activity:

```bash
curl -s -G http://loki.internal/loki/api/v1/query \
  --data-urlencode 'query=count_over_time({container="coupon-bot"}[5m])' \
  | jq '.data.result[].value[1]'
```

## Response Format

Loki returns results as `data.result[].values[]` where each value is a tuple `[timestamp_nanoseconds, log_line_string]`.

- Extract just log lines: `jq '.data.result[].values[] | .[1]'`
- Count results: `jq '[.data.result[].values[]] | length'`
- Check if empty: `jq '.data.result | length'` (0 means no matching streams)
