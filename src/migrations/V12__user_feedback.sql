CREATE TABLE user_feedback (
    id BIGSERIAL PRIMARY KEY,
    user_id BIGINT NOT NULL REFERENCES "user"(id),
    feedback_text TEXT,
    has_media BOOLEAN NOT NULL DEFAULT FALSE,
    telegram_message_id INT,
    github_issue_number INT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_user_feedback_user_id ON user_feedback(user_id);
CREATE INDEX idx_user_feedback_created_at ON user_feedback(created_at);

GRANT SELECT, INSERT, UPDATE ON user_feedback TO coupon_hub_bot_service;
GRANT USAGE, SELECT ON SEQUENCE user_feedback_id_seq TO coupon_hub_bot_service;
