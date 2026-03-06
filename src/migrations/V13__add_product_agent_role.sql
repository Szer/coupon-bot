-- Read-only role for product agent data gathering script.
-- The product workflow queries chat_message and user_feedback
-- tables to build a product data report.

CREATE ROLE product_agent WITH LOGIN PASSWORD 'product_agent';

GRANT CONNECT ON DATABASE coupon_hub_bot TO product_agent;
GRANT USAGE ON SCHEMA public TO product_agent;

GRANT SELECT ON ALL TABLES IN SCHEMA public TO product_agent;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO product_agent;

GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO product_agent;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON SEQUENCES TO product_agent;
