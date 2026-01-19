namespace CouponHubBot.Services

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

/// Logs outgoing HTTP requests/responses (method + URL + status + latency).
/// Intended for debugging Telegram API calls in tests.
type OutgoingHttpLoggingHandler(logger: ILogger<OutgoingHttpLoggingHandler>) =
    inherit DelegatingHandler()

    override _.SendAsync(request: HttpRequestMessage, cancellationToken: CancellationToken) : Task<HttpResponseMessage> =
        let started = DateTime.UtcNow
        let methodName = if isNull request || isNull request.Method then "<null>" else request.Method.Method
        let url = if isNull request || isNull request.RequestUri then "<null>" else string request.RequestUri

        logger.LogInformation("HTTP OUT {Method} {Url}", methodName, url)

        // Call base.SendAsync OUTSIDE any async/task lambda; F# otherwise treats it as an inner lambda access.
        let sendTask = base.SendAsync(request, cancellationToken)

        task {
            try
                let! response = sendTask
                let elapsed = DateTime.UtcNow - started
                logger.LogInformation(
                    "HTTP IN  {StatusCode} {Method} {Url} ({ElapsedMs}ms)",
                    int response.StatusCode,
                    methodName,
                    url,
                    int elapsed.TotalMilliseconds
                )
                return response
            with ex ->
                let elapsed = DateTime.UtcNow - started
                logger.LogError(ex, "HTTP ERR {Method} {Url} ({ElapsedMs}ms)", methodName, url, int elapsed.TotalMilliseconds)
                return raise ex
        }

