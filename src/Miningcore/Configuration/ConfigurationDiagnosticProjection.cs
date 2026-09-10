using System.Globalization;
using System.Net;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Miningcore.Configuration;

/// <summary>
/// A deliberately lossy diagnostic contract, independent of runtime JSON contracts.
/// Only the exact types and members below may be read. Arbitrary strings are never
/// emitted: explicit presence/category policies are separate from value projection.
/// No extension data, arbitrary dictionary, converter, or derived type is traversed.
/// Adding a runtime property does NOT add it to diagnostics: review this allowlist.
/// </summary>
internal static class ConfigurationDiagnosticProjection
{
    internal enum DiagnosticPolicy { Value, Presence, Count, Category }

    private const string Omitted = "[omitted]";
    private static readonly CamelCaseNamingStrategy Naming = new();

    // These are CLR member names (nameof keeps renames compile-checked), not a
    // recursive JSON-name filter. An extension called "enabled" is still omitted.
    private static readonly IReadOnlyDictionary<Type, PropertyInfo[]> Members =
        new Dictionary<Type, PropertyInfo[]>
        {
            [typeof(ClusterConfig)] = Fields<ClusterConfig>(
                nameof(ClusterConfig.InstanceId), nameof(ClusterConfig.Logging),
                nameof(ClusterConfig.Banning), nameof(ClusterConfig.Persistence),
                nameof(ClusterConfig.PaymentProcessing), nameof(ClusterConfig.Notifications),
                nameof(ClusterConfig.Api), nameof(ClusterConfig.Statistics),
                nameof(ClusterConfig.Nicehash), nameof(ClusterConfig.Memory),
                nameof(ClusterConfig.ShareRelay), nameof(ClusterConfig.ShareRelays),
                nameof(ClusterConfig.EquihashMaxThreads), nameof(ClusterConfig.CryptonightMaxThreads),
                nameof(ClusterConfig.Pools)),
            [typeof(ClusterLoggingConfig)] = Fields<ClusterLoggingConfig>(
                nameof(ClusterLoggingConfig.EnableConsoleLog), nameof(ClusterLoggingConfig.EnableConsoleColors),
                nameof(ClusterLoggingConfig.PerPoolLogFile), nameof(ClusterLoggingConfig.GPDRCompliant)),
            [typeof(ClusterBanningConfig)] = Fields<ClusterBanningConfig>(
                nameof(ClusterBanningConfig.Manager), nameof(ClusterBanningConfig.BanOnJunkReceive),
                nameof(ClusterBanningConfig.BanOnInvalidShares), nameof(ClusterBanningConfig.BanOnLoginFailure)),
            [typeof(PersistenceConfig)] = Fields<PersistenceConfig>(nameof(PersistenceConfig.Postgres)),
            [typeof(PostgresConfig)] = Fields<PostgresConfig>(
                nameof(PostgresConfig.Port), nameof(PostgresConfig.Tls), nameof(PostgresConfig.TlsNoValidate),
                nameof(PostgresConfig.CommandTimeout), nameof(PostgresConfig.EnableLegacyTimestamps)),
            [typeof(ClusterPaymentProcessingConfig)] = Fields<ClusterPaymentProcessingConfig>(
                nameof(ClusterPaymentProcessingConfig.Enabled), nameof(ClusterPaymentProcessingConfig.Interval),
                nameof(ClusterPaymentProcessingConfig.ShareAccountingPruneBatchSize),
                nameof(ClusterPaymentProcessingConfig.ShareAccountingRetentionDays)),
            [typeof(NotificationsConfig)] = Fields<NotificationsConfig>(
                nameof(NotificationsConfig.Enabled), nameof(NotificationsConfig.Email),
                nameof(NotificationsConfig.Pushover), nameof(NotificationsConfig.Admin)),
            [typeof(EmailSenderConfig)] = Fields<EmailSenderConfig>(nameof(EmailSenderConfig.Port)),
            [typeof(PushoverConfig)] = Fields<PushoverConfig>(nameof(PushoverConfig.Enabled)),
            [typeof(AdminNotifications)] = Fields<AdminNotifications>(
                nameof(AdminNotifications.Enabled), nameof(AdminNotifications.NotifyBlockFound),
                nameof(AdminNotifications.NotifyPaymentSuccess)),
            [typeof(ApiConfig)] = Fields<ApiConfig>(
                nameof(ApiConfig.Enabled), nameof(ApiConfig.Port), nameof(ApiConfig.AdminPort),
                nameof(ApiConfig.MetricsPort), nameof(ApiConfig.Tls), nameof(ApiConfig.RateLimiting),
                nameof(ApiConfig.LegacyNullValueHandling)),
            [typeof(ApiTlsConfig)] = Fields<ApiTlsConfig>(nameof(ApiTlsConfig.Enabled)),
            [typeof(ApiRateLimitConfig)] = Fields<ApiRateLimitConfig>(nameof(ApiRateLimitConfig.Disabled)),
            [typeof(Statistics)] = Fields<Statistics>(
                nameof(Statistics.UpdateInterval), nameof(Statistics.HashrateCalculationWindow),
                nameof(Statistics.GcInterval), nameof(Statistics.CleanupDays)),
            [typeof(NicehashClusterConfig)] = Fields<NicehashClusterConfig>(nameof(NicehashClusterConfig.EnableAutoDiff)),
            [typeof(ClusterMemoryConfig)] = Fields<ClusterMemoryConfig>(
                nameof(ClusterMemoryConfig.RmsmMaximumFreeSmallPoolBytes),
                nameof(ClusterMemoryConfig.RmsmMaximumFreeLargePoolBytes)),
            [typeof(ShareRelayConfig)] = Fields<ShareRelayConfig>(nameof(ShareRelayConfig.Connect)),
            [typeof(ShareRelayEndpointConfig)] = Fields<ShareRelayEndpointConfig>(),
            [typeof(PoolConfig)] = Fields<PoolConfig>(
                nameof(PoolConfig.Enabled), nameof(PoolConfig.Ports), nameof(PoolConfig.Daemons),
                nameof(PoolConfig.PaymentProcessing), nameof(PoolConfig.Banning), nameof(PoolConfig.RewardRecipients),
                nameof(PoolConfig.ClientConnectionTimeout), nameof(PoolConfig.JobRebroadcastTimeout),
                nameof(PoolConfig.BlockRefreshInterval), nameof(PoolConfig.EnableInternalStratum),
                nameof(PoolConfig.EnableAsicBoost), nameof(PoolConfig.VardiffIdleSweepInterval)),
            [typeof(PoolEndpoint)] = Fields<PoolEndpoint>(
                nameof(PoolEndpoint.Difficulty), nameof(PoolEndpoint.TcpProxyProtocol),
                nameof(PoolEndpoint.VarDiff), nameof(PoolEndpoint.Tls), nameof(PoolEndpoint.TlsAuto)),
            [typeof(TcpProxyProtocolConfig)] = Fields<TcpProxyProtocolConfig>(
                nameof(TcpProxyProtocolConfig.Enable), nameof(TcpProxyProtocolConfig.Mandatory)),
            [typeof(VarDiffConfig)] = Fields<VarDiffConfig>(
                nameof(VarDiffConfig.MinDiff), nameof(VarDiffConfig.MaxDiff), nameof(VarDiffConfig.MaxDelta),
                nameof(VarDiffConfig.TargetTime), nameof(VarDiffConfig.RetargetTime), nameof(VarDiffConfig.VariancePercent)),
            [typeof(DaemonEndpointConfig)] = Fields<DaemonEndpointConfig>(
                nameof(DaemonEndpointConfig.Port), nameof(DaemonEndpointConfig.Ssl), nameof(DaemonEndpointConfig.Http2)),
            [typeof(PoolPaymentProcessingConfig)] = Fields<PoolPaymentProcessingConfig>(
                nameof(PoolPaymentProcessingConfig.Enabled), nameof(PoolPaymentProcessingConfig.MinimumPayment),
                nameof(PoolPaymentProcessingConfig.PayoutScheme), nameof(PoolPaymentProcessingConfig.PpsShareRetentionDays)),
            [typeof(PoolShareBasedBanningConfig)] = Fields<PoolShareBasedBanningConfig>(
                nameof(PoolShareBasedBanningConfig.Enabled), nameof(PoolShareBasedBanningConfig.CheckThreshold),
                nameof(PoolShareBasedBanningConfig.InvalidPercent), nameof(PoolShareBasedBanningConfig.Time),
                nameof(PoolShareBasedBanningConfig.MinerEffortPercent), nameof(PoolShareBasedBanningConfig.MinerEffortTime)),
            [typeof(RewardRecipient)] = Fields<RewardRecipient>(nameof(RewardRecipient.Percentage)),
        };

    private static readonly IReadOnlyDictionary<Type, PropertyInfo[]> PresenceMembers =
        new Dictionary<Type, PropertyInfo[]>
        {
            [typeof(ClusterConfig)] = Fields<ClusterConfig>(nameof(ClusterConfig.ClusterName),
                nameof(ClusterConfig.ShareRecoveryFile), nameof(ClusterConfig.ShareRecoveryStateDirectory)),
            [typeof(ClusterLoggingConfig)] = Fields<ClusterLoggingConfig>(nameof(ClusterLoggingConfig.LogFile),
                nameof(ClusterLoggingConfig.ApiLogFile), nameof(ClusterLoggingConfig.LogBaseDirectory)),
            [typeof(PostgresConfig)] = Fields<PostgresConfig>(nameof(PostgresConfig.Host), nameof(PostgresConfig.User),
                nameof(PostgresConfig.Password), nameof(PostgresConfig.Database), nameof(PostgresConfig.TlsCert),
                nameof(PostgresConfig.TlsKey), nameof(PostgresConfig.TlsPassword)),
            [typeof(PoolConfig)] = Fields<PoolConfig>(nameof(PoolConfig.Id), nameof(PoolConfig.Coin),
                nameof(PoolConfig.Address), nameof(PoolConfig.PubKey)),
            [typeof(PoolEndpoint)] = Fields<PoolEndpoint>(nameof(PoolEndpoint.Name),
                nameof(PoolEndpoint.TlsPfxFile), nameof(PoolEndpoint.TlsPfxPassword)),
            [typeof(DaemonEndpointConfig)] = Fields<DaemonEndpointConfig>(nameof(DaemonEndpointConfig.Host),
                nameof(DaemonEndpointConfig.User), nameof(DaemonEndpointConfig.Password),
                nameof(DaemonEndpointConfig.HttpPath), nameof(DaemonEndpointConfig.Category)),
            [typeof(ApiTlsConfig)] = Fields<ApiTlsConfig>(nameof(ApiTlsConfig.TlsPfxFile), nameof(ApiTlsConfig.TlsPfxPassword)),
            [typeof(EmailSenderConfig)] = Fields<EmailSenderConfig>(nameof(EmailSenderConfig.Host), nameof(EmailSenderConfig.User),
                nameof(EmailSenderConfig.Password), nameof(EmailSenderConfig.FromAddress), nameof(EmailSenderConfig.FromName)),
            [typeof(PushoverConfig)] = Fields<PushoverConfig>(nameof(PushoverConfig.User), nameof(PushoverConfig.Token)),
            [typeof(AdminNotifications)] = Fields<AdminNotifications>(nameof(AdminNotifications.EmailAddress)),
            [typeof(ShareRelayConfig)] = Fields<ShareRelayConfig>(nameof(ShareRelayConfig.PublishUrl), nameof(ShareRelayConfig.SharedEncryptionKey)),
            [typeof(ShareRelayEndpointConfig)] = Fields<ShareRelayEndpointConfig>(nameof(ShareRelayEndpointConfig.Url), nameof(ShareRelayEndpointConfig.SharedEncryptionKey)),
            [typeof(RewardRecipient)] = Fields<RewardRecipient>(nameof(RewardRecipient.Address), nameof(RewardRecipient.Type)),
            [typeof(ClusterPaymentProcessingConfig)] = Fields<ClusterPaymentProcessingConfig>(nameof(ClusterPaymentProcessingConfig.CoinbaseString)),
        };

    private static readonly IReadOnlyDictionary<Type, PropertyInfo[]> CountMembers =
        new Dictionary<Type, PropertyInfo[]>
        {
            [typeof(ClusterConfig)] = Fields<ClusterConfig>(nameof(ClusterConfig.CoinTemplates)),
            [typeof(ApiConfig)] = Fields<ApiConfig>(nameof(ApiConfig.AdminIpWhitelist), nameof(ApiConfig.MetricsIpWhitelist)),
            [typeof(ApiRateLimitConfig)] = Fields<ApiRateLimitConfig>(nameof(ApiRateLimitConfig.IpWhitelist)),
            [typeof(TcpProxyProtocolConfig)] = Fields<TcpProxyProtocolConfig>(nameof(TcpProxyProtocolConfig.ProxyAddresses)),
        };

    // Logging level is a bounded value vocabulary; listener categories are
    // derived metadata. Suffixes are explicit per reviewed member, not inferred
    // from a coincidentally matching CLR name on another type. Keep the reviewed
    // owner explicit: an inherited property's DeclaringType is not its owner.
    private static readonly PropertyInfo LoggingLevelProperty =
        typeof(ClusterLoggingConfig).GetProperty(nameof(ClusterLoggingConfig.Level));
    private static readonly PropertyInfo ApiListenAddressProperty =
        typeof(ApiConfig).GetProperty(nameof(ApiConfig.ListenAddress));
    private static readonly PropertyInfo PoolListenAddressProperty =
        typeof(PoolEndpoint).GetProperty(nameof(PoolEndpoint.ListenAddress));
    private static readonly IReadOnlyDictionary<(Type Owner, PropertyInfo Property), string> CategorySuffixes =
        new Dictionary<(Type Owner, PropertyInfo Property), string>
        {
            [(typeof(ClusterLoggingConfig), LoggingLevelProperty)] = string.Empty,
            [(typeof(ApiConfig), ApiListenAddressProperty)] = "Category",
            [(typeof(PoolEndpoint), PoolListenAddressProperty)] = "Category",
        };

    // Resolve reflected members and derived names once, not once per port/dump.
    private static readonly string LoggingLevelOutputName =
        GetOutputName(typeof(ClusterLoggingConfig), LoggingLevelProperty, DiagnosticPolicy.Category);
    private static readonly string ApiListenAddressOutputName =
        GetOutputName(typeof(ApiConfig), ApiListenAddressProperty, DiagnosticPolicy.Category);
    private static readonly string PoolListenAddressOutputName =
        GetOutputName(typeof(PoolEndpoint), PoolListenAddressProperty, DiagnosticPolicy.Category);

    // Deliberate omissions are review decisions too. Tests require new public
    // properties to acquire an explicit policy; runtime remains fail-closed.
    private static readonly IReadOnlyDictionary<Type, PropertyInfo[]> ExcludedMembers =
        new Dictionary<Type, PropertyInfo[]>
        {
            [typeof(PoolConfig)] = Fields<PoolConfig>(nameof(PoolConfig.Extra), nameof(PoolConfig.Template)),
            [typeof(DaemonEndpointConfig)] = Fields<DaemonEndpointConfig>(nameof(DaemonEndpointConfig.Extra)),
            [typeof(PoolPaymentProcessingConfig)] = Fields<PoolPaymentProcessingConfig>(
                nameof(PoolPaymentProcessingConfig.Extra), nameof(PoolPaymentProcessingConfig.PayoutSchemeConfig)),
            [typeof(ApiRateLimitConfig)] = Fields<ApiRateLimitConfig>(nameof(ApiRateLimitConfig.Rules)),
        };

    // Expose immutable descriptors to tests so scalar/type drift cannot silently
    // degrade reviewed diagnostics. No configuration instances are exposed.
    internal static IEnumerable<Type> ReviewedObjectTypes => Members.Keys;
    internal static IEnumerable<(Type Owner, PropertyInfo Property, DiagnosticPolicy Policy)> ReviewedMembers =>
        Members.SelectMany(pair => pair.Value.Select(property => (pair.Key, property, DiagnosticPolicy.Value)))
            .Concat(PresenceMembers.SelectMany(pair => pair.Value.Select(property => (pair.Key, property, DiagnosticPolicy.Presence))))
            .Concat(CountMembers.SelectMany(pair => pair.Value.Select(property => (pair.Key, property, DiagnosticPolicy.Count))))
            .Concat(CategorySuffixes.Keys.Select(member => (member.Owner, member.Property, DiagnosticPolicy.Category)));

    internal static IEnumerable<(Type Owner, PropertyInfo Property)> ExcludedProperties =>
        ExcludedMembers.SelectMany(pair => pair.Value.Select(property => (pair.Key, property)));

    // Use this same mapping for emission and contract tests, including generated
    // suffixes. Checking CLR names alone misses FooCount/Foo+Count collisions.
    internal static string GetOutputName(Type owner, PropertyInfo property, DiagnosticPolicy policy) =>
        Naming.GetPropertyName(property.Name, false) + (policy switch
        {
            DiagnosticPolicy.Count => "Count",
            DiagnosticPolicy.Category => CategorySuffixes[(owner, property)],
            DiagnosticPolicy.Value or DiagnosticPolicy.Presence => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        });

    internal static string Serialize(ClusterConfig config) => new JObject
    {
        ["diagnosticFormatVersion"] = 2,
        ["notice"] = "Lossy diagnostic projection; credential values, paths, extension data and unreviewed fields omitted. Reviewed strings use presence markers or bounded categories. Not a reusable configuration.",
        ["configuration"] = Project(config),
    }.ToString(Formatting.Indented);

    private static PropertyInfo[] Fields<T>(params string[] names) =>
        names.Select(name => typeof(T).GetProperty(name) ??
            throw new InvalidOperationException("Invalid diagnostic allowlist")).ToArray();

    private static JToken Project(object value)
    {
        if(value == null)
            return JValue.CreateNull();

        var type = value.GetType();
        if(Members.TryGetValue(type, out var properties))
        {
            // Keep tokens unparented until the final sorted object is built.
            // Rebuilding an intermediate JObject would clone every subtree.
            var fields = new List<(string Name, JToken Value)>();
            foreach(var property in properties)
                fields.Add((GetOutputName(type, property, DiagnosticPolicy.Value),
                    Project(property.GetValue(value))));
            if(PresenceMembers.TryGetValue(type, out var presence))
                foreach(var property in presence)
                {
                    var field = property.GetValue(value);
                    fields.Add((GetOutputName(type, property, DiagnosticPolicy.Presence), field switch
                    {
                        null => JValue.CreateNull(),
                        string text => new JValue(string.IsNullOrWhiteSpace(text) ? "[blank]" : "[set]"),
                        _ => new JValue(Omitted),
                    }));
                }
            if(CountMembers.TryGetValue(type, out var counts))
                foreach(var property in counts)
                {
                    var field = property.GetValue(value);
                    fields.Add((GetOutputName(type, property, DiagnosticPolicy.Count), field == null ? JValue.CreateNull() :
                        field is string[] array ? new JValue(array.Length) : new JValue(Omitted)));
                }
            if(type == typeof(ClusterLoggingConfig))
            {
                var level = ((ClusterLoggingConfig) value).Level?.ToLowerInvariant();
                fields.Add((LoggingLevelOutputName, level == null ? JValue.CreateNull() :
                    new JValue(level is "trace" or "debug" or "info" or "warn" or "error" or "fatal" or "off" ? level : Omitted)));
            }
            if(type == typeof(ApiConfig))
                fields.Add((ApiListenAddressOutputName,
                    ClassifyListenAddress(((ApiConfig) value).ListenAddress)));
            if(type == typeof(PoolEndpoint))
                fields.Add((PoolListenAddressOutputName,
                    ClassifyListenAddress(((PoolEndpoint) value).ListenAddress)));
            // Group all policies by emitted name for easy human scanning. Ports
            // remain numerically ordered below; arrays keep source-file order.
            var result = new JObject();
            foreach(var field in fields.OrderBy(field => field.Name, StringComparer.Ordinal))
                result.Add(field.Name, field.Value);
            return result;
        }

        // Only the reviewed container types are admitted. Never traverse Extra,
        // JToken, string-keyed dictionaries, templates or an arbitrary IEnumerable.
        if(type == typeof(PoolConfig[]) || type == typeof(DaemonEndpointConfig[]) ||
           type == typeof(RewardRecipient[]) || type == typeof(ShareRelayEndpointConfig[]))
            return new JArray(((Array) value).Cast<object>().Select(Project));

        if(type == typeof(Dictionary<int, PoolEndpoint>))
        {
            var result = new JObject();
            foreach(var pair in ((Dictionary<int, PoolEndpoint>) value).OrderBy(pair => pair.Key))
                result.Add(pair.Key.ToString(CultureInfo.InvariantCulture), Project(pair.Value));
            return result;
        }

        if(type == typeof(PayoutScheme) || type == typeof(BanManagerKind))
            return new JValue(Enum.IsDefined(type, value) ? Enum.GetName(type, value) : Omitted);

        // Strings and unknown/derived objects fail closed even if accidentally
        // added to the member allowlist. Never call their ToString or converters.
        return value switch
        {
            bool item => new JValue(item),
            byte item => new JValue(item),
            int item => new JValue(item),
            double item when double.IsFinite(item) => new JValue(item),
            decimal item => new JValue(item),
            _ => new JValue(Omitted),
        };
    }

    private static JToken ClassifyListenAddress(string value)
    {
        if(value == null)
            return JValue.CreateNull();
        if(string.IsNullOrWhiteSpace(value))
            return new JValue("blank");
        if(value == "*")
            return new JValue("any");
        if(!IPAddress.TryParse(value, out var address))
            return new JValue("other"); // No DNS, endpoint probing, or arbitrary text.
        if(address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if(address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return new JValue("any");
        if(IPAddress.IsLoopback(address))
            return new JValue("loopback");
        var bytes = address.GetAddressBytes();
        // RFC6598 shared/CGNAT space is intentionally not labeled private. It
        // remains 'other', as do public, multicast and unrecognized addresses.
        var privateAddress = bytes.Length == 4
            ? bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
              (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254)
            : (bytes[0] & 0xfe) == 0xfc || address.IsIPv6LinkLocal;
        return new JValue(privateAddress ? "private" : "other");
    }
}
