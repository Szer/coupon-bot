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
