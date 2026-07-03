using Autofac;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Rpc;
using Miningcore.Time;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

public class MergedMiningValidateCheck : BitcoinJobManager
{
    public MergedMiningValidateCheck(IComponentContext ctx, IMasterClock clock,
        IMessageBus messageBus, IExtraNonceProvider extraNonceProvider) :
        base(ctx, clock, messageBus, extraNonceProvider)
    {
    }

    private RpcClient auxiliaryRpc;

    public async Task<bool> ValidateAuxiliaryAddressAsync(string address, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(address))
            return false;

        var result = await auxiliaryRpc.ExecuteAsync<ValidateAddressResponse>(logger,
            BitcoinCommands.ValidateAddress, ct, new[] { address });

        return result.Error == null && result.Response is { IsValid: true };
    }
}
