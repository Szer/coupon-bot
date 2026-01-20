# Coupon Hub Bot - Local Development Setup

## Quick Start

### 1. Start Infrastructure (PostgreSQL + FakeTgApi)

```bash
# Start Postgres and run migrations (flyway will run automatically and exit)
docker-compose -f docker-compose.dev.yml up -d postgres flyway

# Wait a moment for flyway to complete, then start FakeTgApi (optional)
docker-compose -f docker-compose.dev.yml up -d fake-tg-api
```

**Note:** Flyway runs once and exits. If you need to re-run migrations:
```bash
# Force recreate and run flyway again
docker-compose -f docker-compose.dev.yml run --rm flyway
```

### 2. Configure Environment Variables

Copy `env.example` to `.env` (or configure in Rider Run Configuration):

```bash
cp env.example .env
```

Update `.env` with your values:
- `DATABASE_URL` - should point to `localhost:5439` (Postgres from docker-compose)
- `TELEGRAM_API_URL` - set to `http://localhost:8080` if using FakeTgApi, or leave empty for real Telegram API
- `BOT_TELEGRAM_TOKEN` - your real bot token (or `123:456` for testing with FakeTgApi)
- `BOT_AUTH_TOKEN` - secret token for webhook authentication
- `FEEDBACK_ADMINS` - список Telegram userId админов для `/feedback` (например `123,456`)

### 3. Run Bot in Rider

1. Open Run Configuration
2. Set Environment Variables (or use `.env` file if you have a plugin)
3. Set `ASPNETCORE_URLS=http://localhost:5000`
4. Run with debugger attached

### 4. Test with HTTP Client

Open `coupon-hub-bot.http` in Rider and send requests:
- `GET /health` - health check
- `POST /bot` - send Telegram update (webhook)
- `POST /test/run-reminder` - test-only endpoint (requires `TEST_MODE=true`)
  - можно передать `nowUtc=2026-01-19T08:00:00Z` как query parameter

### 5. View Logs

The bot will log:
- `HTTP REQUEST: {Method} {Path}` - all incoming requests
- `HTTP OUT {Method} {Url}` - outgoing requests to Telegram API
- `HTTP IN {StatusCode} ...` - responses from Telegram API

FakeTgApi logs:
- `FAKE TG IN {method} {path}` - incoming Telegram API calls
- Check `/test/calls` endpoint to see all logged API calls

## Troubleshooting

### Database Connection Issues

```bash
# Check if Postgres is running
docker ps | grep coupon-hub-postgres-dev

# Check Postgres logs
docker logs coupon-hub-postgres-dev

# Connect to Postgres manually (from host, use port 5439)
psql -h localhost -p 5439 -U coupon_hub_bot_service -d coupon_hub_bot

# Or from inside container (port 5432)
docker exec -it coupon-hub-postgres-dev psql -U coupon_hub_bot_service -d coupon_hub_bot
```

### FakeTgApi Not Responding

```bash
# Check if FakeTgApi is running
docker ps | grep coupon-hub-fake-tg-api-dev

# Check FakeTgApi logs
docker logs coupon-hub-fake-tg-api-dev

# Test FakeTgApi health endpoint
curl http://localhost:8080/health
```

### Reset Database

```bash
# Stop and remove containers
docker-compose -f docker-compose.dev.yml down -v

# Start fresh
docker-compose -f docker-compose.dev.yml up -d postgres
docker-compose -f docker-compose.dev.yml --profile migrate run --rm flyway
```

## Telemetry (OpenTelemetry)

- Трейсы включены через OTel tracing (HTTP + ASP.NET Core + Npgsql).
- Метрики включены через `System.Diagnostics.Metrics` и экспортируются через OTel metrics pipeline.

Полезные переменные окружения:
- `OTEL_EXPORTER_OTLP_ENDPOINT` — куда слать OTel (gRPC)
- `OTEL_EXPORTER_CONSOLE=true` — включить консольный экспорт (удобно локально)
- `OTEL_SERVICE_NAME` — имя сервиса (по умолчанию `coupon-hub-bot`)
