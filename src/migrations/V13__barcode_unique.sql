-- Add uniqueness constraint on barcode_text for active coupons to prevent race conditions
-- in concurrent add flows. The SELECT-then-INSERT pattern in TryAddCoupon has a TOCTOU
-- window under ReadCommitted isolation; this constraint makes the database the authoritative guard.
CREATE UNIQUE INDEX IF NOT EXISTS coupon_barcode_active_uniq
    ON public.coupon (barcode_text, expires_at)
    WHERE barcode_text IS NOT NULL AND status IN ('available', 'taken');
