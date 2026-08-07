using Xunit;

namespace Miningcore.Tests;

// Every test that reads or mutates MININGCORE_ADMIN_API_TOKEN, or calls the
// production credential accessor, must join this collection. The environment is
// process-global, and the production provider fixes its initial identity permanently.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AdminApiEnvironmentCollection
{
    public const string Name = "Administrative API process environment";
}
