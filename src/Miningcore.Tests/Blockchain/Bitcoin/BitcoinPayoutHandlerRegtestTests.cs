using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

[Collection(BitcoinCorePayoutIntegrationCollection.Name)]
public class BitcoinPayoutHandlerRegtestTests : TestBase
{
    [BitcoinCoreIntegrationFact]
    public async Task WalletBroadcastEnabled_AcceptsInitiallyUnbroadcastMempoolTransaction()
    {
        await using var node = await BitcoinCoreRegtestNode.StartAsync(walletBroadcast: true);
        var fixture = await CreateFixtureAsync(node);
        var destination = await node.GetNewAddressAsync();

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = destination, Amount = 1 },
        }, CancellationToken.None);

        var txId = Assert.Single(fixture.Handler.SubmittedTransactionIds);
        var mempoolEntry = await node.WalletRpcAsync("getmempoolentry", txId);

        Assert.True(mempoolEntry.Value<bool>("unbroadcast"));
        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            txId, Arg.Any<DateTime>());
    }

    [BitcoinCoreIntegrationFact]
    public async Task WalletBroadcastDisabled_FailsAfterBoundedVerificationRetries()
    {
        await using var node = await BitcoinCoreRegtestNode.StartAsync(walletBroadcast: false);
        var fixture = await CreateFixtureAsync(node);
        var destination = await node.GetNewAddressAsync();

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = destination, Amount = 1 },
            }, CancellationToken.None));

        var txId = Assert.Single(fixture.Handler.SubmittedTransactionIds);
        var mempoolLookup = await node.TryWalletRpcAsync("getmempoolentry", txId);
        var walletTransaction = await node.WalletRpcAsync("gettransaction", txId);

        Assert.Equal((int) BitcoinRPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY,
            mempoolLookup.Error?.Value<int>("code"));
        Assert.Equal(0, walletTransaction.Value<int>("confirmations"));
        Assert.Contains(txId, exception.Message);
        Assert.Contains("after 3 verification attempts", exception.Message);
    }

    [BitcoinCoreIntegrationFact]
    public async Task ConfirmationBetweenMempoolAndWalletQueries_CompletesPayout()
    {
        await using var node = await BitcoinCoreRegtestNode.StartAsync(walletBroadcast: false);
        var fixture = await CreateFixtureAsync(node);
        var destination = await node.GetNewAddressAsync();

        fixture.Handler.BeforeWalletTransactionAsync = async (txId, ct) =>
        {
            var transaction = await node.WalletRpcAsync("gettransaction", ct, txId);
            await node.RootRpcAsync("sendrawtransaction", ct,
                transaction.Value<string>("hex"));
            await node.GenerateAsync(1, ct);
        };

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = destination, Amount = 1 },
        }, CancellationToken.None);

        var txId = Assert.Single(fixture.Handler.SubmittedTransactionIds);
        var walletTransaction = await node.WalletRpcAsync("gettransaction", txId);

        Assert.True(walletTransaction.Value<int>("confirmations") > 0);
    }

    [BitcoinCoreIntegrationFact]
    public async Task BrokenSendMany_ReportsEveryWalletOnlyTransaction()
    {
        await using var node = await BitcoinCoreRegtestNode.StartAsync(walletBroadcast: false);
        var fixture = await CreateFixtureAsync(node, hasBrokenSendMany: true);
        var destination1 = await node.GetNewAddressAsync();
        var destination2 = await node.GetNewAddressAsync();

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = destination1, Amount = 1 },
                new Balance { PoolId = fixture.Config.Id, Address = destination2, Amount = 2 },
            }, CancellationToken.None));

        var txIds = fixture.Handler.SubmittedTransactionIds.Distinct().OrderBy(x => x).ToArray();
        Assert.Equal(2, txIds.Length);

        foreach(var txId in txIds)
        {
            Assert.Contains(txId, exception.Message);
            var mempoolLookup = await node.TryWalletRpcAsync("getmempoolentry", txId);
            var walletTransaction = await node.WalletRpcAsync("gettransaction", txId);

            Assert.Equal((int) BitcoinRPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY,
                mempoolLookup.Error?.Value<int>("code"));
            Assert.Equal(0, walletTransaction.Value<int>("confirmations"));
        }

        await fixture.PaymentRepository.Received(2).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            Arg.Any<string>(), Arg.Any<DateTime>());
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Is<PaymentNotification>(notification => notification.Error == null),
            Arg.Any<string>());
    }

    private async Task<HandlerFixture> CreateFixtureAsync(BitcoinCoreRegtestNode node,
        bool hasBrokenSendMany = false)
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        connectionFactory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);

        var paymentRepository = Substitute.For<IPaymentRepository>();
        paymentRepository.TryBeginPaymentBatchAsync(Arg.Any<IDbConnection>(),
            Arg.Any<IDbTransaction>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>())
            .Returns(true);

        var messageBus = Substitute.For<IMessageBus>();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var config = new PoolConfig
        {
            Id = $"bitcoin-regtest-{Guid.NewGuid():N}",
            Template = ModuleInitializer.CoinTemplates["bitcoin"],
            Daemons = new[] { node.WalletEndpoint },
            PaymentProcessing = new PoolPaymentProcessingConfig(),
            RewardRecipients = Array.Empty<RewardRecipient>(),
            Extra = hasBrokenSendMany
                ? new Dictionary<string, object> { ["hasBrokenSendMany"] = true }
                : null,
        };
        var handler = new RegtestBitcoinPayoutHandler(container, connectionFactory,
            container.Resolve<IMapper>(), Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
            paymentRepository, clock, messageBus, new ActiveBlockGracePeriodTracker());
        await handler.ConfigureAsync(new ClusterConfig(), config, CancellationToken.None);

        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(config);

        return new HandlerFixture(handler, pool, config, paymentRepository, messageBus);
    }

    private sealed record HandlerFixture(RegtestBitcoinPayoutHandler Handler, IMiningPool Pool,
        PoolConfig Config, IPaymentRepository PaymentRepository, IMessageBus MessageBus);

    private sealed class RegtestBitcoinPayoutHandler : BitcoinPayoutHandler
    {
        public RegtestBitcoinPayoutHandler(IComponentContext ctx, IConnectionFactory cf,
            IMapper mapper, IShareRepository shareRepo, IBlockRepository blockRepo,
            IBalanceRepository balanceRepo, IPaymentRepository paymentRepo, IMasterClock clock,
            IMessageBus messageBus, IActiveBlockGracePeriodTracker activeBlockGracePeriodTracker) :
            base(ctx, cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus,
                activeBlockGracePeriodTracker)
        {
        }

        public ConcurrentBag<string> SubmittedTransactionIds { get; } = new();
        public Func<string, CancellationToken, Task> BeforeWalletTransactionAsync { get; set; }

        protected override async Task<RpcResponse<string>> SendManyAsync(object[] args,
            CancellationToken ct)
        {
            var response = await base.SendManyAsync(args, ct);

            if(response.Error == null && !string.IsNullOrEmpty(response.Response))
                SubmittedTransactionIds.Add(response.Response);

            return response;
        }

        protected override async Task<RpcResponse<string>> SendToAddressAsync(object[] args,
            CancellationToken ct)
        {
            var response = await base.SendToAddressAsync(args, ct);

            if(response.Error == null && !string.IsNullOrEmpty(response.Response))
                SubmittedTransactionIds.Add(response.Response);

            return response;
        }

        protected override async Task<RpcResponse<Transaction>> GetWalletTransactionAsync(
            string txId, CancellationToken ct)
        {
            var callback = BeforeWalletTransactionAsync;

            if(callback != null)
            {
                BeforeWalletTransactionAsync = null;
                await callback(txId, ct);
            }

            return await base.GetWalletTransactionAsync(txId, ct);
        }
    }

    internal sealed class BitcoinCoreRegtestNode : IAsyncDisposable
    {
        private const string RpcUser = "miningcore";
        private const string RpcPassword = "miningcore-regtest";
        private const string WalletName = "miningcore";
        private readonly Process process;
        private readonly HttpClient httpClient = new();
        private readonly ConcurrentQueue<string> processOutput = new();

        private BitcoinCoreRegtestNode(Process process, string dataDirectory, int rpcPort)
        {
            this.process = process;
            DataDirectory = dataDirectory;
            RpcPort = rpcPort;
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{RpcUser}:{RpcPassword}")));

            process.OutputDataReceived += (_, args) => RecordProcessOutput(args.Data);
            process.ErrorDataReceived += (_, args) => RecordProcessOutput(args.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public string DataDirectory { get; }
        public int RpcPort { get; }

        public DaemonEndpointConfig WalletEndpoint => new()
        {
            Host = IPAddress.Loopback.ToString(),
            Port = RpcPort,
            User = RpcUser,
            Password = RpcPassword,
            HttpPath = $"/wallet/{WalletName}",
        };

        public static async Task<BitcoinCoreRegtestNode> StartAsync(bool walletBroadcast,
            string binaryOverride = null, string[] extraArguments = null, int initialBlocks = 110)
        {
            var binary = binaryOverride ?? Environment.GetEnvironmentVariable(
                BitcoinCoreIntegrationFactAttribute.BinaryEnvironmentVariable);
            var dataDirectory = Path.Combine(Path.GetTempPath(),
                $"miningcore-bitcoin-regtest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);

            var rpcPort = GetAvailablePort();
            var peerPort = GetAvailablePort();
            var startInfo = new ProcessStartInfo(binary)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach(var argument in new[]
            {
                "-regtest=1",
                "-server=1",
                "-listen=0",
                "-printtoconsole=1",
                $"-datadir={dataDirectory}",
                $"-rpcport={rpcPort}",
                $"-port={peerPort}",
                "-rpcbind=127.0.0.1",
                "-rpcallowip=127.0.0.1",
                $"-rpcuser={RpcUser}",
                $"-rpcpassword={RpcPassword}",
                "-fallbackfee=0.0002",
                $"-walletbroadcast={(walletBroadcast ? 1 : 0)}",
            })
                startInfo.ArgumentList.Add(argument);

            foreach(var argument in extraArguments ?? Array.Empty<string>())
                startInfo.ArgumentList.Add(argument);

            var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Unable to start bitcoind");
            var node = new BitcoinCoreRegtestNode(process, dataDirectory, rpcPort);

            try
            {
                await node.WaitUntilReadyAsync();
                await node.RootRpcAsync("createwallet", WalletName);
                // Fund the wallet with several mature coinbase outputs so parallel
                // sendtoaddress calls cannot contend for a single spendable UTXO.
                if(initialBlocks > 0)
                    await node.GenerateAsync(initialBlocks, CancellationToken.None);
                return node;
            }
            catch
            {
                await node.DisposeAsync();
                throw;
            }
        }

        public async Task<string> GetNewAddressAsync(CancellationToken ct = default) =>
            (await WalletRpcAsync("getnewaddress", ct)).Value<string>();

        public async Task GenerateAsync(int blocks, CancellationToken ct)
        {
            var address = await GetNewAddressAsync(ct);
            await RootRpcAsync("generatetoaddress", ct, blocks, address);
        }

        public Task<JToken> RootRpcAsync(string method, params object[] parameters) =>
            RootRpcAsync(method, CancellationToken.None, parameters);

        public async Task<JToken> RootRpcAsync(string method, CancellationToken ct,
            params object[] parameters)
        {
            var response = await TryRpcAsync(method, wallet: false, ct, parameters);
            return GetSuccessfulResult(method, response);
        }

        public Task<JToken> WalletRpcAsync(string method, params object[] parameters) =>
            WalletRpcAsync(method, CancellationToken.None, parameters);

        public async Task<JToken> WalletRpcAsync(string method, CancellationToken ct,
            params object[] parameters)
        {
            var response = await TryRpcAsync(method, wallet: true, ct, parameters);
            return GetSuccessfulResult(method, response);
        }

        public Task<RpcEnvelope> TryWalletRpcAsync(string method, params object[] parameters) =>
            TryRpcAsync(method, wallet: true, CancellationToken.None, parameters);

        public async ValueTask DisposeAsync()
        {
            try
            {
                if(!process.HasExited)
                    await RootRpcAsync("stop");
            }
            catch
            {
                // Fall through to bounded process termination.
            }

            if(!process.HasExited)
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                try
                {
                    await process.WaitForExitAsync(shutdown.Token);
                }
                catch(OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }

            process.Dispose();
            httpClient.Dispose();

            if(Directory.Exists(DataDirectory))
                Directory.Delete(DataDirectory, recursive: true);
        }

        private async Task WaitUntilReadyAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            while(!timeout.IsCancellationRequested)
            {
                if(process.HasExited)
                    throw new InvalidOperationException(
                        $"bitcoind exited with code {process.ExitCode}: {string.Join(Environment.NewLine, processOutput)}");

                try
                {
                    await RootRpcAsync("getblockchaininfo", timeout.Token);
                    return;
                }
                catch when(!timeout.IsCancellationRequested)
                {
                    await Task.Delay(100, timeout.Token);
                }
            }

            throw new TimeoutException(
                $"bitcoind did not become ready: {string.Join(Environment.NewLine, processOutput)}");
        }

        private async Task<RpcEnvelope> TryRpcAsync(string method, bool wallet,
            CancellationToken ct, params object[] parameters)
        {
            var request = new JObject
            {
                ["jsonrpc"] = "1.0",
                ["id"] = Guid.NewGuid().ToString("N"),
                ["method"] = method,
                ["params"] = JArray.FromObject(parameters ?? Array.Empty<object>()),
            };
            var path = wallet ? $"/wallet/{WalletName}" : "/";
            using var content = new StringContent(request.ToString(Formatting.None),
                Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync(
                $"http://127.0.0.1:{RpcPort}{path}", content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var envelope = JObject.Parse(responseBody);

            return new RpcEnvelope(envelope["result"], envelope["error"] as JObject);
        }

        private static JToken GetSuccessfulResult(string method, RpcEnvelope response)
        {
            if(response.Error != null)
            {
                throw new InvalidOperationException(
                    $"Bitcoin Core RPC {method} failed with {response.Error.Value<int>("code")}: " +
                    response.Error.Value<string>("message"));
            }

            return response.Result;
        }

        private static int GetAvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint) listener.LocalEndpoint).Port;
        }

        private void RecordProcessOutput(string line)
        {
            if(!string.IsNullOrWhiteSpace(line))
                processOutput.Enqueue(line);
        }

        public sealed record RpcEnvelope(JToken Result, JObject Error);
    }
}
