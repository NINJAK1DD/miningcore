using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Miningcore.Api.Responses;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Xunit;

namespace Miningcore.Tests.Api.Responses;

public class PublicResponseArchitectureTests
{
    [Fact]
    public void PublicResponseGraph_DoesNotReachRuntimeConfigurationTypes()
    {
        var roots = GetPublicResponseRoots();

        Assert.NotEmpty(roots);
        var violations = FindConfigurationReferences(roots);

        Assert.True(violations.Length == 0,
            "Public API response types must not expose runtime configuration " +
            "types through statically typed public instance members or " +
            "inheritance. Add a dedicated response DTO and one-way mapping for: " +
            string.Join(", ", violations));
    }

    [Fact]
    public void PublicResponseGraph_AllowsOnlyReviewedUntypedMembers()
    {
        var roots = GetPublicResponseRoots();
        var untypedMembers = FindUntypedMembers(roots);
        var reviewedMembers = new[]
        {
            // Tracked by #80: replace this final untyped exception with
            // fail-closed public payment-processing projections.
            $"{typeof(ApiPoolPaymentProcessingConfig).FullName}.Extra",
        };

        Assert.True(reviewedMembers.SequenceEqual(untypedMembers,
                StringComparer.Ordinal),
            "Public API response types may expose untyped members only after " +
            "an explicit review and corresponding value-level redaction " +
            $"coverage. Reviewed: {string.Join(", ", reviewedMembers)}. " +
            $"Discovered: {string.Join(", ", untypedMembers)}");
    }

    [Fact]
    public void ConfigurationTraversal_FollowsInheritanceInterfacesAndFields()
    {
        var violations = FindConfigurationReferences(new[]
        {
            typeof(ConfigurationList),
            typeof(ConfigurationEnumerable),
            typeof(DerivedConfigurationType),
            typeof(FieldConfigurationType),
        });

        Assert.Equal(new[]
        {
            $"ConfigurationEnumerable.Interface<IEnumerable`1><PoolConfig> -> {typeof(PoolConfig).FullName}",
            $"ConfigurationList.Base<List`1><PoolConfig> -> {typeof(PoolConfig).FullName}",
            $"DerivedConfigurationType.Base<PoolShareBasedBanningConfig> -> {typeof(PoolShareBasedBanningConfig).FullName}",
            $"FieldConfigurationType.Leaked -> {typeof(PoolConfig).FullName}",
        }, violations);
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

    [Fact]
    public void ConfigurationTraversal_RejectsBlockchainConfigurationTypes()
    {
        var violations = FindConfigurationReferences(
            new[] { typeof(BlockchainConfigurationType) });

        Assert.Equal(new[]
        {
            $"BlockchainConfigurationType.Leaked -> {typeof(global::Miningcore.Blockchain.Alephium.Configuration.AlephiumPaymentProcessingConfigExtra).FullName}",
        }, violations);
    }

    [Fact]
    public void UntypedTraversal_FindsMembersRegardlessOfNameOrJsonValueType()
    {
        var members = FindUntypedMembers(
            new[] { typeof(UntypedMemberTypes) });

        Assert.Equal(new[]
        {
            $"{typeof(UntypedMemberTypes).FullName}.Array",
            $"{typeof(UntypedMemberTypes).FullName}.Document",
            $"{typeof(UntypedMemberTypes).FullName}.Items",
            $"{typeof(UntypedMemberTypes).FullName}.Metadata",
            $"{typeof(UntypedMemberTypes).FullName}.Nested",
            $"{typeof(UntypedMemberTypes).FullName}.Payload",
            $"{typeof(UntypedMemberTypes).FullName}.Tokens",
        }, members);
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

    private static string[] FindUntypedMembers(IEnumerable<Type> roots)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);

        foreach(var root in roots)
        {
            InspectType(root, root.Name, new HashSet<Type>(),
                new HashSet<string>(StringComparer.Ordinal), true, members);
        }

        return members.OrderBy(value => value).ToArray();
    }

    private static void InspectType(Type type, string path,
        ISet<Type> visited, ISet<string> violations, bool isRoot = false,
        ISet<string> untypedMembers = null)
    {
        if(type == null || type.IsGenericParameter)
            return;

        var nullable = Nullable.GetUnderlyingType(type);
        if(nullable != null)
        {
            InspectType(nullable,
                $"{path}<{nullable.Name}>", visited, violations, false,
                untypedMembers);
            return;
        }

        if(type.HasElementType)
        {
            InspectType(type.GetElementType(), $"{path}[]", visited,
                violations, false, untypedMembers);
            return;
        }

        if(type.IsGenericType)
        {
            foreach(var argument in type.GetGenericArguments())
            {
                InspectType(argument,
                    $"{path}<{argument.Name}>", visited, violations, false,
                    untypedMembers);
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

        if(type.BaseType != null && type.BaseType != typeof(object))
        {
            InspectType(type.BaseType,
                $"{path}.Base<{type.BaseType.Name}>", visited, violations,
                false, untypedMembers);
        }

        var inheritedInterfaces = type.BaseType?.GetInterfaces() ??
            Type.EmptyTypes;
        foreach(var interfaceType in type.GetInterfaces()
                    .Except(inheritedInterfaces)
                    .OrderBy(candidate => candidate.FullName))
        {
            InspectType(interfaceType,
                $"{path}.Interface<{interfaceType.Name}>", visited,
                violations, false, untypedMembers);
        }

        foreach(var property in type.GetProperties(BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(property =>
                    property.GetIndexParameters().Length == 0))
        {
            RecordUntypedMember(type, property, property.PropertyType,
                untypedMembers);
            InspectType(property.PropertyType,
                $"{path}.{property.Name}", visited, violations, false,
                untypedMembers);
        }

        foreach(var field in type.GetFields(BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            RecordUntypedMember(type, field, field.FieldType,
                untypedMembers);
            InspectType(field.FieldType, $"{path}.{field.Name}", visited,
                violations, false, untypedMembers);
        }
    }

    private static void RecordUntypedMember(Type declaringType,
        MemberInfo member, Type memberType, ISet<string> members)
    {
        if(members == null)
            return;

        var isExtensionData = member.GetCustomAttributesData()
            .Any(attribute => attribute.AttributeType.FullName is
                "System.Text.Json.Serialization.JsonExtensionDataAttribute" or
                "Newtonsoft.Json.JsonExtensionDataAttribute");
        var canCarryUntypedValues = CanCarryUntypedValues(memberType,
            new HashSet<Type>());

        if(isExtensionData || canCarryUntypedValues)
        {
            members.Add($"{declaringType.FullName}.{member.Name}");
        }
    }

    private static bool IsUntypedValueType(Type type) =>
        type == typeof(object) ||
        typeof(Newtonsoft.Json.Linq.JToken).IsAssignableFrom(type) ||
        type == typeof(System.Text.Json.JsonElement) ||
        type == typeof(System.Text.Json.JsonDocument) ||
        typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(type);

    private static bool CanCarryUntypedValues(Type type, ISet<Type> visited)
    {
        if(type == null || type.IsGenericParameter)
            return false;

        if(IsUntypedValueType(type))
            return true;

        var nullable = Nullable.GetUnderlyingType(type);
        if(nullable != null)
            return CanCarryUntypedValues(nullable, visited);

        if(type.HasElementType)
            return CanCarryUntypedValues(type.GetElementType(), visited);

        if(!visited.Add(type))
            return false;

        foreach(var candidate in type.GetInterfaces().Prepend(type))
        {
            if(!candidate.IsGenericType)
                continue;

            var definition = candidate.GetGenericTypeDefinition();
            var arguments = candidate.GetGenericArguments();

            if(definition == typeof(IDictionary<,>) ||
                definition == typeof(IReadOnlyDictionary<,>))
            {
                if(CanCarryUntypedValues(arguments[1], visited))
                    return true;
            }
            else if(definition == typeof(IEnumerable<>) &&
                CanCarryUntypedValues(arguments[0], visited))
            {
                return true;
            }
        }

        return false;
    }

    private static Type[] GetPublicResponseRoots()
    {
        var responseNamespace = typeof(PoolInfo).Namespace;
        return typeof(PoolInfo).Assembly.GetExportedTypes()
            .Where(type => type.Namespace != null &&
                (type.Namespace.Equals(responseNamespace,
                     StringComparison.Ordinal) ||
                 type.Namespace.StartsWith(responseNamespace + ".",
                     StringComparison.Ordinal)))
            .OrderBy(type => type.FullName)
            .ToArray();
    }

    // This intentionally includes configuration enums and blockchain-specific
    // configuration namespaces. Public responses should own even benign
    // configuration contracts so later runtime changes cannot alter the wire
    // representation implicitly. Untyped object/extension-data values cannot
    // be proven safe by reflection and require separate value-level redaction
    // tests.
    private static bool IsRuntimeConfigurationType(Type type) =>
        type.Namespace != null &&
        (type.Namespace.Equals(typeof(PoolConfig).Namespace,
             StringComparison.Ordinal) ||
         type.Namespace.StartsWith(typeof(PoolConfig).Namespace + ".",
             StringComparison.Ordinal) ||
         (type.Namespace.StartsWith(typeof(BlockchainStats).Namespace + ".",
              StringComparison.Ordinal) &&
          (type.Namespace.EndsWith(".Configuration",
               StringComparison.Ordinal) ||
           type.Namespace.Contains(".Configuration.",
               StringComparison.Ordinal))));

    private sealed class WrappedConfigurationTypes
    {
        public BanManagerKind? Nullable { get; set; }
        public PoolConfig[] Array { get; set; }
        public List<PoolConfig> Collection { get; set; }
        public Dictionary<string, PoolConfig> Dictionary { get; set; }
    }

    private sealed class ConfigurationList : List<PoolConfig>
    {
    }

    private sealed class ConfigurationEnumerable : IEnumerable<PoolConfig>
    {
        public IEnumerator<PoolConfig> GetEnumerator() =>
            Enumerable.Empty<PoolConfig>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable
            .GetEnumerator() => GetEnumerator();
    }

    private sealed class DerivedConfigurationType :
        PoolShareBasedBanningConfig
    {
    }

    private sealed class FieldConfigurationType
    {
        public PoolConfig Leaked = null;
    }

    private sealed class BlockchainConfigurationType
    {
        public global::Miningcore.Blockchain.Alephium.Configuration.
            AlephiumPaymentProcessingConfigExtra Leaked { get; set; }
    }

    private sealed class UntypedMemberTypes
    {
        public object[] Array { get; set; }
        public Newtonsoft.Json.Linq.JToken Document { get; set; }
        public List<Newtonsoft.Json.Linq.JToken> Items { get; set; }
        public IDictionary<string, object> Metadata { get; set; }
        public Dictionary<string, List<object>> Nested { get; set; }
        public object Payload = null;
        public IReadOnlyDictionary<string, Newtonsoft.Json.Linq.JToken>
            Tokens { get; set; }
    }
}
