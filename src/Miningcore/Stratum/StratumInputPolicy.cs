using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;

namespace Miningcore.Stratum;

/// <summary>Internal, explicitly selected transport profiles. Ordinary Stratum keeps its original limits.</summary>
internal sealed class StratumInputPolicy
{
    internal static readonly StratumInputPolicy Standard = new(false);
    internal static readonly StratumInputPolicy Pearl = CreatePearl();
    private readonly SemaphoreSlim connections;
    private readonly object sync = new();
    private readonly Dictionary<IPAddress, Usage> peers = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int maximumFrames;
    private readonly int maximumFramesPerIp;
    private readonly long maximumBytes;
    private long bufferedBytes;
    private int uploads;
    private sealed class Usage { internal long Bytes; internal int Uploads; }
    internal bool ProofSized { get; }
    internal int MaximumLength => ProofSized ? 4 * ((8 * 1024 * 1024 + 2) / 3) + 4096 : 0x8000;
    internal int ReadSize => ProofSized ? 16384 : 0x8001;
    private StratumInputPolicy(bool proofSized, int maximumConnections = 1024, int maximumFrames = 256,
        int maximumBufferMiB = 256, int maximumFramesPerIp = 4)
    {
        ProofSized = proofSized;
        if(proofSized)
        {
            connections = new SemaphoreSlim(maximumConnections, maximumConnections);
            this.maximumFrames = Math.Min(maximumFrames, maximumConnections);
            this.maximumFramesPerIp = maximumFramesPerIp;
            maximumBytes = maximumBufferMiB * 1024L * 1024;
        }
    }
    internal static StratumInputPolicy CreatePearl(int maximumConnections = 1024, int maximumFrames = 256,
        int maximumBufferMiB = 256, int maximumFramesPerIp = 4)
    {
        if(maximumConnections is < 16 or > 4096 || maximumFrames is < 1 or > 4096 ||
            maximumBufferMiB is < 64 or > 1024 || maximumFramesPerIp is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(maximumConnections));
        return new StratumInputPolicy(true, maximumConnections, maximumFrames, maximumBufferMiB, maximumFramesPerIp);
    }

    internal IDisposable Acquire()
    {
        if(!ProofSized)
            return null;
        if(!connections.Wait(0))
            throw new IOException("Proof transport connection capacity reached");
        return new Lease(connections);
    }

    internal ProofBuffer CreateBuffer() => ProofSized ? new(this) : null;

    // Upload slots end at LF; byte reservations remain until the consumer has
    // finished verification/persistence. No allocation failure disconnects an
    // otherwise healthy sender: the caller waits under its frame deadline.
    internal sealed class ProofBuffer(StratumInputPolicy policy) : IDisposable
    {
        private long bytes;
        private IPAddress peer;
        private bool uploading;
        private bool disposed;
        private long Charged => Math.Max(0, bytes - 32768);

        internal async Task AddAsync(int count, bool largeFrame, IPAddress address, CancellationToken ct, bool authorized = true)
        {
            if(count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            while(true)
            {
                ct.ThrowIfCancellationRequested();
                Task wait;
                lock(policy.sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if(!authorized && bytes + count > 32768)
                        throw new InvalidDataException("Authorization required for proof buffer capacity");
                    var extra = Math.Max(0, bytes + count - 32768) - Charged;
                    if(peer != null && !peer.Equals(address)) throw new InvalidDataException("Proof identity changed");
                    policy.peers.TryGetValue(address, out var usage);
                    var slot = largeFrame && !uploading;
                    if(policy.bufferedBytes + extra <= policy.maximumBytes &&
                        (usage?.Bytes ?? 0) + extra <= policy.maximumBytes / 4 &&
                        (!slot || policy.uploads < policy.maximumFrames && (usage?.Uploads ?? 0) < policy.maximumFramesPerIp))
                    {
                        bytes += count;
                        if(extra != 0 || slot)
                        {
                            peer = address;
                            if(usage == null) policy.peers.Add(address, usage = new());
                            usage.Bytes += extra;
                            policy.bufferedBytes += extra;
                            if(slot) { uploading = true; usage.Uploads++; policy.uploads++; }
                        }
                        return;
                    }
                    wait = policy.changed.Task;
                }
                await wait.WaitAsync(ct);
            }
        }

        internal void EndUpload()
        {
            lock(policy.sync)
            {
                if(disposed) return;
                if(uploading) { policy.uploads--; policy.peers[peer].Uploads--; uploading = false; }
                Signal();
            }
        }

        internal void Consume(long count)
        {
            lock(policy.sync)
            {
                if(disposed) return;
                if(count < 0 || count > bytes) throw new InvalidOperationException("Invalid proof buffer consumption");
                var before = Charged;
                bytes -= count;
                var released = before - Charged;
                policy.bufferedBytes -= released;
                if(released != 0) policy.peers[peer].Bytes -= released;
                Signal();
            }
        }

        private void Signal()
        {
            if(peer != null && policy.peers.TryGetValue(peer, out var usage) && usage.Bytes == 0 && usage.Uploads == 0)
                policy.peers.Remove(peer);
            var previous = policy.changed;
            policy.changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }

        public void Dispose()
        {
            lock(policy.sync)
            {
                if(disposed) return;
                Consume(bytes);
                EndUpload();
                disposed = true;
                Signal();
            }
        }
    }

    internal static SequencePosition? FindLine(ReadOnlySequence<byte> buffer, ref long examined)
    {
        var position = buffer.Slice(examined).PositionOf((byte) '\n');
        examined = position == null ? buffer.Length : 0;
        return position;
    }

    private sealed class Lease(SemaphoreSlim budget) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed, 1) == 0)
                budget.Release();
        }
    }

    internal void ValidateJson(ReadOnlySequence<byte> input)
    {
        if(!ProofSized)
            return;
        var reader = new Utf8JsonReader(input, new JsonReaderOptions { MaxDepth = 8 });
        var objects = new Stack<HashSet<string>>();
        var tokens = 0;
        while(reader.Read())
        {
            if(++tokens > 128)
                throw new InvalidDataException("Too many proof request fields");
            var length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
            if(reader.TokenType == JsonTokenType.Number && length > 64)
                throw new InvalidDataException("Oversized numeric token");
            if(reader.TokenType == JsonTokenType.StartObject)
                objects.Push(new HashSet<string>(StringComparer.Ordinal));
            else if(reader.TokenType == JsonTokenType.EndObject)
                objects.Pop();
            else if(reader.TokenType == JsonTokenType.PropertyName &&
                (length > 64 || !objects.Peek().Add(reader.GetString())))
                throw new InvalidDataException("Duplicate or oversized property name");
        }
    }

    internal sealed class ByteBudget
    {
        private readonly StratumInputPolicy policy;
        private double remaining;
        private long last = Stopwatch.GetTimestamp();
        internal ByteBudget(StratumInputPolicy policy)
        {
            this.policy = policy;
            remaining = policy.MaximumLength + 32768;
        }
        internal void Charge(int bytes)
        {
            if(!policy.ProofSized)
                return;
            var now = Stopwatch.GetTimestamp();
            remaining = Math.Min(policy.MaximumLength + 32768,
                remaining + Stopwatch.GetElapsedTime(last, now).TotalSeconds * 2 * 1024 * 1024);
            last = now;
            remaining -= bytes;
            if(remaining < 0)
                throw new IOException("Proof transport byte rate exceeded");
        }
    }
}
