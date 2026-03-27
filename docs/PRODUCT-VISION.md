# Product Vision

> **This document is the single source of truth for product direction.**
> The Product agent uses it to triage feedback and evaluate feature requests.
> Only the repository owner may edit this file.

## What Is Coupon Hub Bot?

A Telegram bot for a **private community** to collaboratively manage discount coupons. Members share coupons they don't need and take coupons others have shared. The bot tracks ownership, expiration, and usage.

## Target Users

Members of a single private Telegram group (~10–30 people) who trust each other. They are non-technical, Russian-speaking users who expect a simple, fast bot with minimal friction.

## Core Value Proposition

1. **Zero-friction coupon exchange** — add a coupon in seconds, take one in a tap
2. **Accountability** — every coupon has a clear owner and status history
   > Note: Accountability covers *internal* state changes only (share, take, void, return). The bot cannot detect or track external redemptions at third-party checkouts (e.g., Dunnes app/website). Owners are responsible for voiding coupons they have redeemed externally; if they don't, that is a community trust issue, not a bot bug.
3. **Expiration awareness** — automated reminders prevent coupons from expiring unused

## In-Scope Features

- Coupon lifecycle: add → share → take → use/return/void
- OCR for coupon photo recognition
- Expiration reminders and notifications
- Community membership verification
- Feedback collection from users
- Admin tools for moderation

## Out of Scope (Do NOT build)

- Multi-community / multi-tenant support
- Public-facing web UI or API
- Payment processing or monetization
- Integration with external coupon aggregators
- Gamification (points, leaderboards, badges)
- AI-powered coupon recommendations
- Support for other stores (e.g., SuperValu) — Dunnes only
- Tracking external coupon redemption (e.g., Dunnes app/website checkout) — the bot has no integration with external systems and cannot detect third-party usage; voiding after external redemption is the owner's responsibility

## Quality Attributes

| Attribute | Priority | Rationale |
|-----------|----------|-----------|
| Simplicity | Highest | Non-technical users; every extra button is friction |
| Reliability | High | Bot must respond within seconds, never lose data |
| Speed | High | Telegram interactions should feel instant |
| Maintainability | Medium | Single developer; code must stay simple and well-tested |
| Extensibility | Low | Small user base; YAGNI over architecture astronautics |

## Anti-Patterns to Reject

- **Feature creep**: If a feature doesn't directly help with coupon exchange, it probably doesn't belong
- **Over-engineering**: Prefer simple solutions over architecturally elegant ones
- **Power-user features**: If it requires explanation, it's too complex for this audience
- **Premature scaling**: This serves one community; design for that reality

## Triage Guidelines for the Product Agent

When evaluating feedback or feature requests:

1. **Default is NO** — most ideas sound good but add complexity without proportional value
2. **Look for convergent signals** — a single user request is noise; multiple users asking for the same thing is signal
3. **Problem over solution** — users describe solutions ("add a button for X"); find the underlying problem
4. **Cost vs. impact** — a 2-hour fix that helps everyone beats a 2-week feature that helps one person
5. **Reversibility** — prefer changes that can be undone over permanent architectural decisions
6. **Check telemetry first** — if users say "nobody uses X", verify with actual usage data before acting
