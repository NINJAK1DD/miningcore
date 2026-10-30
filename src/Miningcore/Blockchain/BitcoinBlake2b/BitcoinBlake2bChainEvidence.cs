using Miningcore.Mining;
using Miningcore.Rpc;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.BitcoinBlake2b;

internal static class BitcoinBlake2bChainEvidence
{
    internal static bool Hash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    // Separate RPC calls need not observe the same tip. Prove both snapshots
    // still belong to the active branch instead of mistaking advancement for
    // contract drift. An intervening reorg simply withholds this observation.
    internal static async Task<bool> VerifyAsync(JObject chain, JObject deployment,
        Func<string, Task<RpcResponse<JObject>>> readHeader, string poolId, CancellationToken ct = default)
    {
        var chainHash = chain?["bestblockhash"]?.Type == JTokenType.String ? chain["bestblockhash"].Value<string>() : null;
        var deploymentHash = deployment?["hash"]?.Type == JTokenType.String ? deployment["hash"].Value<string>() : null;
        if(!Hash(chainHash) || !Hash(deploymentHash) ||
            chain?["blocks"]?.Type != JTokenType.Integer || !int.TryParse(chain["blocks"].ToString(), out var chainHeight) || chainHeight < 0 ||
            deployment?["height"]?.Type != JTokenType.Integer || !int.TryParse(deployment["height"].ToString(), out var deploymentHeight) || deploymentHeight < 0)
            throw new PoolStartupException("Bitcoin BLAKE2b chain snapshot metadata is invalid", poolId);
        foreach(var snapshot in new[] { (Hash: chainHash, Height: chainHeight), (Hash: deploymentHash, Height: deploymentHeight) }.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            RpcResponse<JObject> response;
            try { response = await readHeader(snapshot.Hash); }
            catch(Exception ex) when(!ct.IsCancellationRequested &&
                ex is HttpRequestException or TimeoutException or OperationCanceledException)
            { return false; }
            ct.ThrowIfCancellationRequested();
            if(response?.Error != null || response?.Response == null) return false;
            var header = response.Response;
            if(header["hash"]?.Type != JTokenType.String ||
                !string.Equals(header["hash"].Value<string>(), snapshot.Hash, StringComparison.OrdinalIgnoreCase) ||
                header["height"]?.Type != JTokenType.Integer || !int.TryParse(header["height"].ToString(), out var height) || height != snapshot.Height ||
                header["confirmations"]?.Type != JTokenType.Integer ||
                !int.TryParse(header["confirmations"].ToString(), out var confirmations) || confirmations == 0 || confirmations < -1)
                throw new PoolStartupException("Bitcoin BLAKE2b chain snapshot header contradicts its RPC evidence", poolId);
            if(confirmations == -1) return false;
        }
        return true;
    }
}
