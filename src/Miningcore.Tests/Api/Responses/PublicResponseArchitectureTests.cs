using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Miningcore.Api.Responses;
using Miningcore.Configuration;
using Xunit;

namespace Miningcore.Tests.Api.Responses;

public class PublicResponseArchitectureTests
{
    [Fact]
    public void PublicResponseGraph_DoesNotReachRuntimeConfigurationTypes()
    {
        var responseNamespace = typeof(PoolInfo).Namespace;
        var roots = typeof(PoolInfo).Assembly.GetExportedTypes()
            .Where(type => type.Namespace != null &&
                (type.Namespace.Equals(responseNamespace,
                     StringComparison.Ordinal) ||
                 type.Namespace.StartsWith(responseNamespace + ".",
                     StringComparison.Ordinal)))
            .OrderBy(type => type.FullName)
            .ToArray();

        Assert.NotEmpty(roots);
        var violations = FindConfigurationReferences(roots);

        Assert.True(violations.Length == 0,
            "Public API response types must not expose runtime configuration " +
            "types. Add a dedicated response DTO and one-way mapping for: " +
            string.Join(", ", violations));
    }

    [Fact]
    public void ConfigurationTraversal_UnwrapsSupportedContainerShapes()
    {
        var violations = FindConfigurationReferences(
            new[] { typeof(WrappedConfigurationTypes) });

        Assert.Equal(new[]
        {
            $"WrappedConfigurationTypes.Array[] -> {typeof(PoolConfig).FullName}",
            $"WrappedConfigurationTypes.Collection<PoolConfig> -> {typeof(PoolConfig).FullName}",
            $"WrappedConfigurationTypes.Dictionary<PoolConfig> -> {typeof(PoolConfig).FullName}",
            $"WrappedConfigurationTypes.Nullable<BanManagerKind> -> {typeof(BanManagerKind).FullName}",
        }, violations);
    }

    private static string[] FindConfigurationReferences(
        IEnumerable<Type> roots)
    {
        var violations = new HashSet<string>(StringComparer.Ordinal);

        foreach(var root in roots)
        {
            var visited = new HashSet<Type>();
            InspectType(root, root.Name, visited, violations, true);
        }

        return violations.OrderBy(value => value).ToArray();
    }

    private static void InspectType(Type type, string path,
        ISet<Type> visited, ISet<string> violations, bool isRoot = false)
    {
        if(type == null || type.IsGenericParameter)
            return;

        var nullable = Nullable.GetUnderlyingType(type);
        if(nullable != null)
        {
            InspectType(nullable,
                $"{path}<{nullable.Name}>", visited, violations);
            return;
        }

        if(type.HasElementType)
        {
            InspectType(type.GetElementType(), $"{path}[]", visited,
                violations);
            return;
        }

        if(type.IsGenericType)
        {
            foreach(var argument in type.GetGenericArguments())
            {
                InspectType(argument,
                    $"{path}<{argument.Name}>", visited, violations);
            }
        }

        if(IsRuntimeConfigurationType(type))
        {
            violations.Add($"{path} -> {type.FullName}");
            return;
        }

        if(!isRoot && type.Assembly != typeof(PoolInfo).Assembly)
            return;

        if(!visited.Add(type))
            return;

        foreach(var property in type.GetProperties(BindingFlags.Instance |
                    BindingFlags.Public).Where(property =>
                    property.GetIndexParameters().Length == 0))
        {
            InspectType(property.PropertyType,
                $"{path}.{property.Name}", visited, violations);
        }
    }

    private static bool IsRuntimeConfigurationType(Type type) =>
        type.Namespace != null &&
        (type.Namespace.Equals(typeof(PoolConfig).Namespace,
             StringComparison.Ordinal) ||
         type.Namespace.StartsWith(typeof(PoolConfig).Namespace + ".",
             StringComparison.Ordinal));

    private sealed class WrappedConfigurationTypes
    {
        public BanManagerKind? Nullable { get; set; }
        public PoolConfig[] Array { get; set; }
        public List<PoolConfig> Collection { get; set; }
        public Dictionary<string, PoolConfig> Dictionary { get; set; }
    }
}
