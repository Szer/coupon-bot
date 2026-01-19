namespace CouponHubBot.Tests

open System.Text.Json

module FakeCallHelpers =
    /// Parses JSON body from FakeTgApi call and extracts chat_id and text fields
    type ParsedCall =
        { ChatId: int64 option
          Text: string option
          Caption: string option }

    let parseCallBody (body: string) : ParsedCall option =
        try
            use doc = JsonDocument.Parse(body)
            let root = doc.RootElement

            let chatId =
                match root.TryGetProperty("chat_id") with
                | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt64())
                | _ -> None

            let text =
                match root.TryGetProperty("text") with
                | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                | _ -> None

            let caption =
                match root.TryGetProperty("caption") with
                | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                | _ -> None

            Some { ChatId = chatId; Text = text; Caption = caption }
        with _ ->
            None

    /// Finds a call with specific chat_id and checks if text contains substring
    let findCallWithText (calls: FakeCall array) (chatId: int64) (textSubstring: string) : bool =
        calls
        |> Array.exists (fun call ->
            match parseCallBody call.Body with
            | Some parsed when parsed.ChatId = Some chatId ->
                match parsed.Text with
                | Some text -> text.Contains(textSubstring)
                | _ -> false
            | _ -> false)

    /// Finds a call with specific chat_id and checks if text contains any of the substrings
    let findCallWithAnyText (calls: FakeCall array) (chatId: int64) (textSubstrings: string array) : bool =
        calls
        |> Array.exists (fun call ->
            match parseCallBody call.Body with
            | Some parsed when parsed.ChatId = Some chatId ->
                match parsed.Text with
                | Some text -> textSubstrings |> Array.exists text.Contains
                | _ -> false
            | _ -> false)
