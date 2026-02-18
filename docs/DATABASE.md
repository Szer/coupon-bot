# Database

## Engine

PostgreSQL 15.6. Migrations managed by Flyway.

## Schema Notes

### coupon table

Key columns:
- `status TEXT NOT NULL DEFAULT 'available'` — valid values: `available`, `taken`, `used`, `voided`
- `is_app_coupon BOOLEAN NOT NULL DEFAULT FALSE` — true for app-sourced coupons (detected via OCR markers)
- `taken_by BIGINT NULL` — user who took the coupon (NULL when available/voided)
- `barcode_text TEXT NULL` — barcode decoded from coupon photo

### pending_add table

Wizard state for `/add` flow:
- `is_app_coupon BOOLEAN NOT NULL DEFAULT FALSE` — auto-detected from OCR, toggleable by user

### coupon_event table

Event types: `added`, `taken`, `returned`, `used`, `voided`

## Migrations

Migration files live in `src/migrations/` (V1 through V8+). Flyway runs them:
- In tests: via Testcontainers Flyway container
- In CI/CD: via Docker against production DB over WireGuard VPN

## Access Control

Application connects as role `coupon_hub_bot_service`. When adding a **new table** or changing access, always include `GRANT` statements for this role in the migration.

Example:
```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON new_table TO coupon_hub_bot_service;
GRANT USAGE, SELECT ON SEQUENCE new_table_id_seq TO coupon_hub_bot_service;
```

## Init Script

`init.sql` at repo root creates the database, roles, and grants initial permissions. It runs once during test container setup via `dbContainer.ExecScriptAsync(initSql)`.
