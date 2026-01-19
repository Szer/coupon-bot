-- Users (telegram users interacting with bot)
CREATE TABLE IF NOT EXISTS "user"
(
    id         BIGINT PRIMARY KEY,
    username   TEXT NULL,
    first_name TEXT NULL,
    last_name  TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::TEXT, NOW()),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::TEXT, NOW())
);

CREATE INDEX IF NOT EXISTS user_username_idx ON "user" (username);

-- Coupons
CREATE TABLE IF NOT EXISTS coupon
(
    id            SERIAL PRIMARY KEY,
    owner_id      BIGINT      NOT NULL REFERENCES "user" (id) ON DELETE CASCADE,
    photo_file_id TEXT        NOT NULL,
    value         NUMERIC(10, 2) NOT NULL,
    expires_at    DATE        NOT NULL,
    barcode_text  TEXT        NULL,
    status        TEXT        NOT NULL DEFAULT 'available', -- available | taken | used
    taken_by      BIGINT      NULL REFERENCES "user" (id),
    taken_at      TIMESTAMPTZ NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::TEXT, NOW())
);

CREATE INDEX IF NOT EXISTS coupon_status_expires_idx ON coupon (status, expires_at);
CREATE INDEX IF NOT EXISTS coupon_owner_idx ON coupon (owner_id);
CREATE INDEX IF NOT EXISTS coupon_taken_by_idx ON coupon (taken_by);

-- Event log (for group notifications, stats, audits)
CREATE TABLE IF NOT EXISTS coupon_event
(
    id         SERIAL PRIMARY KEY,
    coupon_id  INT        NOT NULL REFERENCES coupon (id) ON DELETE CASCADE,
    user_id    BIGINT     NOT NULL REFERENCES "user" (id) ON DELETE CASCADE,
    event_type TEXT       NOT NULL, -- added | taken | used | returned | expired
    created_at TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::TEXT, NOW())
);

CREATE INDEX IF NOT EXISTS coupon_event_coupon_idx ON coupon_event (coupon_id);
CREATE INDEX IF NOT EXISTS coupon_event_user_idx ON coupon_event (user_id);
CREATE INDEX IF NOT EXISTS coupon_event_type_idx ON coupon_event (event_type);

