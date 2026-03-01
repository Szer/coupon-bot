namespace CouponHubBot.Services

open System
open System.Collections.Generic
open Telegram.Bot.Types
open Telegram.Bot.Types.ReplyMarkups
open CouponHubBot
open CouponHubBot.Utils

/// Pure utility functions and keyboard builders shared across bot handler classes.
/// No DI dependencies — all functions are stateless and side-effect free.
module BotHelpers =

    let formatEventHistoryTable (rows: CouponEventHistoryRow array) =
        let headers = [| "date"; "user"; "event_type" |]
        let widths =
            headers |> Array.mapi (fun i h ->
                rows |> Array.fold (fun mx r ->
                    let v = match i with | 0 -> r.date | 1 -> r.user | _ -> r.event_type
                    max mx v.Length) h.Length)
        let sep = "+" + (widths |> Array.map (fun w -> String('-', w)) |> String.concat "+") + "+"
        let fmtRow vals =
            "|" + (Array.zip widths vals |> Array.map (fun (w, v: string) -> v.PadRight(w)) |> String.concat "|") + "|"
        let lines = [
            sep
            fmtRow headers
            sep
            yield! rows |> Array.map (fun r -> fmtRow [| r.date; r.user; r.event_type |])
            sep
        ]
        String.concat "\n" lines

    /// Short Russian ordinal form used in UI: 1ый, 2ой, 3ий, 4ый, ...
    let formatOrdinalShort (n: int) =
        let suffix =
            match n with
            | 2 | 6 | 7 | 8 -> "ой"
            | 3 -> "ий"
            | _ -> "ый"
        $"{n}{suffix}"

    let parseInt (s: string) =
        match System.Int32.TryParse(s) with
        | true, v -> Some v
        | _ -> None

    let parseDecimalInvariant (s: string) =
        let s2 = s.Trim().Replace(',', '.')
        match Decimal.TryParse(s2, Globalization.NumberStyles.Number, Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    let tryParseTwoDecimals (text: string) =
        if String.IsNullOrWhiteSpace text then None
        else
            let t = text.Trim()
            // Support both "X Y" and "X/Y" formats (spaces around '/' are ok).
            if t.Contains("/") then
                let parts = t.Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)
                if parts.Length <> 2 then None
                else
                    match parseDecimalInvariant parts[0], parseDecimalInvariant parts[1] with
                    | Some a, Some b -> Some(a, b)
                    | _ -> None
            else
                let parts = t.Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                if parts.Length < 2 then None
                else
                    match parseDecimalInvariant parts[0], parseDecimalInvariant parts[1] with
                    | Some a, Some b -> Some(a, b)
                    | _ -> None

    /// Parse a date string in various formats. `today` is used for the "day of month" shortcut.
    let tryParseDateOnly (today: DateOnly) (s: string) =
        let styles = System.Globalization.DateTimeStyles.None
        let culture = System.Globalization.CultureInfo.InvariantCulture
        let formats =
            [| "yyyy-MM-dd"
               "yyyy.MM.dd"
               "yyyy/MM/dd"
               "dd.MM.yyyy"
               "d.M.yyyy"
               "dd/MM/yyyy"
               "d/M/yyyy"
               "dd-MM-yyyy"
               "d-M-yyyy" |]
        let mutable parsed = Unchecked.defaultof<DateOnly>
        let t = if isNull s then "" else s.Trim()
        if DateOnly.TryParseExact(t, formats, culture, styles, &parsed) then
            Some parsed
        else
            // Shortcut: allow user to send a single day-of-month number (1..31),
            // and interpret it as the next such day strictly in the future (UTC).
            let isDigitsOnly =
                not (String.IsNullOrWhiteSpace t) && t |> Seq.forall Char.IsDigit

            if isDigitsOnly then
                match Int32.TryParse(t) with
                | true, day when day >= 1 && day <= 31 ->
                    DateUtils.nextDayOfMonthStrictlyFuture today day
                | _ -> None
            else
                None

    let formatCouponValue (c: Coupon) =
        let v = c.value.ToString("0.##")
        let mc = c.min_check.ToString("0.##")
        $"{v}€ из {mc}€"

    let formatUiDate (d: DateOnly) =
        Utils.DateFormatting.formatDateNoYearWithDow d

    let formatAvailableCouponLine (idx: int) (c: Coupon) =
        let d = formatUiDate c.expires_at
        $"{idx}. {formatCouponValue c}, до {d}"

    /// Picks coupons for /list:
    /// 1) all expiring today (Dublin),
    /// 2) at least 2 coupons of min_check=25 (fivers) when available,
    ///    plus at least 1 of each [40; 50; 100] when available,
    /// 3) the result of (1)+(2) may exceed 6 and must not be truncated,
    /// 4) if the result is still < 6, fill with the nearest-by-expiry coupons up to 6.
    /// Input is expected to be sorted by expires_at, id.
    let pickCouponsForList (today: DateOnly) (coupons: Coupon array) =
        if coupons.Length = 0 then
            [||]
        else
            let distinctById (arr: Coupon array) =
                let seen = HashSet<int>()
                arr |> Array.filter (fun c -> seen.Add c.id)

            let expiringToday =
                coupons
                |> Array.filter (fun c -> c.expires_at = today)

            let requiredMinCheckSlots = [| 25m; 25m; 40m; 50m; 100m |]

            let slotPicks =
                let usedIds = HashSet<int>()
                requiredMinCheckSlots
                |> Array.choose (fun mc ->
                    match coupons |> Array.tryFind (fun c -> c.min_check = mc && not (usedIds.Contains c.id)) with
                    | Some c -> usedIds.Add c.id |> ignore; Some c
                    | None -> None)

            let selected =
                Array.append expiringToday slotPicks
                |> distinctById

            let target = min 6 coupons.Length

            let filled =
                if selected.Length >= target then
                    selected
                else
                    let selectedIds = HashSet<int>(selected |> Array.map (fun c -> c.id))
                    let remaining =
                        coupons
                        |> Array.filter (fun c -> not (selectedIds.Contains c.id))

                    // When filling up to 6, prefer non-"fivers" first (min_check <> 25),
                    // and only then add remaining "fivers" (min_check = 25) if still needed.
                    let needed = target - selected.Length
                    let remainingNonFivers = remaining |> Array.filter (fun c -> c.min_check <> 25m)
                    let remainingFivers = remaining |> Array.filter (fun c -> c.min_check = 25m)

                    let fillNonFivers = remainingNonFivers |> Array.truncate needed
                    let stillNeeded = needed - fillNonFivers.Length
                    let fillFivers =
                        if stillNeeded > 0 then remainingFivers |> Array.truncate stillNeeded
                        else [||]

                    Array.append selected (Array.append fillNonFivers fillFivers)

            filled |> Array.sortBy (fun c -> c.expires_at, c.id)

    let getLargestPhotoFileId (msg: Message) =
        if isNull msg.Photo || msg.Photo.Length = 0 then None
        else
            let p = msg.Photo |> Array.maxBy (fun p -> if p.FileSize.HasValue then p.FileSize.Value else 0)
            Some p.FileId

    let couponsKeyboard (coupons: Coupon array) =
        coupons
        |> Array.indexed
        |> Array.map (fun (i, c) ->
            let humanIdx = i + 1
            seq { InlineKeyboardButton.WithCallbackData($"Взять {formatOrdinalShort humanIdx}", $"take:{c.id}") })
        |> Seq.ofArray
        |> InlineKeyboardMarkup

    let addWizardDiscountKeyboard () =
        seq {
            seq { InlineKeyboardButton.WithCallbackData("5€/25€", "addflow:disc:5:25") }
            seq { InlineKeyboardButton.WithCallbackData("10€/40€", "addflow:disc:10:40") }
            seq { InlineKeyboardButton.WithCallbackData("10€/50€", "addflow:disc:10:50") }
            seq { InlineKeyboardButton.WithCallbackData("20€/100€", "addflow:disc:20:100") }
        }
        |> InlineKeyboardMarkup

    let addWizardDateKeyboard () =
        seq {
            seq { InlineKeyboardButton.WithCallbackData("Сегодня", "addflow:date:today") }
            seq { InlineKeyboardButton.WithCallbackData("Завтра", "addflow:date:tomorrow") }
        }
        |> InlineKeyboardMarkup

    let addWizardOcrKeyboard () =
        seq {
            seq {
                InlineKeyboardButton.WithCallbackData("✅ Да, всё верно", "addflow:ocr:yes")
                InlineKeyboardButton.WithCallbackData("Нет, выбрать вручную", "addflow:ocr:no")
            }
        }
        |> InlineKeyboardMarkup

    let addWizardConfirmKeyboard () =
        seq {
            seq {
                InlineKeyboardButton.WithCallbackData("✅ Добавить", "addflow:confirm")
                InlineKeyboardButton.WithCallbackData("↩️ Отмена", "addflow:cancel")
            }
        }
        |> InlineKeyboardMarkup

    /// Клавиатура для сообщения «Ты взял купон»: при успешном used/return сообщение удаляем.
    let singleTakenKeyboard (c: Coupon) =
        seq {
            seq {
                InlineKeyboardButton.WithCallbackData("Вернуть", $"return:{c.id}:del")
                InlineKeyboardButton.WithCallbackData("Использован", $"used:{c.id}:del")
            }
        }
        |> InlineKeyboardMarkup

    let buildConfirmTextAndKeyboard (value: decimal) (minCheck: decimal) (expiresAt: DateOnly) (barcodeText: string | null) =
        let v = value.ToString("0.##")
        let mc = minCheck.ToString("0.##")
        let d = formatUiDate expiresAt
        let barcodeStr =
            if String.IsNullOrWhiteSpace barcodeText then ""
            else $", штрихкод: {barcodeText}"
        let kb = addWizardConfirmKeyboard ()
        let text = $"Подтвердить добавление купона: {v}€ из {mc}€, до {d}{barcodeStr}?"
        text, kb
