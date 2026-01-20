GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.coupon TO coupon_hub_bot_service;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.coupon_event TO coupon_hub_bot_service;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.pending_add TO coupon_hub_bot_service;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."user" TO coupon_hub_bot_service;

GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO coupon_hub_bot_service;
