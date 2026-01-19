namespace CouponHubBot

open System
open System.Text.Json.Serialization
open Telegram.Bot.Types

[<CLIMutable>]
type BotConfiguration =
    { BotToken: string
      SecretToken: string
      CommunityChatId: int64
      LogsChatId: int64 option
      TelegramApiBaseUrl: string | null
      ReminderHourUtc: int
      ReminderRunOnStart: bool
      OcrEnabled: bool
      OcrMaxFileSizeBytes: int64
      AzureOcrEndpoint: string
      AzureOcrKey: string
      TestMode: bool }

[<CLIMutable>]
type DbUser =
    { id: int64
      username: string | null
      first_name: string | null
      last_name: string | null
      created_at: DateTime
      updated_at: DateTime }

module DbUser =
    let ofTelegramUser (u: User) =
        { id = u.Id
          username = u.Username
          first_name = u.FirstName
          last_name = u.LastName
          created_at = DateTime.UtcNow
          updated_at = DateTime.UtcNow }

[<CLIMutable>]
type Coupon =
    { id: int
      owner_id: int64
      photo_file_id: string
      value: decimal
      expires_at: DateOnly
      barcode_text: string | null
      status: string
      taken_by: Nullable<int64>
      taken_at: Nullable<DateTime>
      created_at: DateTime }

[<CLIMutable>]
type CouponEvent =
    { id: int
      coupon_id: int
      user_id: int64
      event_type: string
      created_at: DateTime }

/// Used by FakeTgApi test endpoints (serialize minimal info)
[<CLIMutable>]
type ApiCallLog =
    { Method: string
      RequestBody: string
      Timestamp: DateTime
      CorrelationId: string | null }

