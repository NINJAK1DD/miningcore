using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Miningcore.Tests.Util;

internal static class CollectionAssertions
{
    internal static Type NonParallelDefinition(Type fixture)
    {
        var name = Name<CollectionAttribute>(fixture);
        var definition = Assert.Single(fixture.Assembly.GetTypes().Where(type => type.CustomAttributes.Any(
            attribute => attribute.AttributeType == typeof(CollectionDefinitionAttribute) &&
                Equals(attribute.ConstructorArguments[0].Value, name))));
        Assert.True(definition.GetCustomAttribute<CollectionDefinitionAttribute>().DisableParallelization);
        return definition;
    }

    internal static IEnumerable<Type> Members(Type definition)
    {
        var name = Name<CollectionDefinitionAttribute>(definition);
        return definition.Assembly.GetTypes().Where(type => type.CustomAttributes.Any(
            attribute => attribute.AttributeType == typeof(CollectionAttribute) &&
                Equals(attribute.ConstructorArguments[0].Value, name)));
    }

    // xUnit 2.4 exposes names via constructor metadata, not public properties.
    private static string Name<TAttribute>(Type type) where TAttribute : Attribute
    {
        var attribute = Assert.Single(type.CustomAttributes.Where(x => x.AttributeType == typeof(TAttribute)));
        return Assert.IsType<string>(Assert.Single(attribute.ConstructorArguments).Value);
    }
}
