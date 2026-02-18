-- Remove app/physical coupon type distinction (no longer needed).

ALTER TABLE coupon DROP COLUMN IF EXISTS is_app_coupon;
ALTER TABLE pending_add DROP COLUMN IF EXISTS is_app_coupon;
