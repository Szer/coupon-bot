-- Add optional barcode_text to pending_add for OCR-assisted /add wizard.
ALTER TABLE pending_add
    ADD COLUMN IF NOT EXISTS barcode_text TEXT NULL;

-- No new tables, but keep grants consistent.
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.pending_add TO coupon_hub_bot_service;

