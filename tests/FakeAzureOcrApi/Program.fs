open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open FakeAzureOcrApi.Handlers

let builder = WebApplication.CreateBuilder()
let app = builder.Build()

// Azure Computer Vision (stream) analyze endpoint used by AzureOcrService:
// POST /computervision/imageanalysis:analyze?overload=stream&api-version=2024-02-01&features=read
app.MapPost("/computervision/imageanalysis:analyze", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> handleAnalyze ctx))
|> ignore

// Test endpoints (configure response / inspect calls)
app.MapPost("/test/mock/response", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setResponse ctx)) |> ignore
app.MapGet("/test/calls", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> getCalls ctx)) |> ignore
app.MapDelete("/test/calls", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> clearCalls ctx)) |> ignore

app.MapGet("/health", Func<string>(fun () -> "OK")) |> ignore

app.Run()

