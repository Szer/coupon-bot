CREATE ROLE admin WITH LOGIN PASSWORD 'admin'; -- no need for prod DB
CREATE ROLE coupon_hub_bot_service WITH LOGIN PASSWORD 'coupon_hub_bot_service'; -- change password for prod
CREATE ROLE product_agent WITH LOGIN PASSWORD 'product_agent'; -- change password for prod
GRANT coupon_hub_bot_service TO postgres; -- no need for prod DB
GRANT product_agent TO postgres; -- no need for prod DB
CREATE DATABASE coupon_hub_bot OWNER admin ENCODING 'UTF8';
GRANT CONNECT ON DATABASE coupon_hub_bot TO coupon_hub_bot_service;
GRANT CONNECT ON DATABASE coupon_hub_bot TO product_agent;

-- product_agent: read-only access for product data gathering script.
-- In prod, run these GRANTs manually after Flyway migrations.
-- ALTER DEFAULT PRIVILEGES must be run AS the role that creates tables (admin)
-- so that future Flyway-created tables automatically get SELECT granted.
\connect coupon_hub_bot
GRANT USAGE ON SCHEMA public TO product_agent;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO product_agent;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO product_agent;
SET ROLE admin;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO product_agent;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON SEQUENCES TO product_agent;
RESET ROLE;
