using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.JsonRpc;
using Miningcore.Rpc;
using Newtonsoft.Json.Linq;
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
