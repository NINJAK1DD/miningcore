using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Pearl;
using Miningcore.JsonRpc;
using Miningcore.Rpc;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlCandidateSubmissionTests
{
    [Theory]
    [InlineData(false, 999, false, false, (int) PearlCandidateOutcome.Uncertain)]
    [InlineData(false, 1000, false, false, (int) PearlCandidateOutcome.Accepted)]
    [InlineData(true, 1000, true, false, (int) PearlCandidateOutcome.Uncertain)]
    [InlineData(true, 1001, true, false, (int) PearlCandidateOutcome.Orphaned)]
    [InlineData(true, 1001, true, true, (int) PearlCandidateOutcome.Uncertain)]
    [InlineData(true, 1001, false, false, (int) PearlCandidateOutcome.Uncertain)]
    public async Task RecoveryRequiresStableCompetingEvidenceBeforeRetiring(bool unknown, int height, bool competing, bool moving, int expected)
    {
        var raw = new byte[108];
        var digest = SHA256.HashData(SHA256.HashData(raw)); Array.Reverse(digest);
        var hash = Convert.ToHexString(digest).ToLowerInvariant();
        Task<RpcResponse<JToken>> Rpc(string method, object args, CancellationToken ct)
        {
            if(method == "getblockheader") return Task.FromResult(unknown
                ? new RpcResponse<JToken>(null, new JsonRpcError(-5, "unknown", null)) : new RpcResponse<JToken>(Convert.ToHexString(raw)));
            if(method == "submitblock") return Task.FromResult(new RpcResponse<JToken>(null, new JsonRpcError(-32603, "unavailable", null)));
            if(method == "getblockhash") Assert.True(height >= 1000);
            JToken value = method switch
            {
                "getblockchaininfo" => new JObject {["blocks"] = height, ["bestblockhash"] = new string('b',64)},
                "getblockhash" => competing ? new string('c',64) : hash,
                "getbestblockhash" => new string(moving ? 'd' : 'b',64),
                _ => throw new InvalidOperationException(method)
            };
            return Task.FromResult(new RpcResponse<JToken>(value));
        }
        Assert.Equal((PearlCandidateOutcome) expected, await PearlCandidateSubmission.ReconcileAsync(hash, raw, 1000, Rpc, CancellationToken.None));
    }

    [Fact]
    public async Task TemporaryMissingSnapshotDuringProofDoesNotCancelAndCompletionRechecksTip()
    {
        string tip = "parent";
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken workerToken = default;
        var proving = PearlCandidateSubmission.ProveWhileCurrentAsync("parent", () => tip,
            token => { workerToken = token; return completion.Task; }, CancellationToken.None, TimeSpan.FromMilliseconds(5));
        tip = null;
        await Task.Delay(30);
        Assert.False(proving.IsCompleted);
        Assert.False(workerToken.IsCancellationRequested);
        tip = "parent";
        completion.SetResult("certificate");
        Assert.Equal("certificate", await proving);

        tip = "parent";
        Assert.Null(await PearlCandidateSubmission.ProveWhileCurrentAsync("parent", () => tip,
            _ => { tip = "competing"; return Task.FromResult("certificate"); }, CancellationToken.None));
    }

    [Fact]
    public async Task ObservedDifferentTipCancelsAndReapsProofBeforeRetirement()
    {
        string tip = "parent";
        var reaped = false;
        var proving = PearlCandidateSubmission.ProveWhileCurrentAsync("parent", () => tip, async token =>
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), token); return "certificate"; }
            finally { reaped = true; }
        }, CancellationToken.None, TimeSpan.FromMilliseconds(5));
        tip = "competing";
        Assert.Null(await proving.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(reaped);
    }

    [Theory]
    [InlineData(false, false, (int) PearlCandidateOutcome.Accepted)]
    [InlineData(true, false, (int) PearlCandidateOutcome.Orphaned)]
    [InlineData(false, true, (int) PearlCandidateOutcome.Rejected)]
    public async Task RawHeaderReconciliationHandlesMainChainSideChainAndRejectedSubmission(bool sideChain, bool rejected, int expected)
    {
        var raw = new byte[PearlConsensus.HeaderSize];
        var bytes = SHA256.HashData(SHA256.HashData(raw)); Array.Reverse(bytes);
        var hash = Convert.ToHexString(bytes).ToLowerInvariant();
        var other = new string('b',64);
        var submitted = false;
        Task<RpcResponse<JToken>> Rpc(string method, object args, CancellationToken ct)
        {
            if(method == "getblockheader")
            {
                Assert.False((bool) ((object[]) args)[1]); // verbose lookup fails for known side chains
                return Task.FromResult(!submitted || rejected
                    ? new RpcResponse<JToken>(null, new JsonRpcError(-5,"unknown",null))
                    : new RpcResponse<JToken>(Convert.ToHexString(raw)));
            }
            if(method == "submitblock")
            {
                Assert.False(submitted); submitted = true;
                return Task.FromResult(new RpcResponse<JToken>(rejected ? new JValue("rejected: invalid block") : null));
            }
            JToken value = method switch
            {
                "getblockchaininfo" => new JObject {["blocks"] = 10, ["bestblockhash"] = other},
                "getblockhash" => sideChain ? other : hash,
                "getbestblockhash" => other,
                _ => throw new InvalidOperationException(method)
            };
            return Task.FromResult(new RpcResponse<JToken>(value));
        }
        Assert.Equal((PearlCandidateOutcome) expected, await PearlCandidateSubmission.ReconcileAsync(hash, raw, 10, Rpc, CancellationToken.None));
        Assert.True(submitted);
    }

    [Fact]
    public async Task RpcFailureDoesNotRetireSubmissionAndAgeRetainsAnExplicitBound()
    {
        var result = await PearlCandidateSubmission.ReconcileAsync(new string('a',64), new byte[108], 10,
            (_, _, _) => Task.FromResult(new RpcResponse<JToken>(null, new JsonRpcError(-32603,"unavailable",null))), CancellationToken.None);
        Assert.Equal(PearlCandidateOutcome.Uncertain,result);
        Assert.False(PearlCandidateSubmission.TipChanged(null,"a"));
        Assert.False(PearlCandidateSubmission.TipChanged("a","a"));
        Assert.True(PearlCandidateSubmission.TipChanged("b","a"));
        var now = DateTime.UtcNow;
        Assert.False(PearlCandidateSubmission.Expired(now.AddHours(-23),now));
        Assert.True(PearlCandidateSubmission.Expired(now.AddHours(-24),now));
    }
}
