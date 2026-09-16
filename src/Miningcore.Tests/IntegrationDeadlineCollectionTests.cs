using System;
using System.Linq;
using Miningcore.Tests.Blockchain.Bitcoin.MergedMining;
using Miningcore.Tests.Util;
using Xunit;

namespace Miningcore.Tests;

public class IntegrationDeadlineCollectionTests
{
    [Fact]
    public void DeadlineFixtures_ResolveToNonParallelDefinition()
    {
        foreach(var fixture in ExpectedMembers)
            Assert.Same(typeof(IntegrationDeadlineCollection), CollectionAssertions.NonParallelDefinition(fixture));
    }

    [Fact]
    public void DeadlineCollection_HasExactlyTheReviewedMembers()
    {
        var expected = ExpectedMembers.Select(x => x.FullName).OrderBy(x => x).ToArray();
        var actual = CollectionAssertions.Members(typeof(IntegrationDeadlineCollection))
            .Select(x => x.FullName).OrderBy(x => x).ToArray();
        Assert.Equal(expected, actual);
    }

    private static readonly Type[] ExpectedMembers =
    {
        typeof(ProgramPoolTemplateTests),
        typeof(MergedMiningManagerReorgTests),
    };
}
