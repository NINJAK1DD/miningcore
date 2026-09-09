using System.Globalization;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Miningcore.Configuration;

/// <summary>
/// A deliberately lossy diagnostic contract, independent of runtime JSON contracts.
/// Only the exact types and members below may be read. No string-valued configuration,
/// extension data, arbitrary dictionary, converter, or derived type is traversed.
/// Adding a runtime property does NOT add it to diagnostics: review this allowlist.
/// </summary>
internal static class ConfigurationDiagnosticProjection
{
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

    internal static string Serialize(ClusterConfig config) => new JObject
    {
        ["diagnosticFormatVersion"] = 1,
        ["notice"] = "Lossy diagnostic projection; strings, paths, credentials, extension data and unreviewed fields omitted. Not a reusable configuration.",
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
            var result = new JObject();
            foreach(var property in properties)
                result.Add(Naming.GetPropertyName(property.Name, false),
                    Project(property.GetValue(value)));
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
}
