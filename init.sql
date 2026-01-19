CREATE ROLE coupon_hub_bot_service WITH LOGIN PASSWORD 'coupon_hub_bot_service';
GRANT coupon_hub_bot_service TO postgres;
CREATE DATABASE coupon_hub_bot OWNER coupon_hub_bot_service ENCODING 'UTF8';
GRANT ALL ON DATABASE coupon_hub_bot TO coupon_hub_bot_service;
GRANT USAGE, CREATE ON SCHEMA public TO coupon_hub_bot_service;

