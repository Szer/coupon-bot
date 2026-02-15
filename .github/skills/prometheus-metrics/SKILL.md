---
name: prometheus-metrics
description: Query Prometheus metrics via HTTP API using PromQL. Use when checking pod restart counts, HTTP error rates, resource usage, or verifying deployment health after a release.
---

# Prometheus Metrics Queries

## Prerequisites

- WireGuard VPN must be connected (established by `copilot-setup-steps.yml`)
- API base: `http://prometheus.internal:9090`
- No auth required (internal network)

## Verify Connectivity

Always run this first. If it fails, VPN is likely down.

```bash
curl -sf http://prometheus.internal:9090/-/healthy && echo "Prometheus OK" || echo "ERROR: cannot reach Prometheus (VPN down?)"
```

## API Endpoints

### Instant query

```bash
curl -s -G 'http://prometheus.internal:9090/api/v1/query' \
  --data-urlencode 'query=up{job="coupon-bot"}' \
  | jq '.data.result[]'
```

### Range query

```bash
curl -s -G 'http://prometheus.internal:9090/api/v1/query_range' \
  --data-urlencode 'query=rate(http_server_request_duration_seconds_count{job="coupon-bot"}[5m])' \
  --data-urlencode "start=$(date -u -d '30 minutes ago' +%Y-%m-%dT%H:%M:%SZ)" \
  --data-urlencode "end=$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --data-urlencode 'step=60s' \
  | jq '.data.result[]'
```

## Key Health Queries

| Metric | PromQL |
|--------|--------|
| Pod restarts | `kube_pod_container_status_restarts_total{container="coupon-bot"}` |
| Pod ready | `kube_pod_status_ready{pod=~"coupon-bot.*"}` |
| Process up | `up{job="coupon-bot"}` |
| 5xx error rate | `sum(rate(http_server_request_duration_seconds_count{http_response_status_code=~"5..",job="coupon-bot"}[5m]))` |
| Request rate | `sum(rate(http_server_request_duration_seconds_count{job="coupon-bot"}[5m]))` |
| 5xx by route | `sum by (http_route) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"5..",job="coupon-bot"}[5m]))` |
| Waiting reason | `kube_pod_container_status_waiting_reason{container="coupon-bot"}` |
| Memory usage | `container_memory_working_set_bytes{container="coupon-bot"}` |
| CPU throttling | `container_cpu_cfs_throttled_periods_total{container="coupon-bot"}` |

The `waiting reason` metric is especially useful for detecting CrashLoopBackOff, ImagePullBackOff, and OOMKilled states.

## Post-Deployment Verification

```bash
# 1. Check restart count (should be 0 or very low)
curl -s -G 'http://prometheus.internal:9090/api/v1/query' \
  --data-urlencode 'query=kube_pod_container_status_restarts_total{container="coupon-bot"}' \
  | jq '.data.result[].value[1]'

# 2. Check 5xx error rate (should be 0)
curl -s -G 'http://prometheus.internal:9090/api/v1/query' \
  --data-urlencode 'query=sum(rate(http_server_request_duration_seconds_count{http_response_status_code=~"5..",job="coupon-bot"}[5m]))' \
  | jq '.data.result[].value[1]'

# 3. Check pod is ready (should be 1)
curl -s -G 'http://prometheus.internal:9090/api/v1/query' \
  --data-urlencode 'query=kube_pod_status_ready{pod=~"coupon-bot.*"}' \
  | jq '.data.result[].value[1]'

# 4. Check for CrashLoopBackOff or other waiting reasons (should be empty)
curl -s -G 'http://prometheus.internal:9090/api/v1/query' \
  --data-urlencode 'query=kube_pod_container_status_waiting_reason{container="coupon-bot"}' \
  | jq '.data.result[] | {reason: .metric.reason, value: .value[1]}'

# 5. Check memory usage (compare against limits to detect OOM risk)
curl -s -G 'http://prometheus.internal:9090/api/v1/query' \
  --data-urlencode 'query=container_memory_working_set_bytes{container="coupon-bot"}' \
  | jq '.data.result[].value[1]'
```

## Response Format

- Instant query: `data.result[].value` is `[unix_timestamp, "value_string"]`
- Range query: `data.result[].values` is array of `[unix_timestamp, "value_string"]`
- Always check `.status == "success"` first
- Empty `data.result` array means no matching time series (metric may not exist yet)
- Restart count is cumulative; a single restart after fresh deployment may be acceptable
