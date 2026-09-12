using System;
using System.Collections.Generic;
using System.Linq;
using Miningcore.Tests.Mining;
using Miningcore.Tests.Payments;
using Miningcore.Tests.Rpc;
using Miningcore.Tests.Util;
using Xunit;

namespace Miningcore.Tests;

public class LoggingCollectionTests
{
    public static IEnumerable<object[]> ReviewedCollections()
    {
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
    public void LoggingCollection_HasExactlyTheReviewedMembers(Type definition, Type[] expected)
    {
        foreach(var fixture in expected)
            Assert.Same(definition, CollectionAssertions.NonParallelDefinition(fixture));

        Assert.Equal(expected.Select(x => x.FullName).OrderBy(x => x),
            CollectionAssertions.Members(definition).Select(x => x.FullName).OrderBy(x => x));
    }
}
