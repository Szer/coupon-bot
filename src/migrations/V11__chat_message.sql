-- Store messages from the community group chat for product analysis.
-- Retention: 1 year (cleanup by application).

CREATE TABLE chat_message (
    id BIGSERIAL PRIMARY KEY,
    chat_id BIGINT NOT NULL,
    message_id INT NOT NULL,
    user_id BIGINT NOT NULL,
    text TEXT,
    has_photo BOOLEAN NOT NULL DEFAULT FALSE,
    has_document BOOLEAN NOT NULL DEFAULT FALSE,
    reply_to_message_id INT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(chat_id, message_id)
);

CREATE INDEX idx_chat_message_created_at ON chat_message(created_at);
CREATE INDEX idx_chat_message_user_id ON chat_message(user_id);
CREATE INDEX idx_chat_message_chat_id_created_at ON chat_message(chat_id, created_at);

GRANT SELECT, INSERT, DELETE ON chat_message TO coupon_hub_bot_service;
GRANT USAGE, SELECT ON SEQUENCE chat_message_id_seq TO coupon_hub_bot_service;
