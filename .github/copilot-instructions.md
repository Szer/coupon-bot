# Coupon Hub Bot

Telegram bot for collaborative coupon management in a private community. Written in **F# / .NET 10**.

## Tech Stack

- **F# / .NET 10**, ASP.NET Core (webhook), PostgreSQL, Dapper
- **Telegram.Bot 22.8.1** — bot framework
- Flyway — database migrations
- Docker — containerization, Testcontainers for E2E tests
- OpenTelemetry — traces and metrics, Serilog — structured logging

## Documentation Map

| Topic | File |
|-------|------|
| Architecture | [docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md) |
| Bot Logic | [docs/TELEGRAM-BOT-LOGIC.md](../docs/TELEGRAM-BOT-LOGIC.md) |
| Testing | [docs/TESTING.md](../docs/TESTING.md) |
| Database | [docs/DATABASE.md](../docs/DATABASE.md) |
| OCR | [docs/OCR.md](../docs/OCR.md) |
| Deployment | [docs/DEPLOYMENT.md](../docs/DEPLOYMENT.md) |
| Observability | [docs/OBSERVABILITY.md](../docs/OBSERVABILITY.md) |

## Key Conventions

- UI text is in **Russian** (кириллица). Never compare raw JSON for Cyrillic — always parse with `JsonDocument`.
- F# idioms: `task { }` CE for async, records with `[<CLIMutable>]` for Dapper, `option` types.
- Database role: `coupon_hub_bot_service`. Always add `GRANT` in migrations for new tables.
- F# compilation order matters — new `.fs` files must be added to `.fsproj` in the correct position.
- `TreatWarningsAsErrors` is enabled — all warnings are errors.
