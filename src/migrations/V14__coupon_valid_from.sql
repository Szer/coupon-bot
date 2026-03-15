-- Add valid_from column to coupon table for future-dated coupon filtering.
ALTER TABLE coupon ADD COLUMN valid_from DATE NULL;

-- Add valid_from column to pending_add wizard table.
ALTER TABLE pending_add ADD COLUMN valid_from DATE NULL;
