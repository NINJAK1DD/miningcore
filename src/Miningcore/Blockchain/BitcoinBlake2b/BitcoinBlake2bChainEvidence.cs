using Miningcore.Mining;
using Miningcore.Rpc;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.BitcoinBlake2b;

internal enum BitcoinBlake2bChainObservation { Verified, RpcUnavailable, ChainChanged, RpcContractInvalid }

internal static class BitcoinBlake2bChainEvidence
{
    internal const int MaximumSnapshotDistance = 6;
    internal static bool Hash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static async Task<bool> VerifyAsync(JObject chain, JObject deployment,
        Func<string, Task<RpcResponse<JObject>>> readHeader, string poolId, CancellationToken ct = default) =>
        await ObserveAsync(chain, deployment, readHeader, poolId, ct) == BitcoinBlake2bChainObservation.Verified;

    // Bind the deployment phase to a nearby chain observation AND immutable
    // ancestry. Independent positive confirmations do not prove that two
    // snapshots belong to the same branch across multiple rapid reorgs.
    internal static async Task<BitcoinBlake2bChainObservation> ObserveAsync(JObject chain, JObject deployment,
        Func<string, Task<RpcResponse<JObject>>> readHeader, string poolId, CancellationToken ct = default)
    {
        var chainHash = chain?["bestblockhash"]?.Type == JTokenType.String ? chain["bestblockhash"].Value<string>() : null;
        var deploymentHash = deployment?["hash"]?.Type == JTokenType.String ? deployment["hash"].Value<string>() : null;
        if(!Hash(chainHash) || !Hash(deploymentHash) ||
            chain?["blocks"]?.Type != JTokenType.Integer || !int.TryParse(chain["blocks"].ToString(), out var chainHeight) || chainHeight < 0 ||
            deployment?["height"]?.Type != JTokenType.Integer || !int.TryParse(deployment["height"].ToString(), out var deploymentHeight) || deploymentHeight < 0)
            throw new PoolStartupException("Bitcoin BLAKE2b chain snapshot metadata is invalid", poolId);
        if(Math.Abs((long) chainHeight - deploymentHeight) > MaximumSnapshotDistance ||
            chainHeight != deploymentHeight && string.Equals(chainHash, deploymentHash, StringComparison.OrdinalIgnoreCase) ||
            chainHeight == deploymentHeight && !string.Equals(chainHash, deploymentHash, StringComparison.OrdinalIgnoreCase))
            return BitcoinBlake2bChainObservation.ChainChanged;

        async Task<(JObject Header, BitcoinBlake2bChainObservation State)> Read(string hash, int expectedHeight)
        {
            ct.ThrowIfCancellationRequested();
            RpcResponse<JObject> response;
            try { response = await readHeader(hash); }
            catch(Exception ex) when(!ct.IsCancellationRequested &&
                ex is HttpRequestException or TimeoutException or OperationCanceledException)
            { return (null, BitcoinBlake2bChainObservation.RpcUnavailable); }
            ct.ThrowIfCancellationRequested();
            if(response?.Error?.Code == -32601 || response?.Error?.InnerException is Newtonsoft.Json.JsonSerializationException)
                return (null, BitcoinBlake2bChainObservation.RpcContractInvalid);
            if(response == null || response.Error != null) return (null, BitcoinBlake2bChainObservation.RpcUnavailable);
            if(response.Response == null) throw new PoolStartupException("Bitcoin BLAKE2b header RPC result is empty", poolId);
            var header = response.Response;
            if(header["hash"]?.Type != JTokenType.String ||
                !string.Equals(header["hash"].Value<string>(), hash, StringComparison.OrdinalIgnoreCase) ||
                header["height"]?.Type != JTokenType.Integer || !int.TryParse(header["height"].ToString(), out var height) || height != expectedHeight ||
                header["confirmations"]?.Type != JTokenType.Integer ||
                !int.TryParse(header["confirmations"].ToString(), out var confirmations) || confirmations == 0 || confirmations < -1)
                throw new PoolStartupException("Bitcoin BLAKE2b chain snapshot header contradicts its RPC evidence", poolId);
            return confirmations == -1 ? (null, BitcoinBlake2bChainObservation.ChainChanged) :
                (header, BitcoinBlake2bChainObservation.Verified);
        }
        var first = await Read(chainHash, chainHeight);
        if(first.State != BitcoinBlake2bChainObservation.Verified) return first.State;
        if(string.Equals(chainHash, deploymentHash, StringComparison.OrdinalIgnoreCase)) return first.State;
        var second = await Read(deploymentHash, deploymentHeight);
        if(second.State != BitcoinBlake2bChainObservation.Verified) return second.State;
        var higherHash = chainHeight > deploymentHeight ? chainHash : deploymentHash;
        var higherHeight = Math.Max(chainHeight, deploymentHeight);
        var lowerHash = chainHeight < deploymentHeight ? chainHash : deploymentHash;
        var lowerHeight = Math.Min(chainHeight, deploymentHeight);
        var ancestor = chainHeight > deploymentHeight ? first.Header : second.Header;
        for(var height = higherHeight; height > lowerHeight; height--)
        {
            var previous = ancestor["previousblockhash"]?.Type == JTokenType.String ? ancestor["previousblockhash"].Value<string>() : null;
            if(!Hash(previous)) throw new PoolStartupException("Bitcoin BLAKE2b snapshot ancestry is malformed", poolId);
            if(height - 1 == lowerHeight)
            {
                if(!string.Equals(previous, lowerHash, StringComparison.OrdinalIgnoreCase))
                    return BitcoinBlake2bChainObservation.ChainChanged;
                break;
            }
            var next = await Read(previous, height - 1);
            if(next.State != BitcoinBlake2bChainObservation.Verified) return next.State;
            ancestor = next.Header;
        }
        // Final membership of the higher snapshot covers both snapshots now
        // that their ancestry is established. Ordinary advancement is allowed.
        return (await Read(higherHash, higherHeight)).State;
    }
}
