using Xunit;

namespace Miningcore.Tests.Util;

// xUnit 2.4.2 awaits every parallel collection, then runs disabled collections one
// at a time (XunitTestAssemblyRunner.RunTestCollectionsAsync). This isolates all
// process-wide state here from the logging collections as well as ordinary tests.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresPolicyCollection : ICollectionFixture<IsolatedPostgresServer>
{
    public const string Name = "PostgreSQL TLS policy";
}
