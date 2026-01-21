-- Reset all data (pre-production) and add dedupe constraints/indexes.
-- We deliberately truncate everything to simplify migration and ensure consistent state.

TRUNCATE TABLE
    public.coupon_event,
    public.coupon,
    public.pending_add,
    public.pending_feedback,
    public."user"
RESTART IDENTITY CASCADE;

-- Enforce photo_file_id uniqueness: Telegram reuses file ids, so re-uploading the same photo is a strong signal of duplicate.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'coupon_photo_file_id_uniq'
    ) THEN
        ALTER TABLE public.coupon
            ADD CONSTRAINT coupon_photo_file_id_uniq UNIQUE (photo_file_id);
    END IF;
END $$;

-- Speed up "duplicate barcode among non-expired coupons" checks.
CREATE INDEX IF NOT EXISTS coupon_barcode_expires_idx
    ON public.coupon (barcode_text, expires_at)
    WHERE barcode_text IS NOT NULL;
