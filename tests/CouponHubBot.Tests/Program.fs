open Xunit
open Xunit.Extensions.AssemblyFixture

[<assembly: TestFramework(AssemblyFixtureFramework.TypeName, AssemblyFixtureFramework.AssemblyName)>]
do ()

module Program =
    [<EntryPoint>]
    let main _ = 0

