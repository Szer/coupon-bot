namespace CouponHubBot

open System
open System.Threading.Tasks

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
