using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Pearl;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

[CollectionDefinition("Pearl proof worker processes", DisableParallelization = true)]
public class PearlProofWorkerCollection { }

[Collection("Pearl proof worker processes")]
public class PearlProofVerifierTests
{
    private sealed class Helper : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "miningcore-pearl-" + Guid.NewGuid().ToString("N"));
        internal string Path { get; }

        internal Helper(string script)
        {
            if(!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException();
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "worker");
            File.WriteAllText(Path, "#!/bin/sh\n" + script + "\n");
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose() => Directory.Delete(directory, true);
    }

    private static (byte[] Header, BigInteger Target, byte[] Proof) Input()
    {
        using var stream = typeof(PearlProofVerifierTests).Assembly.GetManifestResourceStream(
            "Miningcore.Tests.Fixtures.Pearl.srb-proof.json");
        using var reader = new StreamReader(stream);
        var data = JObject.Parse(reader.ReadToEnd());
        return (Convert.FromHexString(data.Value<string>("header")),
            new BigInteger(Convert.FromHexString(data.Value<string>("target")), true, true),
            PearlPlainProofTests.RealProof());
    }

    [PearlLinuxFact]
    public async Task VersionMismatchFailsClosed()
    {
        using var helper = new Helper("printf 'wrong-worker\\n'");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PearlProofVerifier(helper.Path).CheckVersionAsync(CancellationToken.None));
    }

    [PearlLinuxFact]
    public async Task OversizedVersionOutputIsRejected()
    {
        using var helper = new Helper("exec /usr/bin/head -c 300 /dev/zero");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PearlProofVerifier(helper.Path).CheckVersionAsync(CancellationToken.None));
    }

    [PearlLinuxFact]
    public async Task GlobalCapacityHasNoQueueAndCancellationKillsAndReapsWorkers()
    {
        using var helper = new Helper("exec /bin/sleep 60");
        var verifier = new PearlProofVerifier(helper.Path);
        var (header, target, proof) = Input();
        using var cancellation = new CancellationTokenSource();
        var elapsed = Stopwatch.StartNew();
        var first = verifier.VerifyAsync(header, target, proof, false, cancellation.Token);
        var second = verifier.VerifyAsync(header, target, proof, false, cancellation.Token);
        try
        {
            // A second service instance still shares the same process-wide budget.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new PearlProofVerifier(helper.Path).VerifyAsync(header, target, proof, false, CancellationToken.None));
        }
        finally
        {
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        // The slots must be usable after cancellation, rather than permanently leaked.
        using var next = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifyAsync(header, target, proof, false, next.Token));
    }
}

internal sealed class PearlLinuxFactAttribute : FactAttribute
{
    public PearlLinuxFactAttribute()
    {
        if(!OperatingSystem.IsLinux())
            Skip = "Pearl worker currently supports Linux only";
    }
}
