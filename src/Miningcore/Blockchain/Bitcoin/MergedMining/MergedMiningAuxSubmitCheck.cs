using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

public class MergedMiningAuxSubmitCheck : BitcoinJobManager
{
    public MergedMiningAuxSubmitCheck(IComponentContext ctx, IMasterClock clock,
        IMessageBus messageBus, IExtraNonceProvider extraNonceProvider) :
        base(ctx, clock, messageBus, extraNonceProvider)
    {
    }

    private const string SubmitAuxBlock = "submitauxblock";
    private PoolConfig auxiliaryPoolConfig;
    private RpcClient auxiliaryRpc;

    private async Task SubmitAuxiliaryBlockAsync(StratumConnection worker,
        MergedMiningBitcoinWorkerContext context, MergedMiningShareResult result, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(context.AuxiliaryMiner))
            return;

        var template = result.AuxiliaryBlockTemplate;
        var submitResponse = await auxiliaryRpc.ExecuteAsync<JToken>(logger, SubmitAuxBlock, ct,
            new object[] { template.Hash, result.AuxPowHex });

        var accepted = submitResponse.Error == null && submitResponse.Response?.Type == JTokenType.Boolean &&
            submitResponse.Response.Value<bool>();

        if(!accepted)
            return;

        OnBlockFound();

        var coinbaseTransaction = await GetAuxiliaryCoinbaseTransactionAsync(template.Hash, ct);
        if(string.IsNullOrEmpty(coinbaseTransaction))
        {
            var message = $"Auxiliary daemon accepted block {template.Height}, but its coinbase transaction could not be retrieved";
            messageBus.SendMessage(new AdminNotification("Auxiliary block accounting failed", message));
            return;
        }

        var auxiliaryShare = new Share
        {
            PoolId = auxiliaryPoolConfig.Id,
            Miner = context.AuxiliaryMiner,
            Worker = context.Worker,
            UserAgent = context.UserAgent,
            IpAddress = worker.RemoteEndpoint?.Address?.ToString(),
            Source = clusterConfig.ClusterName,
            Difficulty = result.Share.Difficulty,
            ShareDifficulty = result.Share.ShareDifficulty,
            ActualDifficulty = result.Share.ActualDifficulty,
            NetworkDifficulty = result.AuxiliaryDifficulty,
            BlockHeight = template.Height,
            BlockHash = template.Hash,
            IsBlockCandidate = true,
            TransactionConfirmationData = coinbaseTransaction,
            SessionId = context.SessionId,
            Created = clock.Now,
        };

        messageBus.SendMessage(auxiliaryShare);
    }

    private async Task<string> GetAuxiliaryCoinbaseTransactionAsync(string blockHash, CancellationToken ct)
    {
        for(var attempt = 1; attempt <= 8; attempt++)
        {
            var response = await auxiliaryRpc.ExecuteAsync<Block>(logger, BitcoinCommands.GetBlock, ct,
                new object[] { blockHash });

            var coinbase = response.Error == null ? response.Response?.Transactions?.FirstOrDefault() : null;
            if(!string.IsNullOrEmpty(coinbase))
                return coinbase;

            if(attempt < 8)
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 100), ct);
        }

        return null;
    }
}
