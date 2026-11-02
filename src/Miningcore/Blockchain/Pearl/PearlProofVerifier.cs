using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;

namespace Miningcore.Blockchain.Pearl;

/// <summary>
/// Bounded process boundary for the pinned Linux x64 proof worker.
/// No queue: callers must apply transport/session rate limits before invoking this service.
/// </summary>
internal sealed class PearlProofVerifier
{
    internal const string ExpectedVersion =
        "miningcore-pearl-worker 2 d7c517d267f10f5bcbbdc366f3e603942f9a5ab5 linux-x64";
    private readonly string executable;
    private static readonly SemaphoreSlim verificationSlots = new(2, 2);
    private static readonly SemaphoreSlim provingSlots = new(1, 1);

    internal static int ActiveVerifiers => 2 - verificationSlots.CurrentCount;
    internal static int ActiveProvers => 1 - provingSlots.CurrentCount;

    internal sealed record Result(byte[] Header, byte[] PublicData, bool IsBlockCandidate, byte[] Certificate);

    internal PearlProofVerifier(string executable)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        if(!Path.IsPathFullyQualified(executable))
            throw new ArgumentException("Proof worker path must be absolute", nameof(executable));
        this.executable = executable;
    }

    internal async Task<Result> VerifyAsync(byte[] header, BigInteger shareTarget, byte[] proof,
        bool prove, CancellationToken ct)
    {
        if(header == null || header.Length != PearlConsensus.IncompleteHeaderSize)
            throw new ArgumentException("Invalid incomplete header", nameof(header));
        if(shareTarget <= 0 || shareTarget > PearlConsensus.MaximumTarget)
            throw new ArgumentOutOfRangeException(nameof(shareTarget));
        ArgumentNullException.ThrowIfNull(proof);
        var dimensions = PearlPlainProof.Inspect(proof);
        if(dimensions.Rank < 128)
            throw new InvalidDataException("Current Pearl work requires rank >= 128");
        PearlConsensus.ScaleTarget(shareTarget, dimensions.RankAdjustedWorkFactor, false);

        var slots = prove ? provingSlots : verificationSlots;
        if(!await slots.WaitAsync(0, ct))
            throw new InvalidOperationException("Pearl proof workers are busy");

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(prove ? TimeSpan.FromSeconds(180) : TimeSpan.FromSeconds(5));
            var fixedInput = new byte[124];
            Encoding.ASCII.GetBytes("PRLJOB02", fixedInput);
            fixedInput[8] = prove ? (byte) 1 : (byte) 0;
            fixedInput[9] = 3;
            header.CopyTo(fixedInput, 12);
            var targetBytes = shareTarget.ToByteArray(isUnsigned: true, isBigEndian: true);
            targetBytes.CopyTo(fixedInput, 120 - targetBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInput.AsSpan(120), (uint) proof.Length);

            using var process = new Process { StartInfo = StartInfo() };
            Task lifetime;
            try {lifetime = await StartOwnedAsync(process);}
            catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {throw new PearlWorkerException("Pearl worker could not start", ex, PearlWorkerFailure.Start);}
            PearlWorkerException failure = null;
            try
            {
                var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, 60180, deadline.Token);
                var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, 4096, deadline.Token);
                var writeTask = WriteAsync(process, fixedInput, proof, deadline.Token);
                await Task.WhenAll(outputTask, errorTask, writeTask, process.WaitForExitAsync(deadline.Token));
                var response = await outputTask;
                if(process.ExitCode == 1 && response.AsSpan().SequenceEqual("PRLRES02\x01\0\0\0"u8))
                    throw new PearlInvalidProofException();
                if(process.ExitCode != 0)
                    throw new PearlWorkerException("Pearl worker failed", failure: process.ExitCode == 2 &&
                        response.AsSpan().SequenceEqual("PRLRES02\x02\0\0\0"u8) ? PearlWorkerFailure.Native : PearlWorkerFailure.Exit);
                if(response.Length < 180 || !response.AsSpan(0, 8).SequenceEqual("PRLRES02"u8) ||
                    response[8] != 0 || response[9] > 1 || response[10] != 0 || response[11] != 0)
                    throw new PearlWorkerException("Pearl worker failed or returned an invalid response");

                var size = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(176));
                if(size > PearlConsensus.MaximumProofSize || response.Length != 180 + size ||
                    prove && (size == 0 || response[9] != 1) || !prove && size != 0)
                    throw new PearlWorkerException("Invalid Pearl worker response");
                var publicData = response[12..176];
                if(BinaryPrimitives.ReadUInt32LittleEndian(publicData) != dimensions.K ||
                    BinaryPrimitives.ReadUInt16LittleEndian(publicData.AsSpan(4)) != dimensions.Rank ||
                    BinaryPrimitives.ReadUInt32LittleEndian(publicData.AsSpan(148)) != dimensions.M ||
                    BinaryPrimitives.ReadUInt32LittleEndian(publicData.AsSpan(152)) != dimensions.N)
                    throw new PearlWorkerException("Pearl worker dimensions do not match the proof");
                var jackpot = new BigInteger(publicData.AsSpan(116, 32), isUnsigned: true, isBigEndian: false);
                if(jackpot > PearlConsensus.ScaleTarget(shareTarget, dimensions.RankAdjustedWorkFactor, false))
                    throw new PearlWorkerException("Pearl worker returned a low-difficulty proof");
                var networkTarget = PearlConsensus.DecodeTarget(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(72)));
                var candidate = jackpot <= PearlConsensus.ScaleTarget(networkTarget, dimensions.RankAdjustedWorkFactor, true);
                if(candidate != (response[9] == 1))
                    throw new PearlWorkerException("Pearl worker candidate classification mismatch");
                return new Result(header.ToArray(), publicData, candidate, response[180..]);
            }
            catch(PearlWorkerException ex) { failure = ex; throw; }
            catch(OperationCanceledException ex) when(!ct.IsCancellationRequested)
            {
                throw failure = new PearlWorkerException("Pearl worker exceeded its deadline", ex, PearlWorkerFailure.Timeout);
            }
            catch(InvalidDataException ex)
            {
                throw failure = new PearlWorkerException("Pearl worker exceeded its response limits", ex, PearlWorkerFailure.OutputLimit);
            }
            catch(IOException ex)
            {
                throw failure = new PearlWorkerException("Pearl worker communication failed", ex, PearlWorkerFailure.Pipe);
            }
            finally
            {
                // Cancellation alone cannot stop native work. Kill and reap before releasing capacity.
                try { await StopAsync(process); }
                finally { await lifetime; }
                if(failure != null) failure.ExitCode = process.ExitCode;
            }
        }
        catch(PearlWorkerException ex) { ex.Phase = prove ? PearlProofPhase.Prove : PearlProofPhase.Verify; throw; }
        finally { slots.Release(); }
    }

    internal async Task CheckVersionAsync(CancellationToken ct)
    {
        if(OperatingSystem.IsLinux())
            PearlWorkerTrust.Validate(executable);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = StartInfo() };
        process.StartInfo.ArgumentList.Add("--version");
        var lifetime = await StartOwnedAsync(process);
        try
        {
            process.StandardInput.Close();
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, 256, deadline.Token);
            var error = ReadBoundedAsync(process.StandardError.BaseStream, 4096, deadline.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            if(process.ExitCode != 0 || Encoding.UTF8.GetString(await output).TrimEnd('\r', '\n') != ExpectedVersion)
                throw new InvalidDataException("Pearl proof worker version mismatch");
        }
        finally
        {
            try { await StopAsync(process); }
            finally { await lifetime; }
        }
    }

    private static Task<Task> StartOwnedAsync(Process process)
    {
        // Linux PR_SET_PDEATHSIG follows the spawning THREAD. A thread-pool
        // thread may retire during proving even while Miningcore stays alive.
        // Keep the launch thread until reaping; process death still kills it and
        // therefore the child. The existing worker slots bound these threads.
        var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                if(!process.Start()) throw new InvalidOperationException("Pearl worker did not start");
                started.SetResult(exited.Task);
                process.WaitForExit();
            }
            catch(Exception ex)
            {
                if(!started.TrySetException(ex)) exited.TrySetException(ex);
            }
            finally { exited.TrySetResult(); }
        }) {IsBackground = true, Name = "Pearl worker lifetime"}.Start();
        return started.Task;
    }

    private static async Task StopAsync(Process process)
    {
        try {if(!process.HasExited) process.Kill(entireProcessTree: true);}
        catch(InvalidOperationException) when(process.HasExited) { }
        await process.WaitForExitAsync(CancellationToken.None);
    }

    private ProcessStartInfo StartInfo()
    {
        var result = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true,
        };
        result.Environment.Clear();
        result.Environment["RAYON_NUM_THREADS"] = "4";
        result.Environment["MALLOC_ARENA_MAX"] = "2";
        result.Environment["LANG"] = "C";
        return result;
    }

    private static async Task WriteAsync(Process process, byte[] header, byte[] proof, CancellationToken ct)
    {
        await process.StandardInput.BaseStream.WriteAsync(header, ct);
        await process.StandardInput.BaseStream.WriteAsync(proof, ct);
        process.StandardInput.Close();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken ct)
    {
        using var result = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while((count = await stream.ReadAsync(buffer.AsMemory(0,
            Math.Min(buffer.Length, maximum - (int) result.Length + 1)), ct)) != 0)
        {
            if(result.Length + count > maximum)
                throw new InvalidDataException("Pearl worker output exceeded its bound");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
}

// Deliberately distinct from malformed client input: infrastructure failures must not ban miners.
internal enum PearlWorkerFailure { Start, Native, Exit, Response, Timeout, OutputLimit, Pipe }
internal sealed class PearlWorkerException(string message, Exception inner = null,
    PearlWorkerFailure failure = PearlWorkerFailure.Response) : Exception(message, inner)
{
    internal PearlWorkerFailure Failure { get; } = failure;
    internal PearlProofPhase Phase { get; set; }
    internal int? ExitCode { get; set; }
    // Only finite enums and an OS integer are exposed; never stderr, input or paths.
    internal void WriteDiagnostic(NLog.ILogger logger) => logger.Warn(
        "Pearl proof worker failure: phase={0}, class={1}, exit={2}", Phase, Failure, ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable");
}
internal sealed class PearlInvalidProofException() : Exception("Invalid Pearl proof");
