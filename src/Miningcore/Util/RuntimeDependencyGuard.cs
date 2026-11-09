using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace Miningcore.Util;

// Also compiled into the release smoke: startup and release acceptance share one policy.
internal static class RuntimeDependencyGuard
{
    internal static readonly Version RuntimeMinimum = ReadMinimum("MiningcoreRuntimeMinimum");
    internal static readonly Version PkcsSecurityMinimum = ReadMinimum("MiningcorePkcsSecurityMinimum");

    private static Version ReadMinimum(string name)
    {
        using var stream = typeof(RuntimeDependencyGuard).Assembly.GetManifestResourceStream(
            "Miningcore.RuntimeDependencyPolicy.props") ?? throw new InvalidOperationException("Runtime policy missing");
        return Version.Parse(XDocument.Load(stream).Descendants(name).Single().Value);
    }

    internal static Version InformationalVersion(Assembly assembly)
    {
        var value = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if(!Version.TryParse(value?.Split('+')[0], out var version))
            throw new InvalidOperationException($"Invalid version for {assembly.GetName().Name}: {value}");
        return version;
    }

    internal static void RequireVersion(string name, Version actual, Version minimum)
    {
        if(actual.Major != minimum.Major || actual.Minor != minimum.Minor || actual < minimum)
            throw new InvalidOperationException($"{name} {actual} requires {minimum} or later servicing in the same major/minor version");
    }

    internal static bool SameDirectory(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static Assembly Validate(TextWriter output)
    {
        RequireVersion("Microsoft.NETCore.App", Environment.Version, RuntimeMinimum);
        output.WriteLine($"Selected Microsoft.NETCore.App: {Environment.Version} at {RuntimeEnvironment.GetRuntimeDirectory()}");
        var aspnet = Assembly.Load("Microsoft.AspNetCore.Http.Abstractions");
        RequireVersion("Microsoft.AspNetCore.App", InformationalVersion(aspnet), RuntimeMinimum);
        var aspnetDirectory = Path.GetDirectoryName(aspnet.Location)!;
        if(new DirectoryInfo(aspnetDirectory).Parent?.Name != "Microsoft.AspNetCore.App")
            throw new InvalidOperationException("ASP.NET evidence must come from its selected shared framework");
        output.WriteLine($"Selected Microsoft.AspNetCore.App: {InformationalVersion(aspnet)} at {aspnet.Location}");
        var pkcs = Assembly.Load("System.Security.Cryptography.Pkcs");
        RequireVersion("PKCS", InformationalVersion(pkcs), PkcsSecurityMinimum);
        output.WriteLine($"Selected PKCS: {InformationalVersion(pkcs)} at {pkcs.Location}");
        return pkcs;
    }

    internal static void ValidateBinding(Version actual, Version expected, string mode,
        string pkcsDirectory, string appDirectory, string aspnetDirectory)
    {
        RequireVersion("PKCS", actual, expected);
        var framework = SameDirectory(pkcsDirectory, aspnetDirectory);
        if(mode == "package" && ((actual == expected && SameDirectory(pkcsDirectory, appDirectory)) ||
            (actual >= expected && framework)))
            return;
        if(mode == "framework" && framework)
            return;
        throw new InvalidOperationException("PKCS must load the reviewed application copy or an eligible selected ASP.NET shared-framework copy");
    }
}
