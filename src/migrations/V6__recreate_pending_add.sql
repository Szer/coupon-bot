-- Recreate pending_add for /add wizard workflow.
-- Old pending_add was used for OCR confirm flow; we no longer rely on it.
DROP TABLE IF EXISTS pending_add CASCADE;

CREATE TABLE pending_add
(
    user_id      BIGINT PRIMARY KEY REFERENCES "user" (id) ON DELETE CASCADE,
    stage        TEXT        NOT NULL,
    photo_file_id TEXT       NULL,
    value        NUMERIC(10, 2) NULL,
    min_check    NUMERIC(10, 2) NULL,
    expires_at   DATE        NULL,
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::TEXT, NOW())
);

CREATE INDEX IF NOT EXISTS pending_add_updated_idx ON pending_add (updated_at);

GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.pending_add TO coupon_hub_bot_service;
