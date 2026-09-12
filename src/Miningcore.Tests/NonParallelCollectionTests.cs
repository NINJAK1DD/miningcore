using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Miningcore.Tests.Blockchain.Bitcoin;
using Miningcore.Tests.Blockchain.BitcoinBlake2b;
using Miningcore.Tests.Mining;
using Miningcore.Tests.Payments;
using Miningcore.Tests.Rpc;
using Miningcore.Tests.Util;
using Xunit;

namespace Miningcore.Tests;

public class NonParallelCollectionTests
{
    [Fact]
    public void NonParallelDefinitions_HaveExactlyTheReviewedInventory()
    {
        var expected = ReviewedCollections().Select(row => (Type) row[0])
            .Append(typeof(IntegrationDeadlineCollection));
        var actual = typeof(NonParallelCollectionTests).Assembly.GetTypes().Where(type =>
            type.GetCustomAttribute<CollectionDefinitionAttribute>(inherit: false)?.DisableParallelization == true);

        Assert.Equal(expected.Select(x => x.FullName).OrderBy(x => x),
            actual.Select(x => x.FullName).OrderBy(x => x));
    }

    public static IEnumerable<object[]> ReviewedCollections()
    {
        // The deadline collection has its own exact-membership contract.
        yield return new object[] { typeof(AdminApiEnvironmentCollection), new[] { typeof(AdminApiSecurityTests) } };
        yield return new object[] { typeof(BitcoinCorePayoutIntegrationCollection),
            new[] { typeof(BitcoinDirectSoloRegtestTests), typeof(BitcoinPayoutHandlerRegtestTests),
                typeof(BitcoinVersionRollingRegtestTests), typeof(BitcoinBlake2bRegtestTests),
                typeof(BitcoinBlake2bStartupTests), typeof(MergedMiningPayoutRegtestTests) } };
        yield return new object[] { typeof(RpcDiagnosticCollection),
            new[] { typeof(RpcDiagnosticTests), typeof(RpcConsumerDiagnosticTests) } };
        yield return new object[] { typeof(PayoutManagerLoggingCollection), new[] { typeof(PayoutManagerLoggingTests) } };
        yield return new object[] { typeof(IPAccessWhitelistLoggingCollection), new[] { typeof(IPAccessWhitelistLoggingTests) } };
        yield return new object[] { typeof(ShareRecoveryLoggingCollection),
            new[] { typeof(ShareRecorderTests), typeof(ShareRecoveryPathOwnershipTests) } };
        yield return new object[] { typeof(PostgresConfigurationLoggingCollection), new[] { typeof(PostgresConfigurationLoggingTests) } };
    }

    [Theory]
    [MemberData(nameof(ReviewedCollections))]
    public void NonParallelCollection_HasExactlyTheReviewedMembers(Type definition, Type[] expected)
    {
        foreach(var fixture in expected)
            Assert.Same(definition, CollectionAssertions.NonParallelDefinition(fixture));

        Assert.Equal(expected.Select(x => x.FullName).OrderBy(x => x),
            CollectionAssertions.Members(definition).Select(x => x.FullName).OrderBy(x => x));
    }
}
