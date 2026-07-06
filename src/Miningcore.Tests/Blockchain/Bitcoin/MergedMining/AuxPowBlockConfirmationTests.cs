using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Blockchain;
using Miningcore.JsonRpc;
using Miningcore.Rpc;
using Newtonsoft.Json.Linq;
using System.Threading;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class AuxPowBlockConfirmationTests
{
    [Fact]
    public void PendingMarker_RoundTripsBlockHash()
    {
        const string blockHash = "0123456789abcdef";

        var marker = AuxPowBlockConfirmation.CreatePending(blockHash);
        var parsed = AuxPowBlockConfirmation.TryGetPendingBlockHash(marker, out var result);

        Assert.True(parsed);
        Assert.Equal(blockHash, result);
    }

    [Fact]
    public void ClaimMarker_RoundTripsProofIdentityAndMissCount()
    {
        const string blockHash = "0123456789abcdef";
        const string parentBlock = "parent-header";

        var marker = AuxPowBlockConfirmation.CreateClaim(blockHash, parentBlock, 2);
        var parsed = AuxPowBlockConfirmation.TryGetClaim(marker, out var result,
            out var resultParentBlock, out var misses);

        Assert.True(parsed);
        Assert.Equal(blockHash, result);
        Assert.Equal(parentBlock, resultParentBlock);
        Assert.Equal(2, misses);
        Assert.False(AuxPowBlockConfirmation.TryGetPendingBlockHash(marker, out _));
    }

    [Fact]
    public void ParentUncertainMarker_RoundTripsBlockHashAndMissCount()
    {
        const string blockHash = "0123456789abcdef";

        var marker = AuxPowBlockConfirmation.CreateParentUncertain(blockHash, 2);
        var parsed = AuxPowBlockConfirmation.TryGetParentUncertain(marker, out var result,
            out var misses);

        Assert.True(parsed);
        Assert.Equal(blockHash, result);
        Assert.Equal(2, misses);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("regular-coinbase-txid")]
    [InlineData("auxpow-block:")]
    public void PendingMarker_RejectsNonMarkers(string value)
    {
        Assert.False(AuxPowBlockConfirmation.TryGetPendingBlockHash(value, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void AuxiliaryTemplateChange_DistinguishesTipFromSameHeightRefresh()
    {
        var previous = Template(100, "previous", "hash-one");

        Assert.Equal(AuxiliaryTemplateChange.None,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryTemplateChange(
                previous, Template(100, "previous", "hash-one")));
        Assert.Equal(AuxiliaryTemplateChange.Template,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryTemplateChange(
                previous, Template(100, "previous", "hash-two")));
        Assert.Equal(AuxiliaryTemplateChange.ChainTip,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryTemplateChange(
                previous, Template(101, "hash-one", "hash-three")));
        Assert.Equal(AuxiliaryTemplateChange.ChainTip,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryTemplateChange(
                previous, Template(100, "different-previous", "hash-four")));
    }

    [Fact]
    public void AuxiliaryTemplateResolution_RequiresInitialTemplateAndUsesCacheDuringOutage()
    {
        var cached = Template(100, "previous", "cached-hash");
        var unavailable = new RpcResponse<AuxBlockTemplate>(null,
            new JsonRpcError(-500, "unavailable", null));

        Assert.False(MergedMiningBitcoinJobManager.TryResolveAuxiliaryTemplate(
            null, unavailable, out var missing, out var missingUsedCache));
        Assert.Null(missing);
        Assert.False(missingUsedCache);

        Assert.True(MergedMiningBitcoinJobManager.TryResolveAuxiliaryTemplate(
            cached, unavailable, out var fallback, out var fallbackUsedCache));
        Assert.Same(cached, fallback);
        Assert.True(fallbackUsedCache);

        var fresh = Template(101, "cached-hash", "fresh-hash");
        Assert.True(MergedMiningBitcoinJobManager.TryResolveAuxiliaryTemplate(
            cached, new RpcResponse<AuxBlockTemplate>(fresh), out var resolved,
            out var resolvedUsedCache));
        Assert.Same(fresh, resolved);
        Assert.False(resolvedUsedCache);
    }

    [Fact]
    public void AuxiliaryAddressValidation_DistinguishesInvalidAddressFromDaemonOutage()
    {
        Assert.Equal(AuxiliaryAddressValidation.Valid,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryAddressValidation(
                new RpcResponse<ValidateAddressResponse>(new ValidateAddressResponse { IsValid = true })));
        Assert.Equal(AuxiliaryAddressValidation.Invalid,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryAddressValidation(
                new RpcResponse<ValidateAddressResponse>(new ValidateAddressResponse { IsValid = false })));
        Assert.Equal(AuxiliaryAddressValidation.Unavailable,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryAddressValidation(
                new RpcResponse<ValidateAddressResponse>(null,
                    new JsonRpcError(-500, "unavailable", null))));
    }

    [Fact]
    public void AuxiliarySubmissionResponse_DistinguishesRejectionFromAmbiguousTransportFailure()
    {
        Assert.Equal(AuxiliarySubmissionResult.Accepted,
            MergedMiningBitcoinJobManager.ClassifyAuxiliarySubmissionResponse(
                new RpcResponse<JToken>(JToken.FromObject(true))));
        Assert.Equal(AuxiliarySubmissionResult.Rejected,
            MergedMiningBitcoinJobManager.ClassifyAuxiliarySubmissionResponse(
                new RpcResponse<JToken>(JToken.FromObject(false))));
        Assert.Equal(AuxiliarySubmissionResult.Ambiguous,
            MergedMiningBitcoinJobManager.ClassifyAuxiliarySubmissionResponse(
                new RpcResponse<JToken>(null,
                    new JsonRpcError(-500, "connection lost", null))));
        Assert.Equal(AuxiliarySubmissionResult.Rejected,
            MergedMiningBitcoinJobManager.ClassifyAuxiliarySubmissionResponse(
                new RpcResponse<JToken>(null,
                    new JsonRpcError(-8, "block hash unknown", null))));
        Assert.Equal(AuxiliarySubmissionResult.Rejected,
            MergedMiningBitcoinJobManager.ClassifyAuxiliarySubmissionResponse(
                new RpcResponse<JToken>(null,
                    new JsonRpcError(-22, "AuxPoW decode failed", null))));
    }

    [Fact]
    public void AuxiliaryBlockLookup_AcceptsOnlyActiveMatchingParentProof()
    {
        Assert.Equal(AuxiliaryBlockLookupResult.Accepted,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryBlockLookup("doge-block",
                "parent-a", new RpcResponse<Block>(new Block
                {
                    Hash = "doge-block",
                    Confirmations = 1,
                    AuxPow = new AuxPow { ParentBlock = "parent-a" },
                })));

        Assert.Equal(AuxiliaryBlockLookupResult.LostToDifferentProof,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryBlockLookup("doge-block",
                "parent-b", new RpcResponse<Block>(new Block
                {
                    Hash = "doge-block",
                    Confirmations = 1,
                    AuxPow = new AuxPow { ParentBlock = "parent-a" },
                })));
    }

    [Fact]
    public void AuxiliaryBlockLookup_PersistsClaimWhenProofIsUnavailable()
    {
        Assert.Equal(AuxiliaryBlockLookupResult.MissingProof,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryBlockLookup("doge-block",
                "parent-a", new RpcResponse<Block>(new Block
                {
                    Hash = "doge-block",
                    Confirmations = 1,
                })));

        Assert.Equal(AuxiliaryBlockLookupResult.Unavailable,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryBlockLookup("doge-block",
                "parent-a", new RpcResponse<Block>(null,
                    new JsonRpcError(-5, "Block not found", null))));
    }

    [Fact]
    public void AuxiliaryBlockLookup_RejectsKnownInactiveChild()
    {
        Assert.Equal(AuxiliaryBlockLookupResult.Orphaned,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryBlockLookup("doge-block",
                "parent-a", new RpcResponse<Block>(new Block
                {
                    Hash = "doge-block",
                    Confirmations = -1,
                    AuxPow = new AuxPow { ParentBlock = "parent-a" },
                })));
    }

    [Theory]
    [InlineData((int) AuxiliarySubmissionResult.Accepted, (int) AuxiliaryBlockLookupResult.Accepted, true, false)]
    [InlineData((int) AuxiliarySubmissionResult.Accepted, (int) AuxiliaryBlockLookupResult.LostToDifferentProof, false, false)]
    [InlineData((int) AuxiliarySubmissionResult.Accepted, (int) AuxiliaryBlockLookupResult.Unavailable, false, true)]
    [InlineData((int) AuxiliarySubmissionResult.Accepted, (int) AuxiliaryBlockLookupResult.MissingProof, false, true)]
    [InlineData((int) AuxiliarySubmissionResult.Accepted, (int) AuxiliaryBlockLookupResult.Orphaned, false, false)]
    [InlineData((int) AuxiliarySubmissionResult.Ambiguous, (int) AuxiliaryBlockLookupResult.Accepted, true, false)]
    [InlineData((int) AuxiliarySubmissionResult.Rejected, (int) AuxiliaryBlockLookupResult.Accepted, false, false)]
    public void AuxiliarySubmissionOutcome_RequiresProofAttributionForBooleanTrue(
        int submissionResultValue, int lookupResultValue,
        bool expectedAccepted, bool expectedUncertain)
    {
        var submissionResult = (AuxiliarySubmissionResult) submissionResultValue;
        var lookupResult = (AuxiliaryBlockLookupResult) lookupResultValue;
        var (accepted, uncertain) =
            MergedMiningBitcoinJobManager.ClassifyAuxiliarySubmissionOutcome(
                submissionResult, lookupResult);

        Assert.Equal(expectedAccepted, accepted);
        Assert.Equal(expectedUncertain, uncertain);
    }

    [Fact]
    public void AuxiliaryProofLookup_IsRequiredForBooleanTrueAndTransportAmbiguity()
    {
        Assert.True(MergedMiningBitcoinJobManager.RequiresAuxiliaryProofLookup(
            AuxiliarySubmissionResult.Accepted));
        Assert.True(MergedMiningBitcoinJobManager.RequiresAuxiliaryProofLookup(
            AuxiliarySubmissionResult.Ambiguous));
        Assert.False(MergedMiningBitcoinJobManager.RequiresAuxiliaryProofLookup(
            AuxiliarySubmissionResult.Rejected));
    }

    [Fact]
    public void AmbiguousLookup_UsesRemainingSubmissionOperationDeadline()
    {
        using var operation = new CancellationTokenSource();
        using var lookup = MergedMiningBitcoinJobManager
            .CreateAmbiguousLookupCancellationTokenSource(operation.Token);

        operation.Cancel();

        Assert.True(lookup.IsCancellationRequested);
    }

    [Fact]
    public void ParentTemplateOrdering_RejectsLowerHeight()
    {
        var previous = new BlockTemplate { Height = 102 };

        Assert.True(MergedMiningBitcoinJobManager.IsStaleParentTemplate(
            previous, new BlockTemplate { Height = 101 }));
        Assert.False(MergedMiningBitcoinJobManager.IsStaleParentTemplate(
            previous, new BlockTemplate { Height = 102 }));
        Assert.False(MergedMiningBitcoinJobManager.IsStaleParentTemplate(
            previous, new BlockTemplate { Height = 103 }));
    }

    [Fact]
    public void StreamRefresh_UsesCachedAuxiliaryTemplateWithoutBlockingOnDogeRpc()
    {
        Assert.False(MergedMiningBitcoinJobManager.ShouldRefreshAuxiliaryTemplate(
            JobRefreshBy.BlockTemplateStream, true));
        Assert.False(MergedMiningBitcoinJobManager.ShouldRefreshAuxiliaryTemplate(
            JobRefreshBy.BlockTemplateStreamRefresh, true));
        Assert.True(MergedMiningBitcoinJobManager.ShouldRefreshAuxiliaryTemplate(
            JobRefreshBy.Poll, true));
        Assert.True(MergedMiningBitcoinJobManager.ShouldRefreshAuxiliaryTemplate(
            JobRefreshBy.BlockTemplateStream, false));
    }

    private static AuxBlockTemplate Template(uint height, string previousBlockHash, string hash)
    {
        return new AuxBlockTemplate
        {
            Height = height,
            PreviousBlockhash = previousBlockHash,
            Hash = hash,
        };
    }
}
