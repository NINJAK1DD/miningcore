using Xunit;

namespace Miningcore.Tests;

// These fixtures measure real host shutdown and loopback RPC deadlines. xUnit
// 2.4's aggressive scheduler can delay their continuations behind unrelated
// tests. Let xUnit schedule this collection separately instead of escaping its
// concurrency limit with Task.Run or increasing the production/test deadlines.
// https://xunit.net/docs/running-tests-in-parallel#selectively-opting-out-of-parallelism
// Admit fixture classes with observed full-suite continuation delays during
// generic-host lifecycle or loopback JSON-RPC tests. A short timeout alone is
// not a membership criterion: require failure evidence and a full-suite cost
// measurement before expanding this collection. The contract pins its members.
// Membership applies to the whole of both classes, including their unit cases.
// Other collections retain their settings. This root-level definition spans
// the root host fixture and the Blockchain namespace's RPC fixture.
// Revisit this isolation when upgrading xUnit to its conservative scheduler.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IntegrationDeadlineCollection
{
    public const string Name = "Host and RPC integration deadlines";
}
