using FluentValidation;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Miningcore.Configuration;

#region Validators

public class EmailSenderConfigValidator : AuthenticatedNetworkEndpointConfigValidator<EmailSenderConfig>
{
    public EmailSenderConfigValidator()
    {
        RuleFor(j => j.FromAddress)
            .NotNull()
            .NotEmpty()
            .WithMessage("EmailSender fromAddress missing or empty");
    }
}

public class AdminNotificationsValidator : AbstractValidator<AdminNotifications>
{
    public AdminNotificationsValidator()
    {
        RuleFor(j => j.EmailAddress)
            .NotNull()
            .NotEmpty()
            .When(x => x.Enabled)
            .WithMessage("Admin notification recipient missing or empty");
    }
}

public class NotificationsConfigValidator : AbstractValidator<NotificationsConfig>
{
    public NotificationsConfigValidator()
    {
        RuleFor(j => j.Email)
            .NotNull()
            .When(x => x.Enabled)
            .WithMessage("You must configure at least one notifications provider when notifications are enabled");
    }
}

public class NetworkEndpointConfigValidator<T> : AbstractValidator<T>
    where T : NetworkEndpointConfig
{
    public NetworkEndpointConfigValidator()
    {
        RuleFor(j => j.Host)
            .NotNull()
            .NotEmpty()
            .WithMessage("Host missing or empty");

        RuleFor(j => j.Port)
            .GreaterThan(0)
            .WithMessage("Invalid port number '{PropertyValue}'");
    }
}

public class AuthenticatedNetworkEndpointConfigValidator<T> : NetworkEndpointConfigValidator<T>
    where T : AuthenticatedNetworkEndpointConfig
{
}

public class PoolEndpointValidator : AbstractValidator<PoolEndpoint>
{
    public PoolEndpointValidator()
    {
        RuleFor(j => j.Difficulty)
            .GreaterThan(0)
            .WithMessage("Pool Endpoint: Difficulty missing or invalid");

        RuleFor(j => j.TlsPfxFile)
            .NotNull()
            .NotEmpty()
            .When(j => j.Tls)
            .WithMessage("Pool Endpoint: Tls enabled but neither TlsPemFile nor TlsPfxFile specified");

        RuleFor(j => j.TlsPfxFile)
            .Must(File.Exists)
            .When(j => j.Tls)
            .WithMessage(j => $"Pool Endpoint: {j.TlsPfxFile} does not exist");

        RuleFor(j => j.TlsPfxFile)
            .Must((j, h, c) =>
            {
                try
                {
                    using var tlsCert = X509CertificateLoader.LoadPkcs12FromFile(h, j.TlsPfxPassword);
                    return tlsCert.HasPrivateKey;
                }
                catch
                {
                    return false;
                }
            })
            .When(j => j.Tls)
            .WithMessage(j => $"Pool Endpoint: {j.TlsPfxFile} is not valid or does not include the private key and cannot be used");
        RuleFor(j => j.VarDiff)
            .SetValidator(new VarDiffConfigValidator())
            .When(x => x.VarDiff != null);
    }
}

public class ApiConfigValidator : AbstractValidator<ApiConfig>
{
    public ApiConfigValidator()
    {
        RuleFor(j => j.ListenAddress)
            .Must(address => ListenerAddressUtils.TryResolve(address,
                out _))
            .WithMessage(
                "API: listenAddress must be '*' or a valid IPv4/IPv6 address");

        RuleForEach(j => j.AdminIpWhitelist)
            .NotNull()
            .WithMessage(
                "API: adminIpWhitelist[{CollectionIndex}] must not be null")
            .Must(address => address == null ||
                IPAddress.TryParse(address, out _))
            .WithMessage(
                "API: adminIpWhitelist[{CollectionIndex}] contains invalid IP address '{PropertyValue}'");

        RuleForEach(j => j.MetricsIpWhitelist)
            .NotNull()
            .WithMessage(
                "API: metricsIpWhitelist[{CollectionIndex}] must not be null")
            .Must(address => address == null ||
                IPAddress.TryParse(address, out _))
            .WithMessage(
                "API: metricsIpWhitelist[{CollectionIndex}] contains invalid IP address '{PropertyValue}'");

        RuleFor(j => j.Port)
            .InclusiveBetween(1, ushort.MaxValue)
            .WithMessage("API: Invalid port number '{PropertyValue}'");

        RuleFor(j => j.AdminPort.Value)
            .InclusiveBetween(1, ushort.MaxValue)
            .When(j => j.AdminPort.HasValue)
            .WithMessage("API: Invalid adminPort number '{PropertyValue}'")
            .OverridePropertyName("adminPort");

        RuleFor(j => j.MetricsPort.Value)
            .InclusiveBetween(1, ushort.MaxValue)
            .When(j => j.MetricsPort.HasValue)
            .WithMessage("API: Invalid metricsPort number '{PropertyValue}'")
            .OverridePropertyName("metricsPort");

        RuleFor(j => j)
            .Must(j => !j.AdminPort.HasValue || j.AdminPort.Value != j.Port)
            .WithMessage("API: adminPort must differ from port when configured");

        RuleFor(j => j)
            .Must(j => !j.MetricsPort.HasValue || j.MetricsPort.Value != j.Port)
            .WithMessage("API: metricsPort must differ from port when configured");

        RuleFor(j => j)
            .Must(j => !j.AdminPort.HasValue || !j.MetricsPort.HasValue ||
                j.AdminPort.Value != j.MetricsPort.Value)
            .WithMessage("API: adminPort and metricsPort must differ when configured");
    }
}

public class VarDiffConfigValidator : AbstractValidator<VarDiffConfig>
{
    public VarDiffConfigValidator()
    {
        RuleFor(j => j.MaxDiff)
            .GreaterThanOrEqualTo(x => x.MinDiff)
            .When(x => x.MaxDiff.HasValue)
            .WithMessage("VarDiff: max value must be greater or equal min value");

        RuleFor(j => j.VariancePercent)
            .InclusiveBetween(1, 100)
            .WithMessage("VarDiff: variancePercent must be a percentage betwen 1 and 100");

        RuleFor(j => j.TargetTime)
            .GreaterThan(0)
            .WithMessage("VarDiff: targetTime invalid");

        RuleFor(j => j.RetargetTime)
            .GreaterThan(0)
            .WithMessage("VarDiff: retargetTime invalid");
    }
}

public class PoolConfigValidator : AbstractValidator<PoolConfig>
{
    public PoolConfigValidator(bool recoveryMode = false)
        : this(recoveryMode, null)
    {
    }

    internal PoolConfigValidator(bool recoveryMode,
        IReadOnlyCollection<ListenerAddressUtils.IPv4InterfaceSubnet> activeIPv4Subnets)
    {
        activeIPv4Subnets ??= recoveryMode
            ? Array.Empty<ListenerAddressUtils.IPv4InterfaceSubnet>()
            : ListenerAddressUtils.CaptureActiveIPv4Subnets();

        bool ShouldValidateStratumListeners(PoolConfig pool) =>
            !recoveryMode && pool.Enabled &&
            pool.EnableInternalStratum == true;

        RuleFor(j => j.Id)
            .NotNull()
            .NotEmpty()
            .WithMessage("Pool: id missing or empty");

        RuleFor(j => j.Coin)
            .NotNull()
            .When(_ => !recoveryMode)
            .WithMessage("Pool: Coin config missing or empty");

        RuleFor(j => j.Ports)
            .NotNull()
            .NotEmpty()
            .When(ShouldValidateStratumListeners)
            .WithMessage("Pool: Stratum port config missing or empty");

        RuleFor(j => j.Ports)
            .Must((pc, ports, ctx) =>
            {
                if(ports?.Keys.Any(port =>
                    port is < 1 or > ushort.MaxValue) == true)
                {
                    var invalidPort = ports.Keys.First(port =>
                        port is < 1 or > ushort.MaxValue);
                    ctx.MessageFormatter.AppendArgument("port", invalidPort);
                    return false;
                }

                return true;
            })
            .When(ShouldValidateStratumListeners)
            .WithMessage("Pool: Invalid stratum port number {port}");

        RuleFor(j => j.Ports)
            .Custom((ports, context) =>
            {
                if(ports == null)
                    return;

                var pool = context.InstanceToValidate;
                foreach(var (port, endpoint) in ports)
                {
                    if(endpoint == null)
                    {
                        context.AddFailure($"Ports[{port}]",
                            ListenerAddressUtils.FormatNullEndpointError(
                                pool.Id, port));
                        continue;
                    }

                    var address = endpoint.ListenAddress;
                    if(!ListenerAddressUtils.TryResolve(address,
                           out var resolvedAddress))
                    {
                        context.AddFailure($"Ports[{port}].ListenAddress",
                            $"Pool '{pool.Id}' Stratum port {port}: listenAddress must be '*' or a valid IPv4/IPv6 address (received '{address}')");
                        continue;
                    }

                    if(!ListenerAddressUtils.IsSuitableForListener(
                           resolvedAddress, activeIPv4Subnets, out var reason))
                    {
                        context.AddFailure($"Ports[{port}].ListenAddress",
                            $"Pool '{pool.Id}' Stratum port {port}: listenAddress '{address}' is unsuitable: {reason}");
                    }
                }
            })
            .When(ShouldValidateStratumListeners);

        RuleForEach(j => j.Ports.Values)
            .SetValidator(x => new PoolEndpointValidator())
            .When(x => x.Ports != null &&
                ShouldValidateStratumListeners(x));

        RuleFor(j => j.Address)
            .NotNull()
            .NotEmpty()
            .When(_ => !recoveryMode)
            .WithMessage("Pool: Wallet address missing or empty");

        RuleFor(j => j.Daemons)
            .NotNull()
            .NotEmpty()
            .When(_ => !recoveryMode)
            .WithMessage("Pool: Daemons missing or empty");

        RuleForEach(j => j.Daemons)
            .SetValidator(new AuthenticatedNetworkEndpointConfigValidator<DaemonEndpointConfig>())
            .When(_ => !recoveryMode);

        // Live pool, payout and administrative paths treat this object as a
        // required per-pool contract. Keep recovery deliberately narrower: it
        // neither starts those services nor consumes payout configuration.
        RuleFor(j => j.PaymentProcessing)
            .NotNull()
            .When(_ => !recoveryMode)
            .WithMessage(pool =>
                $"Pool '{(string.IsNullOrEmpty(pool.Id) ? "<unnamed>" : pool.Id)}': " +
                "paymentProcessing configuration missing; keep the object and " +
                "set enabled=false to disable payouts");

        RuleFor(j => j.PaymentProcessing.PpsShareRetentionDays)
            .InclusiveBetween(1, 365)
            .When(j => !recoveryMode && j.PaymentProcessing?.Enabled == true &&
                j.PaymentProcessing.PayoutScheme == PayoutScheme.PPS)
            .WithMessage(pool =>
                $"Pool '{pool.Id}': paymentProcessing.ppsShareRetentionDays must be between 1 and 365");
    }
}

public class ClusterConfigValidator : AbstractValidator<ClusterConfig>
{
    internal sealed record StratumListenerBinding(string PoolId, int Port,
        IPAddress Address)
    {
        internal string Endpoint =>
            $"{ListenerAddressUtils.FormatHost(Address)}:{Port}";
    }

    internal sealed record StratumListenerConflict(
        StratumListenerBinding First, StratumListenerBinding Second);

    public ClusterConfigValidator(bool recoveryMode = false)
    {
        var activeIPv4Subnets = recoveryMode
            ? Array.Empty<ListenerAddressUtils.IPv4InterfaceSubnet>()
            : ListenerAddressUtils.CaptureActiveIPv4Subnets();

        RuleFor(j => j.Logging)
            .SetValidator(new ClusterLoggingConfigValidator())
            .When(j => j.Logging != null);

        RuleFor(j => j.PaymentProcessing)
            .NotNull()
            .When(_ => !recoveryMode)
            .WithMessage("Cluster paymentProcessing configuration missing");

        RuleFor(j => j.PaymentProcessing.ShareAccountingRetentionDays)
            .InclusiveBetween(1, 3650)
            .When(j => j.PaymentProcessing != null)
            .WithMessage(
                "Cluster paymentProcessing.shareAccountingRetentionDays must be between 1 and 3650");

        RuleFor(j => j.Persistence)
            .NotNull()
            .When(x => !recoveryMode &&
                x.PaymentProcessing?.Enabled == true && x.ShareRelay == null);

        RuleFor(j => j.Persistence)
            .NotNull()
            .When(_ => recoveryMode)
            .WithMessage("Share recovery requires persistence configuration");

        RuleFor(j => j.Persistence.Postgres)
            .NotNull()
            .When(x => recoveryMode && x.Persistence != null)
            .WithMessage("Share recovery requires PostgreSQL persistence");

        RuleFor(j => j.Persistence.Postgres)
            .SetValidator(new RecoveryPostgresConfigValidator())
            .When(x => recoveryMode && x.Persistence?.Postgres != null);

        RuleFor(j => j.Pools)
            .NotNull()
            .NotEmpty();

        RuleForEach(j => j.Pools)
            .NotNull()
            .WithMessage("Pool configuration entry must not be null");

        RuleFor(j => j.InstanceId)
            .GreaterThan((byte) 0)
            .When(x => !recoveryMode && x.InstanceId.HasValue)
            .WithMessage("instanceId must either be omitted or be non-zero");

        RuleFor(j => j.Api)
            .SetValidator(new ApiConfigValidator())
            .When(j => !recoveryMode && j.Api?.Enabled == true);

        // ensure pool ids are unique
        RuleFor(j => j.Pools)
            .Must((pc, pools, ctx) =>
            {
                var ids = pools
                    .Where(pool => pool != null)
                    .GroupBy(x => x.Id)
                    .ToArray();

                if(ids.Any(id => id.Count() > 1))
                {
                    ctx.MessageFormatter.AppendArgument("poolId", ids.First(id => id.Count() > 1).Key);
                    return false;
                }

                return true;
            })
            .When(j => j.Pools != null)
            .WithMessage("Duplicate pool id '{poolId}'");

        // Reject only listener pairs that the operating system cannot bind
        // concurrently. Distinct specific addresses may safely reuse a port.
        RuleFor(j => j.Pools)
            .Custom((pools, context) =>
            {
                foreach(var conflict in FindStratumListenerConflicts(pools))
                {
                    context.AddFailure(nameof(ClusterConfig.Pools),
                        $"Stratum listener conflict: pool '{conflict.First.PoolId}' endpoint {conflict.First.Endpoint} overlaps pool '{conflict.Second.PoolId}' endpoint {conflict.Second.Endpoint}");
                }
            })
            .When(config => !recoveryMode && config.Pools != null);

        RuleForEach(j => j.Pools)
            .Where(pool => pool != null)
            .SetValidator(new PoolConfigValidator(recoveryMode,
                activeIPv4Subnets));
    }

    internal static IReadOnlyList<StratumListenerConflict>
        FindStratumListenerConflicts(
        IEnumerable<PoolConfig> pools)
    {
        var bindings = (pools ?? Enumerable.Empty<PoolConfig>())
            .Where(pool => pool != null && pool.Enabled &&
                pool.EnableInternalStratum == true &&
                pool.Ports?.Any() == true)
            .SelectMany(pool => pool.Ports.Select(entry =>
                (Pool: pool, Port: entry.Key, Endpoint: entry.Value)))
            .Select(item =>
            {
                if(item.Endpoint == null ||
                    !ListenerAddressUtils.TryResolve(
                        item.Endpoint.ListenAddress, out var address))
                    return null;

                return new StratumListenerBinding(item.Pool.Id,
                    item.Port, address);
            })
            // Null endpoints and malformed addresses are reported by
            // PoolConfigValidator. The conflict scan must never replace that
            // diagnostic with an exception or invent a default listener.
            .Where(binding => binding != null)
            .ToArray();
        var conflicts = new List<StratumListenerConflict>();

        foreach(var group in bindings.GroupBy(binding => binding.Port))
        {
            var candidates = group.ToArray();

            for(var first = 0; first < candidates.Length; first++)
            {
                for(var second = first + 1; second < candidates.Length;
                    second++)
                {
                    if(ListenerAddressUtils.Overlaps(
                        candidates[first].Address,
                        candidates[second].Address))
                    {
                        conflicts.Add(new StratumListenerConflict(
                            candidates[first], candidates[second]));
                    }
                }
            }
        }

        return conflicts;
    }
}

public class ClusterLoggingConfigValidator : AbstractValidator<ClusterLoggingConfig>
{
    public ClusterLoggingConfigValidator()
    {
        RuleFor(j => j.Level)
            .Must(IsValidLogLevel)
            .WithMessage(
                "Logging: level '{PropertyValue}' is invalid; use trace, debug, info/information, warn/warning, error, fatal, off/none, or omit it for info");
    }

    private static bool IsValidLogLevel(string level)
    {
        if(string.IsNullOrEmpty(level))
            return true;

        try
        {
            _ = NLog.LogLevel.FromString(level);
            return true;
        }
        catch(ArgumentException)
        {
            return false;
        }
    }
}

internal sealed class RecoveryPostgresConfigValidator :
    AbstractValidator<PostgresConfig>
{
    public RecoveryPostgresConfigValidator()
    {
        RuleFor(j => j.Host)
            .NotNull()
            .NotEmpty()
            .WithMessage("Share recovery PostgreSQL host missing or empty");

        RuleFor(j => j.Port)
            .InclusiveBetween(1, ushort.MaxValue)
            .WithMessage("Share recovery PostgreSQL port is invalid");

        RuleFor(j => j.Database)
            .NotNull()
            .NotEmpty()
            .WithMessage("Share recovery PostgreSQL database missing or empty");

        RuleFor(j => j.User)
            .NotNull()
            .NotEmpty()
            .WithMessage("Share recovery PostgreSQL user missing or empty");
    }
}

#endregion // Validators

public partial class ClusterLoggingConfig
{
}

public partial class VarDiffConfig
{
}

public partial class PoolShareBasedBanningConfig
{
}

public partial class PoolPaymentProcessingConfig
{
}

public partial class ClusterPaymentProcessingConfig
{
}

public partial class PersistenceConfig
{
}

public partial class NetworkEndpointConfig
{
}

public partial class AuthenticatedNetworkEndpointConfig
{
}

public partial class EmailSenderConfig
{
}

public partial class AdminNotifications
{
}

public partial class NotificationsConfig
{
}

public partial class ApiConfig
{
}

public partial class PoolConfig
{
}

public partial class ClusterConfig
{
    public void Validate(bool recoveryMode = false)
    {
        var validator = new ClusterConfigValidator(recoveryMode);
        var result = validator.Validate(this);

        if(!result.IsValid)
            throw new ValidationException(result.Errors);
    }
}
