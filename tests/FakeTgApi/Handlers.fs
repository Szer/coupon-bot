namespace FakeTgApi

open System
open System.Net
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http

module Handlers =
    let okResult (resultJson: string) =
        $"""{{"ok":true,"result":{resultJson}}}"""

    let readBody (ctx: HttpContext) =
        task {
            if ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value = 0L then
                return ""
            else
                use reader = new IO.StreamReader(ctx.Request.Body, Encoding.UTF8)
                return! reader.ReadToEndAsync()
        }

    let respondJson (ctx: HttpContext) (status: int) (json: string) =
        task {
            ctx.Response.StatusCode <- status
            ctx.Response.ContentType <- "application/json"
            let bytes = Encoding.UTF8.GetBytes(json)
            do! ctx.Response.Body.WriteAsync(bytes.AsMemory(0, bytes.Length))
        }

    let methodFromPath (path: string) =
        // /bot{token}/{method}
        let parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
        if parts.Length >= 2 && parts[0].StartsWith("bot", StringComparison.OrdinalIgnoreCase) then
            Some parts[1]
        else None

    let handleTelegramMethod (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            let url = ctx.Request.Path.ToString()
            match methodFromPath url with
            | None ->
                do! respondJson ctx (int HttpStatusCode.NotFound) """{"ok":false}"""
            | Some methodName ->
                // Debug log for incoming Telegram API calls
                let len = if isNull body then 0 else body.Length
                Console.WriteLine($"FAKE TG IN  {methodName} {url} bodyLen={len}")
                Store.logCall methodName url body

                match methodName with
                | "sendMessage" ->
                    let chatId =
                        try
                            use doc = JsonDocument.Parse(body)
                            match doc.RootElement.TryGetProperty("chat_id") with
                            | true, v -> v.GetInt64()
                            | _ -> 1L
                        with _ -> 1L
                    let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    let resultJson =
                        $"""{{"message_id":1,"date":{now},"chat":{{"id":{chatId},"type":"private"}},"text":"ok"}}"""
                    do! respondJson ctx 200 (okResult resultJson)
                | "sendPhoto" ->
                    let chatId =
                        try
                            use doc = JsonDocument.Parse(body)
                            match doc.RootElement.TryGetProperty("chat_id") with
                            | true, v -> v.GetInt64()
                            | _ -> 1L
                        with _ -> 1L
                    let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    let resultJson =
                        $"""{{"message_id":1,"date":{now},"chat":{{"id":{chatId},"type":"private"}},"caption":"ok"}}"""
                    do! respondJson ctx 200 (okResult resultJson)
                | "answerCallbackQuery" ->
                    do! respondJson ctx 200 (okResult "true")
                | "getChatMember" ->
                    // We only need Status for membership checks.
                    // For simplicity return ChatMemberLeft/Member depending on a test-set map.
                    let userId =
                        try
                            use doc = JsonDocument.Parse(body)
                            doc.RootElement.GetProperty("user_id").GetInt64()
                        with _ -> 0L

                    let status =
                        match Store.chatMemberStatus.TryGetValue userId with
                        | true, s -> s
                        | _ -> "member"

                    let normalized =
                        match status with
                        | "kicked" -> "kicked"
                        | "left" -> "left"
                        | "administrator" -> "administrator"
                        | "creator" -> "creator"
                        | _ -> "member"

                    let resultJson =
                        $"""{{"status":"{normalized}","user":{{"id":{userId},"is_bot":false,"first_name":"x"}}}}"""
                    do! respondJson ctx 200 (okResult resultJson)
                | "getFile" ->
                    let fileId =
                        try
                            use doc = JsonDocument.Parse(body)
                            doc.RootElement.GetProperty("file_id").GetString()
                        with _ -> "file"
                    let filePath = $"photos/{fileId}.jpg"
                    let resultJson =
                        $"""{{"file_id":"{fileId}","file_unique_id":"{fileId}-uid","file_size":1024,"file_path":"{filePath}"}}"""
                    do! respondJson ctx 200 (okResult resultJson)
                | "deleteMessage" ->
                    do! respondJson ctx 200 (okResult "true")
                | _ ->
                    // generic OK (true)
                    do! respondJson ctx 200 (okResult "true")
        }

    let handleFileDownload (ctx: HttpContext) =
        task {
            let bytes = Encoding.UTF8.GetBytes(ctx.Request.Path.ToString())
            ctx.Response.StatusCode <- 200
            ctx.Response.ContentType <- "application/octet-stream"
            do! ctx.Response.Body.WriteAsync(bytes.AsMemory(0, bytes.Length))
        }

    let getCalls (ctx: HttpContext) =
        task {
            let methodFilter =
                if ctx.Request.Query.ContainsKey("method") then
                    string ctx.Request.Query["method"]
                else null

            let calls =
                Store.calls
                |> Seq.filter (fun c -> isNull methodFilter || c.Method = methodFilter)
                |> Seq.toArray

            let json = JsonSerializer.Serialize(calls, JsonSerializerOptions(JsonSerializerDefaults.Web))
            do! respondJson ctx 200 json
        }

    let clearCalls (ctx: HttpContext) =
        task {
            Store.clearCalls()
            do! respondJson ctx 200 (okResult "true")
        }

    let setChatMember (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload = JsonSerializer.Deserialize<ChatMemberMock>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                Store.chatMemberStatus[payload.userId] <- payload.status
                do! respondJson ctx 200 (okResult "true")
            with _ ->
                do! respondJson ctx 400 (okResult "false")
        }

