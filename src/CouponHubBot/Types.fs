namespace CouponHubBot

open System
open System.Text.Json.Serialization
open Telegram.Bot.Types
open CouponHubBot

[<CLIMutable>]
type BotConfiguration =
    { BotToken: string
      SecretToken: string
      CommunityChatId: int64
      TelegramApiBaseUrl: string | null
      ReminderHourDublin: int
      ReminderRunOnStart: bool
      OcrEnabled: bool
      OcrMaxFileSizeBytes: int64
      AzureOcrEndpoint: string
      AzureOcrKey: string
      FeedbackAdminIds: int64 array
      TestMode: bool
      MaxTakenCoupons: int }

[<CLIMutable>]
type DbUser =
    { id: int64
      username: string | null
      first_name: string | null
      last_name: string | null
      created_at: DateTime
      updated_at: DateTime }

[<CLIMutable>]
type Coupon =
    { id: int
      owner_id: int64
      photo_file_id: string
      value: decimal
      min_check: decimal
      expires_at: DateOnly
      barcode_text: string | null
      status: string
      taken_by: Nullable<int64>
      taken_at: Nullable<DateTime>
      created_at: DateTime }

/// OCR result for coupon photo.
/// Each field is optional: when present it's trusted enough to pre-fill /add wizard,
/// and the user still confirms everything before saving.
[<CLIMutable>]
type CouponOCR =
    { couponValue: Nullable<decimal>
      minCheck: Nullable<decimal>
      validFrom: Nullable<DateTime>
      validTo: Nullable<DateTime>
      barcode: string | null }

[<CLIMutable>]
type CouponEvent =
    { id: int
      coupon_id: int
      user_id: int64
      event_type: string
      created_at: DateTime }

[<CLIMutable>]
type CouponEventHistoryRow =
    { date: string
      user: string
      event_type: string }

/// Used by FakeTgApi test endpoints (serialize minimal info)
[<CLIMutable>]
type ApiCallLog =
    { Method: string
      RequestBody: string
      Timestamp: DateTime
      CorrelationId: string | null }

