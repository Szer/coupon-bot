-- Pending feedback flow: user issued /feedback and next message should be forwarded to admins.
CREATE TABLE IF NOT EXISTS pending_feedback
(
    user_id    BIGINT PRIMARY KEY REFERENCES "user" (id) ON DELETE CASCADE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::TEXT, NOW())
);

CREATE INDEX IF NOT EXISTS pending_feedback_created_idx ON pending_feedback (created_at);

GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.pending_feedback TO coupon_hub_bot_service;
