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
