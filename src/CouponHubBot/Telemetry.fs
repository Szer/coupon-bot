namespace CouponHubBot

open System.Diagnostics
open System.Diagnostics.Metrics

/// ActivitySource for custom spans in traces (OTEL). Used by AddOpenTelemetry in Program.
module Telemetry =
    let botActivity = new ActivitySource("CouponHubBot")

module Metrics =
    let meter = new Meter("CouponHubBot.Metrics")

    /// Count of UI/button interactions, tagged by `button`.
    let buttonClickTotal = meter.CreateCounter<int64>("couponhubbot_button_click_total")

    /// Count of command invocations, tagged by `command` (e.g. "list", "add", "feedback").
    let commandTotal = meter.CreateCounter<int64>("couponhubbot_command_total")

    /// Count of callback query actions, tagged by `action` (e.g. "take", "return", "used", "void", "addflow", "myAdded").
    let callbackTotal = meter.CreateCounter<int64>("couponhubbot_callback_total")

    /// Count of user feedback submissions via /feedback flow.
    let feedbackTotal = meter.CreateCounter<int64>("couponhubbot_feedback_total")
