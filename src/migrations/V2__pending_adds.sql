-- Pending adds for OCR-assisted /add flow
CREATE TABLE IF NOT EXISTS pending_add
(
    id            UUID PRIMARY KEY,
    owner_id      BIGINT      NOT NULL REFERENCES "user" (id) ON DELETE CASCADE,
    photo_file_id TEXT        NOT NULL,
    value         NUMERIC(10, 2) NOT NULL,
    expires_at    DATE        NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::TEXT, NOW())
);

CREATE INDEX IF NOT EXISTS pending_add_owner_idx ON pending_add (owner_id);

