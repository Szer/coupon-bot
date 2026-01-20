namespace CouponHubBot

open System.Diagnostics

/// ActivitySource for custom spans in traces (OTEL). Used by AddOpenTelemetry in Program.
module Telemetry =
    let botActivity = new ActivitySource("CouponHubBot")
