using Xunit;

namespace Miningcore.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AdminApiEnvironmentCollection
{
    public const string Name = "Administrative API process environment";
}
