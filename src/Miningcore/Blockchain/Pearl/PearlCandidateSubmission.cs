using System.IO;
using System.Security.Cryptography;
using Miningcore.Rpc;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Pearl;

internal enum PearlCandidateOutcome { Uncertain, Accepted, Orphaned, Rejected }

// The same state machine is used by live submission and restart recovery.
internal static class PearlCandidateSubmission
{
    internal static bool TipChanged(string observedTip, string previous) => observedTip != null && observedTip != previous;
    internal static bool Expired(DateTime created, DateTime now) => now - created >= TimeSpan.FromHours(24);

    internal static async Task<T> ProveWhileCurrentAsync<T>(string previous, Func<string> observedTip,
        Func<CancellationToken, Task<T>> prove, CancellationToken ct, TimeSpan? pollInterval = null) where T : class
    {
        if(TipChanged(observedTip(), previous)) return null;
        using var proving = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = prove(proving.Token);
        while(!pending.IsCompleted)
        {
            await Task.WhenAny(pending, Task.Delay(pollInterval ?? TimeSpan.FromSeconds(1), ct));
            if(ct.IsCancellationRequested || TipChanged(observedTip(), previous))
            {
                proving.Cancel();
                try { await pending; } // Reap before retiring work or releasing native capacity.
                catch(OperationCanceledException) when(proving.IsCancellationRequested) { }
                ct.ThrowIfCancellationRequested();
                return null;
            }
        }
        var result = await pending;
        ct.ThrowIfCancellationRequested();
        return TipChanged(observedTip(), previous) ? null : result;
    }

    internal static async Task<PearlCandidateOutcome> ReconcileAsync(string hash, byte[] block, long height,
        Func<string, object, CancellationToken, Task<RpcResponse<JToken>>> rpc, CancellationToken ct)
    {
        var header = await rpc("getblockheader", new object[] {hash, false}, ct);
        var rejected = false;
        if(header.Error?.Code == -5)
        {
            var submission = await rpc("submitblock", new object[] {Convert.ToHexString(block).ToLowerInvariant()}, ct);
            rejected = submission.Error == null && submission.Response?.Type == JTokenType.String &&
                !string.IsNullOrEmpty(submission.Response.Value<string>());
            header = await rpc("getblockheader", new object[] {hash, false}, ct);
        }
        if(header.Error != null)
        {
            if(header.Error.Code != -5) return PearlCandidateOutcome.Uncertain;
            if(rejected) return PearlCandidateOutcome.Rejected;
            // A missing header alone is not rejection evidence. Retire only when a
            // stable canonical chain has actually passed a competing block at this height.
            return await CanonicalOutcomeAsync(hash, height, true, rpc, ct);
        }
        var encoded = header.Response?.Type == JTokenType.String ? header.Response.Value<string>() : null;
        if(encoded?.Length != PearlConsensus.HeaderSize * 2) throw new InvalidDataException("Invalid Pearl candidate header evidence");
        var digest = SHA256.HashData(SHA256.HashData(Convert.FromHexString(encoded)));
        Array.Reverse(digest);
        if(Convert.ToHexString(digest).ToLowerInvariant() != hash)
            throw new InvalidDataException("Pearl candidate header identity mismatch");

        return await CanonicalOutcomeAsync(hash, height, false, rpc, ct);
    }

    private static async Task<PearlCandidateOutcome> CanonicalOutcomeAsync(string hash, long height, bool unknown,
        Func<string, object, CancellationToken, Task<RpcResponse<JToken>>> rpc, CancellationToken ct)
    {
        var chain = await rpc("getblockchaininfo", null, ct);
        var tip = chain.Response?["blocks"]?.Value<long>();
        var best = chain.Response?["bestblockhash"]?.Value<string>();
        if(chain.Error != null || tip is null or < 0 || !PearlPayoutHandler.IsTransactionId(best)) return PearlCandidateOutcome.Uncertain;
        if(tip < height || unknown && tip == height) return PearlCandidateOutcome.Uncertain;
        var canonical = await rpc("getblockhash", new object[] {height}, ct);
        var identity = canonical.Response?.Type == JTokenType.String ? canonical.Response.Value<string>() : null;
        if(canonical.Error != null || !PearlPayoutHandler.IsTransactionId(identity)) return PearlCandidateOutcome.Uncertain;
        var outcome = identity == hash ? (unknown ? PearlCandidateOutcome.Uncertain : PearlCandidateOutcome.Accepted) : PearlCandidateOutcome.Orphaned;
        var end = await rpc("getbestblockhash", null, ct);
        return end.Error == null && end.Response?.Value<string>() == best ? outcome : PearlCandidateOutcome.Uncertain;
    }
}
