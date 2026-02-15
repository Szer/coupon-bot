# Architecture

## System Overview

Coupon Hub Bot is a Telegram bot running as an ASP.NET Core webhook server. Users interact via private messages; notifications go to a community group chat.

## Key Modules

```
src/CouponHubBot/
├── Program.fs            # Entry point, DI, webhook setup
├── Types.fs              # Domain types (Coupon, User, PendingAddFlow, etc.)
├── Utils.fs              # Shared utilities (date parsing, pluralization)
├── Telemetry.fs          # OpenTelemetry configuration
└── Services/
    ├── DbService.fs      # PostgreSQL access via Dapper
    ├── TelegramService.fs # Telegram API wrapper
    ├── CouponService.fs  # Coupon CRUD operations
    ├── AddFlowService.fs # /add wizard state machine
    ├── ReminderService.fs # Scheduled reminders (expiring coupons, weekly stats)
    ├── AzureOcrService.fs # Azure Computer Vision OCR client
    └── CouponOcrEngine.fs # Barcode + text OCR processing
```

## Data Flow

1. Telegram sends webhook POST to `/bot`
2. Bot authenticates via `X-Telegram-Bot-Api-Secret-Token` header
3. Update is routed to the appropriate handler (command, callback, message)
4. Handler interacts with PostgreSQL via `DbService` and replies via Telegram API
5. Notifications (coupon added/taken/returned) are sent to the community group

## Infrastructure

- **Database**: PostgreSQL 15.6 with Flyway migrations
- **Container**: Docker image pushed to GHCR (`ghcr.io/szer/coupon-bot`)
- **Orchestration**: ArgoCD with image-reloader (polls GHCR every ~5 min)
- **Observability**: Serilog → Loki, OpenTelemetry → Prometheus
