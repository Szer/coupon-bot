namespace FakeAzureOcrApi

open System
open System.Net
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http

module Handlers =
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

    let handleAnalyze (ctx: HttpContext) =
        task {
            // For debugging: store method/url and body length (binary body will be unreadable as UTF-8)
            let url = ctx.Request.Path.ToString() + ctx.Request.QueryString.ToString()
            let! body = readBody ctx
            let len = body.Length
            Console.WriteLine($"FAKE AZURE IN  {ctx.Request.Method} {url} bodyLen={len}")
            Store.logCall ctx.Request.Method url body

            do! respondJson ctx Store.responseStatus Store.responseBody
        }

    let getCalls (ctx: HttpContext) =
        task {
            let calls = Store.calls |> Seq.toArray
            let json = JsonSerializer.Serialize(calls, JsonSerializerOptions(JsonSerializerDefaults.Web))
            do! respondJson ctx 200 json
        }

    let clearCalls (ctx: HttpContext) =
        task {
            Store.clearCalls()
            do! respondJson ctx 200 """{"ok":true}"""
        }

    let setResponse (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload =
                    JsonSerializer.Deserialize<ResponseMockDto>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                if isNull payload then
                    do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
                else
                    Store.responseStatus <- payload.status
                    Store.responseBody <- payload.body
                    do! respondJson ctx 200 """{"ok":true}"""
            with _ ->
                do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
        }

