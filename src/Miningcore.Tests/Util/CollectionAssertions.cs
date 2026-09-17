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
        var attribute = EffectiveCollectionAttribute(fixture);
        Assert.NotNull(attribute);
        var definition = Definition(fixture.Assembly, Name(attribute));
        var settings = definition.GetCustomAttribute<CollectionDefinitionAttribute>();
        Assert.NotNull(settings);
        Assert.True(settings.DisableParallelization);
        return definition;
    }

    internal static IEnumerable<Type> Members(Type definition)
    {
        var name = Name(Assert.Single(definition.CustomAttributes.Where(
            x => x.AttributeType == typeof(CollectionDefinitionAttribute))));
        Assert.Same(definition, Definition(definition.Assembly, name));
        return definition.Assembly.GetTypes().Where(type =>
        {
            var attribute = EffectiveCollectionAttribute(type);
            return attribute != null && Name(attribute) == name;
        });
    }

    private static Type Definition(Assembly assembly, string name) =>
        Assert.Single(assembly.GetTypes().Where(type => type.CustomAttributes.Any(
            attribute => attribute.AttributeType == typeof(CollectionDefinitionAttribute) &&
                Name(attribute) == name)));

    // CollectionAttribute is inherited and single-use in xUnit 2.4.2. A declared
    // collection overrides the base collection; do not union ancestor attributes.
    private static CustomAttributeData EffectiveCollectionAttribute(Type type)
    {
        for(var current = type; current != null; current = current.BaseType)
        {
            var attributes = current.CustomAttributes.Where(x => x.AttributeType == typeof(CollectionAttribute)).ToArray();
            if(attributes.Length != 0)
                return Assert.Single(attributes);
        }

        return null;
    }

    // xUnit 2.4 exposes names via constructor metadata, not public properties.
    private static string Name(CustomAttributeData attribute)
    {
        Assert.True(attribute.ConstructorArguments.Count == 1,
            $"Expected one collection-name argument on {attribute.AttributeType.FullName}; review the resolver when upgrading xUnit.");
        return Assert.IsType<string>(Assert.Single(attribute.ConstructorArguments).Value);
    }
}
