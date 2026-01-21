namespace FakeAzureOcrApi

open System
open System.Collections.Concurrent

module Store =
    let calls = ConcurrentQueue<ApiCallLog>()
    let mutable responseStatus = 200
    let mutable responseBody = """{"readResult":{"content":"stub","blocks":[]}}"""

    let logCall (methodName: string) (url: string) (body: string) =
        calls.Enqueue(
            { Method = methodName
              Url = url
              Body = body
              Timestamp = DateTime.UtcNow }
        )

    let clearCalls () =
        let mutable item = Unchecked.defaultof<ApiCallLog>
        while calls.TryDequeue(&item) do
            ()

