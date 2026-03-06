# Observability

## Logging

- **Serilog** with structured JSON output
- Logs shipped to **Loki** via Grafana stack
- Query logs: `http://loki.internal/loki/api/v1/query_range` (requires VPN, no auth)
- Common LogQL: `{container="coupon-bot"} | json | level="Error"`

## Metrics

- **OpenTelemetry** for traces and metrics
- Metrics exported to **Prometheus** at `http://prometheus.internal:9090` (requires VPN)
- Key metrics: HTTP request duration, Npgsql query duration
- Health queries: see Cursor skill `prometheus-metrics`

### Custom Bot Metrics

All custom metrics are defined in `src/CouponHubBot/Telemetry.fs` under the `CouponHubBot.Metrics` meter.

| Metric | Type | Tags | Description |
|--------|------|------|-------------|
| `couponhubbot_command_total` | Counter | `command` | Counts every command invocation (e.g. `list`, `add`, `feedback`) |
| `couponhubbot_callback_total` | Counter | `action` | Counts every callback button press (e.g. `take`, `return`, `used`, `void`, `addflow`, `myAdded`) |
| `couponhubbot_feedback_total` | Counter | — | Counts user feedback submissions via the `/feedback` flow |
| `couponhubbot_button_click_total` | Counter | `button` | Legacy: counts UI/button interactions; `button` tag is a fixed value for some actions (e.g. `take`) and raw callback data for `addflow:` callbacks |

### Useful PromQL Queries

- Top commands (24h): `sort_desc(sum by (command)(increase(couponhubbot_command_total[24h])))`
- Feedback rate: `sum(rate(couponhubbot_feedback_total[1h]))`
- Callback distribution: `sum by (action)(increase(couponhubbot_callback_total[24h]))`

## Tracing

- OpenTelemetry traces configured in `Telemetry.fs`
- Includes Npgsql instrumentation for database query tracing

## Accessing Over VPN

All observability endpoints are behind WireGuard VPN:
- Loki: `http://loki.internal` (no auth, direct access)
- Grafana: `http://grafana.internal` (requires auth, dashboards only)
- Prometheus: `http://prometheus.internal:9090`
- ArgoCD: `http://argo.internal`

Locally: ensure WireGuard is connected.
In CI: VPN is set up via `secrets.WIREGUARD_CONFIG` in GitHub Actions.
