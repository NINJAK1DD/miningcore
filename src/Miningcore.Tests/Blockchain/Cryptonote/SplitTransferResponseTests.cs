using System;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Cryptonote;
using Miningcore.Blockchain.Zano;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using NLog;
using NSubstitute;
using Xunit;
using CryptonoteSplitResponse = Miningcore.Blockchain.Cryptonote.DaemonResponses.TransferSplitResponse;
using ZanoSplitResponse = Miningcore.Blockchain.Zano.DaemonResponses.TransferSplitResponse;

namespace Miningcore.Tests.Blockchain.Cryptonote;

public class SplitTransferResponseTests
{
    [Fact]
    public void CryptonoteMalformedSuccessesAreUncertain()
    {
        var malformed = new CryptonoteSplitResponse[]
        {
            null,
            new() { TxHashList = null, FeeList = Array.Empty<ulong>() },
            new() { TxHashList = Array.Empty<string>(), FeeList = Array.Empty<ulong>() },
            new() { TxHashList = new[] { "tx-1" }, FeeList = null },
            new() { TxHashList = new[] { "tx-1" }, FeeList = Array.Empty<ulong>() },
            new() { TxHashList = new[] { "tx-1", null }, FeeList = new ulong[] { 1, 2 } },
        };

        foreach(var payload in malformed)
        {
            Assert.Throws<PayoutOutcomeUncertainException>(() =>
                CryptonotePayoutHandler.ParseTransferSplitSuccess(payload));
        }
    }

    [Fact]
    public void ZanoMalformedSuccessesAreUncertain()
    {
        var malformed = new ZanoSplitResponse[]
        {
            null,
            new() { TxHashList = null },
            new() { TxHashList = Array.Empty<string>() },
            new() { TxHashList = new[] { "tx-1", " " } },
        };

        foreach(var payload in malformed)
        {
            Assert.Throws<PayoutOutcomeUncertainException>(() =>
                ZanoPayoutHandler.ParseTransferSplitSuccess(payload));
        }
    }

    [Fact]
    public async Task CryptonoteNullSuccessBodyDoesNotWritePaymentsOrBalances()
    {
        var fixture = CreateCryptonoteFixture();
        var response = new RpcResponse<CryptonoteSplitResponse>(null);

        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.HandleTransferSplitResponseAsync(response));

        await fixture.ConnectionFactory.DidNotReceive().OpenConnectionAsync();
        await fixture.PaymentRepository.DidNotReceiveWithAnyArgs()
            .TryBeginPaymentBatchAsync(default, default, default, default, default);
        await fixture.BalanceRepository.DidNotReceiveWithAnyArgs()
            .AddAmountAsync(default, default, default, default, default, default);
    }

    [Fact]
    public async Task ZanoNullSuccessBodyDoesNotWritePaymentsOrBalances()
    {
        var fixture = CreateZanoFixture();
        var response = new RpcResponse<ZanoSplitResponse>(null);

        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.HandleTransferSplitResponseAsync(response));

        await fixture.ConnectionFactory.DidNotReceive().OpenConnectionAsync();
        await fixture.PaymentRepository.DidNotReceiveWithAnyArgs()
            .TryBeginPaymentBatchAsync(default, default, default, default, default);
        await fixture.BalanceRepository.DidNotReceiveWithAnyArgs()
            .AddAmountAsync(default, default, default, default, default, default);
    }

    [Fact]
    public async Task ExplicitCryptonoteWalletRejectionRemainsConclusive()
    {
        var fixture = CreateCryptonoteFixture();
        InitializeHandler(fixture.Handler,
            new CryptonoteCoinTemplate { Symbol = "XMR", Name = "Monero" });
        var response = new RpcResponse<CryptonoteSplitResponse>(null,
            new JsonRpcError(-4, "transaction too large", null));

        Assert.False(await fixture.Handler.HandleTransferSplitResponseAsync(response));
        await fixture.ConnectionFactory.DidNotReceive().OpenConnectionAsync();
    }

    [Fact]
    public async Task ExplicitZanoWalletRejectionRemainsConclusive()
    {
        var fixture = CreateZanoFixture();
        InitializeHandler(fixture.Handler,
            new ZanoCoinTemplate { Symbol = "ZANO", Name = "Zano" });
        var response = new RpcResponse<ZanoSplitResponse>(null,
            new JsonRpcError(-4, "transaction too large", null));

        Assert.False(await fixture.Handler.HandleTransferSplitResponseAsync(response));
        await fixture.ConnectionFactory.DidNotReceive().OpenConnectionAsync();
    }

    private static HandlerFixture<CryptonotePayoutHandler> CreateCryptonoteFixture()
    {
        var dependencies = CreateDependencies();
        var handler = new CryptonotePayoutHandler(dependencies.Context,
            dependencies.ConnectionFactory, dependencies.Mapper, dependencies.ShareRepository,
            dependencies.BlockRepository, dependencies.BalanceRepository,
            dependencies.PaymentRepository, dependencies.Clock, dependencies.MessageBus);
        return dependencies.WithHandler(handler);
    }

    private static HandlerFixture<ZanoPayoutHandler> CreateZanoFixture()
    {
        var dependencies = CreateDependencies();
        var handler = new ZanoPayoutHandler(dependencies.Context,
            dependencies.ConnectionFactory, dependencies.Mapper, dependencies.ShareRepository,
            dependencies.BlockRepository, dependencies.BalanceRepository,
            dependencies.PaymentRepository, dependencies.Clock, dependencies.MessageBus);
        return dependencies.WithHandler(handler);
    }

    private static HandlerDependencies CreateDependencies() => new(
        Substitute.For<IComponentContext>(), Substitute.For<IConnectionFactory>(),
        Substitute.For<IMapper>(), Substitute.For<IShareRepository>(),
        Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
        Substitute.For<IPaymentRepository>(), Substitute.For<IMasterClock>(),
        Substitute.For<IMessageBus>());

    private static void InitializeHandler(object handler, CoinTemplate template)
    {
        var baseType = typeof(PayoutHandlerBase);
        baseType.GetField("logger", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)?.SetValue(handler, LogManager.GetCurrentClassLogger());
        baseType.GetField("poolConfig", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)?.SetValue(handler,
            new PoolConfig { Id = "pool", Template = template });
    }

    private sealed record HandlerDependencies(IComponentContext Context,
        IConnectionFactory ConnectionFactory, IMapper Mapper,
        IShareRepository ShareRepository, IBlockRepository BlockRepository,
        IBalanceRepository BalanceRepository, IPaymentRepository PaymentRepository,
        IMasterClock Clock, IMessageBus MessageBus)
    {
        public HandlerFixture<T> WithHandler<T>(T handler) => new(handler,
            ConnectionFactory, BalanceRepository, PaymentRepository);
    }

    private sealed record HandlerFixture<T>(T Handler,
        IConnectionFactory ConnectionFactory, IBalanceRepository BalanceRepository,
        IPaymentRepository PaymentRepository);
}
