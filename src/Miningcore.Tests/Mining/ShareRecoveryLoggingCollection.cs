using Xunit;

namespace Miningcore.Tests.Mining;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ShareRecoveryLoggingCollection
{
    public const string Name = "Share recovery logging";
}
