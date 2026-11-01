using System.IO;
using System.Security.Cryptography;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Rpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Miningcore.Blockchain.Pearl;

/// <summary>Bounded RPC and immutable template publication. Failover always rechecks network identity.</summary>
internal sealed class PearlJobManager
{
    private readonly RpcClient[] nodes;
    private readonly ILogger logger;
    private readonly PearlNetwork network;
    private readonly string address;
    private readonly TimeProvider time;
    private Snapshot current;
    private int selected;
    internal sealed record Snapshot(string Fingerprint, string Tip, string Json, long Received, uint Height, double Difficulty);
    internal Snapshot Current => Volatile.Read(ref current);
    internal bool Ready => Current is { } value && time.GetElapsedTime(value.Received).TotalSeconds < 45;
    internal double TemplateAgeSeconds => Current is { } value ? time.GetElapsedTime(value.Received).TotalSeconds : -1;
    internal RpcClient Node => nodes[Volatile.Read(ref selected)];

    internal PearlJobManager(PoolConfig pool, PearlNetwork network, JsonSerializerSettings settings,
        IMessageBus bus, ILogger logger, TimeProvider time = null)
    {
        this.logger = logger;
        this.network = network;
        this.time = time ?? TimeProvider.System;
        address = pool.Address;
        nodes = pool.Daemons.Where(x => string.IsNullOrEmpty(x.Category) || x.Category == "node")
            .Select(x => new RpcClient(x, settings, bus, pool.Id)).ToArray();
        if(nodes.Length is < 1 or > 4) throw new InvalidDataException("Pearl requires one to four node endpoints");
    }

    internal static string Genesis(PearlNetwork network) => network switch
    {
        PearlNetwork.Mainnet => "a18d3093b7ff618f0cdd073fa3d10374e2e0105fe7ef53ff48b55f41c58dadd7",
        PearlNetwork.Regtest => "78d0ab27df6a51a8e9dbbdefffca1ebbbeb3818cb083ce2c2f43b047247e5832",
        _ => throw new InvalidDataException("Uncertified Pearl network")
    };

    internal async Task<bool> RefreshAsync(CancellationToken ct)
    {
        for(var attempt = 0; attempt < nodes.Length; attempt++)
        {
            var index = (selected + attempt) % nodes.Length;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var node = nodes[index];
                var genesis = await CallAsync<string>(node, "getblockhash", new object[] {0}, deadline.Token);
                if(genesis != Genesis(network)) throw new InvalidDataException("Pearl node genesis mismatch");
                var info = await CallAsync<JObject>(node, "getblockchaininfo", null, deadline.Token);
                if(info.Value<bool?>("initialblockdownload") == true || info.Value<long?>("blocks") == null)
                    throw new InvalidDataException("Pearl node is not synchronized");
                var template = await CallAsync<PearlBlockTemplate>(node, "getblocktemplate", new object[]
                {
                    new {capabilities = new[] {"coinbasevalue", "workid", "coinbase/append"}, rules = new[] {"segwit"}}
                }, deadline.Token, 12_000_000);
                if(template.Height != info.Value<long>("blocks") + 1)
                    throw new InvalidDataException("Pearl template changed during synchronization check");
                // Validate all committed fields before exposing the snapshot to any miner.
                _ = new PearlBlockJob(template, address, network, new byte[24]);
                var json = JsonConvert.SerializeObject(template);
                var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
                var changed = Current?.Fingerprint != fingerprint;
                var snapshot = new Snapshot(fingerprint, template.PreviousBlockhash, json, time.GetTimestamp(),
                    template.Height, PearlWork.Difficulty(PearlConsensus.DecodeTarget(Convert.ToUInt32(template.Bits, 16))));
                Volatile.Write(ref selected, index);
                Volatile.Write(ref current, snapshot);
                return changed;
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception ex)
            {
                RpcConsumerDiagnostics.Write(logger, LogLevel.Warn, "PearlJobManager.Refresh", failure: ex);
            }
        }
        Volatile.Write(ref current, null);
        return false;
    }

    internal PearlBlockJob CreateJob()
    {
        var snapshot = Current;
        if(snapshot == null || !Ready) throw new InvalidOperationException("Pearl node is unavailable or unsynchronized");
        return new PearlBlockJob(JsonConvert.DeserializeObject<PearlBlockTemplate>(snapshot.Json), address,
            network, RandomNumberGenerator.GetBytes(24));
    }

    internal async Task<T> CallAsync<T>(RpcClient rpc, string method, object parameters, CancellationToken ct,
        int maxBytes = 262144) where T : class
    {
        var response = await rpc.ExecuteBoundedAsync<T>(logger, method, ct, new RpcResponseLimits(maxBytes, 64), parameters);
        if(response.Error != null) throw new InvalidDataException($"Pearl {method} failed ({response.Error.Code})");
        return response.Response ?? throw new InvalidDataException($"Pearl {method} returned no result");
    }
}
