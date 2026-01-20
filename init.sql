CREATE ROLE admin WITH LOGIN PASSWORD 'admin'; -- no need for prod DB
CREATE ROLE coupon_hub_bot_service WITH LOGIN PASSWORD 'coupon_hub_bot_service'; -- change password for prod
GRANT coupon_hub_bot_service TO postgres; -- no need for prod DB
CREATE DATABASE coupon_hub_bot OWNER admin ENCODING 'UTF8';
GRANT CONNECT ON DATABASE coupon_hub_bot TO coupon_hub_bot_service;
