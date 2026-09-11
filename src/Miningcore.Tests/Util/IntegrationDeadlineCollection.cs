using Xunit;

namespace Miningcore.Tests.Util;

// These fixtures measure real host shutdown and loopback RPC deadlines. xUnit
// 2.4's aggressive scheduler can delay their continuations behind unrelated
// tests. Let xUnit schedule this collection separately instead of escaping its
// concurrency limit with Task.Run or increasing the production/test deadlines.
// https://xunit.net/docs/running-tests-in-parallel#selectively-opting-out-of-parallelism
// Collection membership applies to both entire classes, including their unit
// cases. Other collections retain their existing parallelism settings.
// Revisit this isolation when upgrading xUnit to its conservative scheduler.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IntegrationDeadlineCollection
{
    public const string Name = "Host and RPC integration deadlines";
}
