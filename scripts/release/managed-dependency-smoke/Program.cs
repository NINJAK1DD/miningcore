using NBitcoin.Zcash;
using System.Runtime.InteropServices;
using System.Reflection;

Console.WriteLine($"Selected runtime: {RuntimeInformation.FrameworkDescription} at {RuntimeEnvironment.GetRuntimeDirectory()}");
if(Environment.Version.Major != 10 || Environment.Version.Minor != 0 || Environment.Version.Build < 14)
    throw new InvalidOperationException("Release validation requires the supported .NET 10 runtime at 10.0.14 or later servicing level");

if(args.Length != 2 || !Version.TryParse(args[0], out var expectedPkcs) ||
    (args[1] != "package" && args[1] != "framework"))
    throw new InvalidOperationException("Expected the reviewed PKCS version and package/framework binding mode");

var pkcs = Assembly.Load("System.Security.Cryptography.Pkcs");
var informationalVersion = pkcs.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
if(!Version.TryParse(informationalVersion?.Split('+')[0], out var actualPkcs) ||
    actualPkcs.Major != 10 || actualPkcs.Minor != 0 || actualPkcs < expectedPkcs || actualPkcs.Build < 13)
    throw new InvalidOperationException($"Loaded PKCS '{informationalVersion}' does not meet the reviewed version {expectedPkcs}");

var pkcsDirectory = Path.GetDirectoryName(pkcs.Location)!;
if(args[1] == "package")
{
    if(actualPkcs != expectedPkcs || !string.Equals(Path.GetFullPath(pkcsDirectory),
           Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.Ordinal))
        throw new InvalidOperationException("PKCS package binding must load the exact published version from the application directory");
}
else if(new DirectoryInfo(pkcsDirectory).Parent?.Name != "Microsoft.AspNetCore.App")
    throw new InvalidOperationException("Framework-pruned PKCS must load from Microsoft.AspNetCore.App");

Console.WriteLine($"Selected PKCS: {informationalVersion} at {pkcs.Location} ({args[1]} binding; reference {expectedPkcs})");

// NBitcoin.Zcash 3.0.0 BLAKE2bWriter with a 16-byte zero personalization and no payload.
const string expectedHash = "a8e32ff1cd45abfa87778f7747dfe5d1a1da9960b02eabe8b243e526c051570e";

using var writer = new BLAKE2bWriter(new byte[16]);
var hash = writer.GetHash().ToString();

if(hash != expectedHash)
    throw new InvalidOperationException($"NBitcoin.Zcash BLAKE2b produced '{hash}', expected '{expectedHash}'");

Console.WriteLine($"NBitcoin.Zcash BLAKE2b runtime binding succeeded: {hash}");
