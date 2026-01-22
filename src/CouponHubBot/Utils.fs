namespace CouponHubBot

open System
open System.Threading.Tasks
open System.Globalization

module Utils =
    let inline (~%) x = ignore x

    let getEnv name =
        let value = Environment.GetEnvironmentVariable name
        if value = null then
            ArgumentException $"Required environment variable %s{name} not found"
            |> raise
        else
            value

    let getEnvOr (name: string) (defaultValue: string) =
        let value = Environment.GetEnvironmentVariable(name)
        if isNull value then defaultValue else value

    let getEnvOrBool (name: string) (defaultValue: bool) =
        match Environment.GetEnvironmentVariable(name) with
        | null -> defaultValue
        | v -> Boolean.Parse(v)

    let getEnvOrInt64 (name: string) (defaultValue: int64) =
        match Environment.GetEnvironmentVariable(name) with
        | null -> defaultValue
        | v -> Int64.Parse(v)

    /// If environment variable is set, call f with its value.
    let getEnvWith (name: string) (f: string -> unit) =
        match Environment.GetEnvironmentVariable(name) with
        | null -> ()
        | v -> f v

    type Task<'a> with
        member this.Ignore() = task { let! _ = this in () }

    let inline taskIgnore (t: Task<'a>) = t.Ignore()

    // needed for STJ
    let jsonOptions =
        let baseOpts = Microsoft.AspNetCore.Http.Json.JsonOptions()
        Telegram.Bot.JsonBotAPI.Configure(baseOpts.SerializerOptions)
        
        // HACK TIME
        // there is a contradiction in Telegram.Bot library where User.IsBot is not nullable and required during deserialization,
        // but it is omitted when default on deserialization via settings setup in JsonBotAPI.Configure
        // so we'll override this setting explicitly
        baseOpts.SerializerOptions.DefaultIgnoreCondition <- System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        
        baseOpts.SerializerOptions

    module DateUtils =
        /// Find the next date (strictly after `today`) that has given day-of-month.
        /// Skips months that don't contain the day (e.g. 31 in April).
        let nextDayOfMonthStrictlyFuture (today: DateOnly) (dayOfMonth: int) : DateOnly option =
            if dayOfMonth < 1 || dayOfMonth > 31 then
                None
            else
                // Search forward month-by-month (bounded to avoid infinite loops).
                let startMonth = DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc)

                let rec loop monthOffset =
                    if monthOffset > 24 then
                        None
                    else
                        let dt = startMonth.AddMonths(monthOffset)
                        try
                            let candidate = DateOnly(dt.Year, dt.Month, dayOfMonth)
                            if candidate > today then Some candidate else loop (monthOffset + 1)
                        with _ ->
                            loop (monthOffset + 1)

                loop 0

    module DateFormatting =
        let private ru = CultureInfo("ru-RU")

        /// User-facing date format: day + full month name + full day-of-week, no year.
        /// Example: "22 января, четверг"
        let formatDateNoYearWithDow (d: DateOnly) =
            d.ToString("d MMMM, dddd", ru)

    module TimeZones =
        let private tryFind (id: string) =
            try
                Some(TimeZoneInfo.FindSystemTimeZoneById(id))
            with _ ->
                None

        // NOTE:
        // - Linux containers typically have IANA TZ IDs (e.g. "Europe/Dublin")
        // - Windows typically uses Windows TZ IDs (e.g. "GMT Standard Time")
        let private dublinTzLazy =
            lazy (
                match tryFind "Europe/Dublin" with
                | Some tz -> tz
                | None ->
                    match tryFind "GMT Standard Time" with
                    | Some tz -> tz
                    | None -> TimeZoneInfo.Utc
            )

        let getDublinTimeZone () = dublinTzLazy.Value

        /// Returns "today" date in Europe/Dublin (derived from TimeProvider UTC now).
        let dublinToday (time: TimeProvider) =
            let nowUtc = time.GetUtcNow().UtcDateTime
            let local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, getDublinTimeZone ())
            DateOnly.FromDateTime(local)

/// Time helpers (testability via TimeProvider).
module Time =
    /// Environment variable that, when set, freezes `TimeProvider` to a constant UTC time.
    /// Format: any DateTimeOffset parseable string, recommended ISO-8601 like `2026-01-21T08:00:00Z`.
    [<Literal>]
    let FixedUtcNowEnvVar = "BOT_FIXED_UTC_NOW"

    type FixedTimeProvider(fixedUtcNow: DateTimeOffset) =
        inherit TimeProvider()
        override _.GetUtcNow() = fixedUtcNow

    let private parseFixedUtcNow (raw: string) =
        match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
        | true, dto -> dto
        | _ -> failwithf "Invalid %s value: '%s'. Expected ISO-8601 like 2026-01-21T08:00:00Z" FixedUtcNowEnvVar raw

    let fromEnvironment () : TimeProvider =
        match Utils.getEnvOr FixedUtcNowEnvVar "" with
        | null
        | "" -> TimeProvider.System
        | raw -> FixedTimeProvider(parseFixedUtcNow raw) :> TimeProvider
