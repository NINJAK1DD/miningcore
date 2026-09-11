using System;
using System.Linq;
using System.Reflection;
using Miningcore.Tests.Blockchain.Bitcoin.MergedMining;
using Xunit;

namespace Miningcore.Tests.Util;

public class IntegrationDeadlineCollectionTests
{
    [Fact]
    public void DeadlineCollection_IsExplicitlyNonParallel()
    {
        var definition = typeof(IntegrationDeadlineCollection).GetCustomAttribute<CollectionDefinitionAttribute>();
        Assert.NotNull(definition);
        Assert.Equal(IntegrationDeadlineCollection.Name,
            CollectionName<CollectionDefinitionAttribute>(typeof(IntegrationDeadlineCollection)));
        Assert.True(definition.DisableParallelization);
    }

    [Theory]
    [InlineData(typeof(ProgramPoolTemplateTests))]
    [InlineData(typeof(MergedMiningManagerReorgTests))]
    public void DeadlineFixture_JoinsIsolatedCollection(Type fixture)
    {
        var collection = fixture.GetCustomAttribute<CollectionAttribute>();
        Assert.NotNull(collection);
        Assert.Equal(IntegrationDeadlineCollection.Name, CollectionName<CollectionAttribute>(fixture));
    }

    // xUnit 2.4 exposes collection names through constructor metadata, not a
    // public property on its collection attributes.
    private static string CollectionName<TAttribute>(Type type) where TAttribute : Attribute
    {
        var data = Assert.Single(type.GetCustomAttributesData().Where(x => x.AttributeType == typeof(TAttribute)));
        return Assert.IsType<string>(Assert.Single(data.ConstructorArguments).Value);
    }
}
