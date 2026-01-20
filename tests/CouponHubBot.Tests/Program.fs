open Xunit
open Xunit.Extensions.AssemblyFixture

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
[<assembly: TestFramework(AssemblyFixtureFramework.TypeName, AssemblyFixtureFramework.AssemblyName)>]
do ()

module Program =
    [<EntryPoint>]
    let main _ = 0

