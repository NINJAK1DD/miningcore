using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Sdk;

namespace Miningcore.Tests.Util;

public class CollectionAssertionsTests
{
    [Fact]
    public void Members_IncludeInheritedMembershipAndHonorNearestOverride()
    {
        var module = NewModule();
        var first = Define(module, "First", typeof(CollectionDefinitionAttribute), "first");
        var second = Define(module, "Second", typeof(CollectionDefinitionAttribute), "second");
        var parent = Define(module, "Parent", typeof(CollectionAttribute), "first");
        var child = Define(module, "Child", parent: parent);
        var grandchild = Define(module, "Grandchild", parent: child);
        var replacement = Define(module, "Replacement", typeof(CollectionAttribute), "second", child);
        var replacementChild = Define(module, "ReplacementChild", parent: replacement);
        Define(module, "Unassigned");

        Assert.Equal(new[] { parent, child, grandchild }.OrderBy(x => x.Name),
            CollectionAssertions.Members(first).OrderBy(x => x.Name));
        Assert.Equal(new[] { replacement, replacementChild }.OrderBy(x => x.Name),
            CollectionAssertions.Members(second).OrderBy(x => x.Name));
        Assert.Same(first, CollectionAssertions.NonParallelDefinition(grandchild));
        Assert.Same(second, CollectionAssertions.NonParallelDefinition(replacementChild));
    }

    [Fact]
    public void DuplicateDefinitions_AreRejectedByBothEntryPoints()
    {
        var module = NewModule();
        var definition = Define(module, "First", typeof(CollectionDefinitionAttribute), "duplicate");
        Define(module, "Second", typeof(CollectionDefinitionAttribute), "duplicate");
        var fixture = Define(module, "Fixture", typeof(CollectionAttribute), "duplicate");

        Assert.Throws<SingleException>(() => CollectionAssertions.Members(definition).ToArray());
        Assert.Throws<SingleException>(() => CollectionAssertions.NonParallelDefinition(fixture));
    }

    [Fact]
    public void MissingMembershipOrDefinition_IsRejected()
    {
        var module = NewModule();
        var unassigned = Define(module, "Unassigned");
        var orphan = Define(module, "Orphan", typeof(CollectionAttribute), "missing");

        Assert.Throws<NotNullException>(() => CollectionAssertions.NonParallelDefinition(unassigned));
        Assert.Throws<SingleException>(() => CollectionAssertions.NonParallelDefinition(orphan));
    }

    [Fact]
    public void ParallelDefinition_IsRejected()
    {
        var module = NewModule();
        Define(module, "Parallel", typeof(CollectionDefinitionAttribute), "parallel", disableParallelization: false);
        var fixture = Define(module, "Fixture", typeof(CollectionAttribute), "parallel");

        Assert.Throws<TrueException>(() => CollectionAssertions.NonParallelDefinition(fixture));
    }

    [Fact]
    public void CollectionAttribute_StillUsesInheritedSingleNameContract()
    {
        var usage = typeof(CollectionAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.True(usage.Inherited);
        Assert.False(usage.AllowMultiple);
        foreach(var type in new[] { typeof(CollectionAttribute), typeof(CollectionDefinitionAttribute) })
        {
            var constructor = Assert.Single(type.GetConstructors());
            Assert.Equal(typeof(string), Assert.Single(constructor.GetParameters()).ParameterType);
        }
    }

    // Runtime-only assemblies keep synthetic collections out of xUnit discovery.
    private static ModuleBuilder NewModule()
    {
        Assert.True(RuntimeFeature.IsDynamicCodeSupported,
            "Collection resolver regressions require dynamic code; redesign the synthetic fixtures before adding a Native AOT test lane.");
        return AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("CollectionAssertions-" + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.RunAndCollect).DefineDynamicModule("Fixtures");
    }

    private static Type Define(ModuleBuilder module, string name, Type attributeType = null,
        string collection = null, Type parent = null, bool disableParallelization = true)
    {
        var type = module.DefineType(name, TypeAttributes.Public, parent);
        if(attributeType != null)
        {
            var properties = attributeType == typeof(CollectionDefinitionAttribute)
                ? new[] { attributeType.GetProperty(nameof(CollectionDefinitionAttribute.DisableParallelization)) }
                : Array.Empty<PropertyInfo>();
            var values = properties.Length == 0 ? Array.Empty<object>() : new object[] { disableParallelization };
            type.SetCustomAttribute(new CustomAttributeBuilder(attributeType.GetConstructor(new[] { typeof(string) }),
                new object[] { collection }, properties, values));
        }

        return type.CreateType();
    }
}
