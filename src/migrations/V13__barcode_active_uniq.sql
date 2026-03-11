-- Enforce uniqueness of barcode_text among active coupons to close the race condition
-- in TryAddCoupon where two concurrent transactions could both pass the SELECT duplicate
-- check and both succeed in INSERT.
-- No GRANT needed: indexes inherit access from the parent table (coupon), which already
-- has the required permissions granted to coupon_hub_bot_service.
CREATE UNIQUE INDEX IF NOT EXISTS coupon_barcode_active_uniq
    ON public.coupon (barcode_text, expires_at)
    WHERE barcode_text IS NOT NULL
      AND status IN ('available', 'taken');
