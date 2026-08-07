using Xunit;

namespace Miningcore.Tests;

// Every test that reads or mutates MININGCORE_ADMIN_API_TOKEN must join this
// collection because the credential source and environment variable are process-global.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AdminApiEnvironmentCollection
{
    public const string Name = "Administrative API process environment";
}
