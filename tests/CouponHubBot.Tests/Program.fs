namespace CouponHubBot.Tests

open Xunit

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
[<assembly: AssemblyFixture(typeof<DefaultCouponHubTestContainers>)>]
[<assembly: AssemblyFixture(typeof<OcrCouponHubTestContainers>)>]
do ()

