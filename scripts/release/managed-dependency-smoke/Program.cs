using NBitcoin.Zcash;
using System.Reflection;
using Miningcore.Util;

if(args.Length != 2 || !Version.TryParse(args[0], out var expectedPkcs) ||
    (args[1] != "package" && args[1] != "framework"))
    throw new InvalidOperationException("Expected the reviewed PKCS version and package/framework binding mode");

var pkcs = RuntimeDependencyGuard.Validate(Console.Out);
var aspnet = Assembly.Load("Microsoft.AspNetCore.Http.Abstractions");
RuntimeDependencyGuard.ValidateBinding(RuntimeDependencyGuard.InformationalVersion(pkcs), expectedPkcs,
    args[1], Path.GetDirectoryName(pkcs.Location)!, AppContext.BaseDirectory,
    Path.GetDirectoryName(aspnet.Location)!);
Console.WriteLine($"PKCS binding accepted: {args[1]}; reviewed reference {expectedPkcs}");

// NBitcoin.Zcash 3.0.0 BLAKE2bWriter with a 16-byte zero personalization and no payload.
const string expectedHash = "a8e32ff1cd45abfa87778f7747dfe5d1a1da9960b02eabe8b243e526c051570e";

using var writer = new BLAKE2bWriter(new byte[16]);
var hash = writer.GetHash().ToString();

if(hash != expectedHash)
    throw new InvalidOperationException($"NBitcoin.Zcash BLAKE2b produced '{hash}', expected '{expectedHash}'");

Console.WriteLine($"NBitcoin.Zcash BLAKE2b runtime binding succeeded: {hash}");
