-- Add is_app_coupon flag to coupon and pending_add tables.
-- Add voided as valid status (comment-level; no CHECK constraint exists).
-- Status values: available | taken | used | voided

ALTER TABLE coupon
    ADD COLUMN IF NOT EXISTS is_app_coupon BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE pending_add
    ADD COLUMN IF NOT EXISTS is_app_coupon BOOLEAN NOT NULL DEFAULT FALSE;

GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.coupon TO coupon_hub_bot_service;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.pending_add TO coupon_hub_bot_service;
