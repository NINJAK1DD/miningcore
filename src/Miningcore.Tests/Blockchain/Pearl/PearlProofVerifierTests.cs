using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Pearl;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

[CollectionDefinition("Pearl proof worker processes", DisableParallelization = true)]
public class PearlProofWorkerCollection { }

[Collection("Pearl proof worker processes")]
public class PearlProofVerifierTests
{
    [Theory]
    [InlineData(0, 1000, true)]
    [InlineData(1000, 1000, true)]
    [InlineData(1001, 1000, false)]
    [InlineData(1001, 0, false)]
    public void OnlyRootOrServiceUidIsTrusted(int owner, int service, bool expected) =>
        Assert.Equal(expected, PearlWorkerTrust.TrustedOwner((uint) owner, (uint) service));

    [PearlLinuxFact]
    public async Task LaunchingThreadExitDoesNotKillAnActiveWorker()
    {
        var (header, target, proof) = Input();
        using var helper = new Helper("unused");
        var ready = helper.Path + ".ready";
        File.WriteAllText(helper.Path, "#!/usr/bin/python3\nimport ctypes,sys,time\n" +
            "assert ctypes.CDLL(None).prctl(1,9,0,0,0)==0\n" +
            "open(" + JsonConvert.SerializeObject(ready) + ", 'w').close()\n" +
            "sys.stdin.buffer.read()\ntime.sleep(0.5)\nsys.stdout.buffer.write(b'PRLRES02\\x02\\0\\0\\0')\nsys.exit(2)\n");
        var launched = new TaskCompletionSource<Task<PearlProofVerifier.Result>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var work = new PearlProofVerifier(helper.Path).VerifyAsync(header, target, proof, false, CancellationToken.None);
                Assert.True(SpinWait.SpinUntil(() => File.Exists(ready), TimeSpan.FromSeconds(5)));
                launched.SetResult(work);
            }
            catch(Exception ex) { launched.TrySetException(ex); }
        }) { IsBackground = true };
        thread.Start();
        var pending = await launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        var error = await Assert.ThrowsAsync<PearlWorkerException>(() => pending);
        Assert.Equal(PearlWorkerFailure.Native, error.Failure);
        Assert.Equal(2, error.ExitCode); // The helper completed; it was not killed with its launch thread.
    }

    [PearlLinuxFact]
    public async Task WorkerFailuresExposeOnlyBoundedDiagnosticFields()
    {
        var (header, target, proof) = Input();
        foreach(var (script, category, code) in new[]
        {
            ("/bin/cat >/dev/null; printf 'private-secret' >&2; printf 'PRLRES02\\002\\000\\000\\000'; exit 2", PearlWorkerFailure.Native, 2),
            ("/bin/cat >/dev/null; kill -KILL $$", PearlWorkerFailure.Exit, 137),
            ("/bin/cat >/dev/null; printf 'malformed-private-response'", PearlWorkerFailure.Response, 0),
            ("/bin/cat >/dev/null; exec /bin/sleep 60", PearlWorkerFailure.Timeout, 137)
        })
        {
            using var helper = new Helper(script);
            var error = await Assert.ThrowsAsync<PearlWorkerException>(() => new PearlProofVerifier(helper.Path)
                .VerifyAsync(header, target, proof, false, CancellationToken.None));
            Assert.Equal(category, error.Failure);
            Assert.Equal(code, error.ExitCode);
            Assert.Equal(PearlProofPhase.Verify, error.Phase);
            using var log = new NLog.LogFactory();
            var targetLog = new NLog.Targets.MemoryTarget {Layout = "${message}${exception:format=tostring}"};
            var configuration = new NLog.Config.LoggingConfiguration();
            configuration.AddRuleForAllLevels(targetLog); log.Configuration = configuration;
            error.WriteDiagnostic(log.GetLogger("test"));
            var entry = Assert.Single(targetLog.Logs);
            Assert.Contains("phase=Verify", entry);
            Assert.Contains("class=" + category, entry);
            Assert.Contains("exit=" + code, entry);
            Assert.DoesNotContain("private", entry);
            Assert.DoesNotContain(helper.Path, entry);
        }
        var start = await Assert.ThrowsAsync<PearlWorkerException>(() => new PearlProofVerifier("/nonexistent/pearl-worker")
            .VerifyAsync(header, target, proof, true, CancellationToken.None));
        Assert.Equal(PearlProofPhase.Prove, start.Phase);
        Assert.Equal(PearlWorkerFailure.Start, start.Failure);
        Assert.Null(start.ExitCode);
    }

    [PearlOwnershipFact]
    public async Task ForeignOwnedExecutableAndAncestorAreRejectedBeforeExecution()
    {
        using var helper = new Helper("printf '" + PearlProofVerifier.ExpectedVersion + "\\n'");
        var verifier = new PearlProofVerifier(helper.Path);
        await verifier.CheckVersionAsync(CancellationToken.None);
        async Task Owner(string path, string uid)
        {
            var info = new ProcessStartInfo("/usr/bin/chown") {UseShellExecute = false};
            info.ArgumentList.Add(uid); info.ArgumentList.Add(path);
            using var process = Process.Start(info); await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        }
        var directory = Path.GetDirectoryName(helper.Path);
        try
        {
            await Owner(helper.Path, "65534");
            await Assert.ThrowsAsync<InvalidDataException>(() => verifier.CheckVersionAsync(CancellationToken.None));
            await Owner(helper.Path, "0");
            await Owner(directory, "65534");
            await Assert.ThrowsAsync<InvalidDataException>(() => verifier.CheckVersionAsync(CancellationToken.None));
        }
        finally { await Owner(helper.Path, "0"); await Owner(directory, "0"); }
    }
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
    public async Task GroupWritableExecutableOrDirectoryAndSymlinkAreRejected()
    {
        if(!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var helper = new Helper("printf 'unused\\n'");
        var fileMode = File.GetUnixFileMode(helper.Path);
        File.SetUnixFileMode(helper.Path, fileMode | UnixFileMode.GroupWrite);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PearlProofVerifier(helper.Path).CheckVersionAsync(CancellationToken.None));
        File.SetUnixFileMode(helper.Path, fileMode);
        var directory = System.IO.Path.GetDirectoryName(helper.Path);
        var directoryMode = File.GetUnixFileMode(directory);
        File.SetUnixFileMode(directory, directoryMode | UnixFileMode.OtherWrite);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PearlProofVerifier(helper.Path).CheckVersionAsync(CancellationToken.None));
        File.SetUnixFileMode(directory, directoryMode);
        var link = helper.Path + "-link";
        File.CreateSymbolicLink(link, helper.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PearlProofVerifier(link).CheckVersionAsync(CancellationToken.None));
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
    public async Task OnlyExplicitRejectionCanBeAttributedToTheMiner()
    {
        var (header, target, proof) = Input();
        await Assert.ThrowsAsync<PearlWorkerException>(() =>
            new PearlProofVerifier("/nonexistent/pearl-worker").VerifyAsync(header, target, proof, false, CancellationToken.None));
        using(var rejected = new Helper("/bin/cat >/dev/null; printf 'PRLRES02\\001\\000\\000\\000'; exit 1"))
            await Assert.ThrowsAsync<PearlInvalidProofException>(() =>
                new PearlProofVerifier(rejected.Path).VerifyAsync(header, target, proof, false, CancellationToken.None));
        foreach(var script in new[]
        {
            "/bin/cat >/dev/null; printf 'PRLRES02\\002\\000\\000\\000'; exit 2",
            "/bin/cat >/dev/null; kill -KILL $$",
            "exit 2", // Broken stdin while the caller writes is also infrastructure failure.
            "/bin/cat >/dev/null; printf 'PRLRES02\\001\\000\\000\\000'; exit 2",
            "/bin/cat >/dev/null; printf 'PRLRES02\\001\\000\\000\\000extra'; exit 1",
            "/bin/cat >/dev/null; /usr/bin/head -c 61000 /dev/zero"
        })
        {
            using var failed = new Helper(script);
            await Assert.ThrowsAsync<PearlWorkerException>(() =>
                new PearlProofVerifier(failed.Path).VerifyAsync(header, target, proof, false, CancellationToken.None));
        }
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

internal sealed class PearlOwnershipFactAttribute : FactAttribute
{
    public PearlOwnershipFactAttribute()
    {
        if(!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("MININGCORE_TEST_PEARL_OWNERSHIP") != "1")
            Skip = "Requires the opt-in disposable root ownership lab";
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
