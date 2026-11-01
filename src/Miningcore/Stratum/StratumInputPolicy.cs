using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Miningcore.Stratum;

/// <summary>Internal, explicitly selected transport profiles. Ordinary Stratum keeps its original limits.</summary>
internal sealed class StratumInputPolicy
{
    internal static readonly StratumInputPolicy Standard = new(false);
    internal static readonly StratumInputPolicy Pearl = CreatePearl();
    private readonly SemaphoreSlim connections;
    private readonly SemaphoreSlim frames;
    internal bool ProofSized { get; }
    internal int MaximumLength => ProofSized ? 4 * ((8 * 1024 * 1024 + 2) / 3) + 4096 : 0x8000;
    internal int ReadSize => ProofSized ? 16384 : 0x8001;
    private StratumInputPolicy(bool proofSized, int maximumConnections = 1024, int maximumFrames = 4)
    {
        ProofSized = proofSized;
        if(proofSized)
        {
            connections = new SemaphoreSlim(maximumConnections, maximumConnections);
            frames = new SemaphoreSlim(maximumFrames, maximumFrames);
        }
    }
    internal static StratumInputPolicy CreatePearl(int maximumConnections = 1024, int maximumFrames = 4)
    {
        if(maximumConnections is < 16 or > 4096 || maximumFrames is < 1 or > 32 || maximumFrames > maximumConnections)
            throw new ArgumentOutOfRangeException(nameof(maximumConnections));
        return new StratumInputPolicy(true, maximumConnections, maximumFrames);
    }

    internal IDisposable Acquire()
    {
        if(!ProofSized)
            return null;
        if(!connections.Wait(0))
            throw new IOException("Proof transport connection capacity reached");
        return new Lease(connections);
    }

    internal IDisposable AcquireFrame()
    {
        if(!frames.Wait(0)) throw new IOException("Proof frame capacity reached; retry later");
        return new Lease(frames);
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
