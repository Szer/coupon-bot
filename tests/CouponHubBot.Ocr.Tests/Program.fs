open Xunit

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()

module Program =
    [<EntryPoint>]
    let main _ = 0

