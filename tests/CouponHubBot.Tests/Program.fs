namespace CouponHubBot.Tests

open Xunit
open Xunit.Sdk
open System.Collections.Generic
open System.Linq

type AlphabeticalTestCaseOrderer() =
    interface ITestCaseOrderer with
        member _.OrderTestCases(testCases: IReadOnlyCollection<'TTestCase>) : IReadOnlyCollection<'TTestCase> =
            testCases
            |> Seq.sortBy (fun tc -> tc.TestCaseDisplayName)
            |> Seq.toArray
            :> IReadOnlyCollection<_>

[<assembly: TestCaseOrderer(typeof<AlphabeticalTestCaseOrderer>)>]
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
[<assembly: AssemblyFixture(typeof<DefaultCouponHubTestContainers>)>]
[<assembly: AssemblyFixture(typeof<OcrCouponHubTestContainers>)>]
do ()

