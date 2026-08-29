using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Persistence.Repositories;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using NSubstitute;
using Xunit;
using PersistedShare = Miningcore.Persistence.Model.Share;
using Miningcore.Tests.Blockchain.Bitcoin;

namespace Miningcore.Tests.Payments;

[Collection(BitcoinCorePayoutIntegrationCollection.Name)]
public class MergedMiningPayoutRegtestTests
{
    [MergedMiningDaemonIntegrationFact]
    public async Task PinnedDaemons_MatureRealRewardsAcrossEverySupportedScheme()
    {
        await using var litecoin = await RegtestNode.StartAsync(
            RegtestChain.Litecoin);
        await using var dogecoin = await RegtestNode.StartAsync(
            RegtestChain.Dogecoin);

        var litecoinReward = await litecoin.MineMatureRewardAsync();
        var dogecoinReward = await dogecoin.MineMatureRewardAsync();

        Assert.True(litecoinReward.Confirmations > 100);
        Assert.True(dogecoinReward.Confirmations > 30);
        Assert.True(litecoinReward.Reward > 0);
        Assert.True(dogecoinReward.Reward > 0);

        await AssertBlockSchemesCreateExactBalancesAsync("ltc-regtest",
            litecoinReward);
        await AssertBlockSchemesCreateExactBalancesAsync("doge-regtest",
            dogecoinReward);
        await AssertPpsCreatesExactlyOnceBalancesAsync(litecoinReward,
            dogecoinReward);

        // Exercise a mixed parent/auxiliary selection against the same mature daemon evidence.
        await AssertSchemeCreatesExactBalanceAsync("ltc-mixed", PayoutScheme.PPLNS,
            litecoinReward);
        await AssertSchemeCreatesExactBalanceAsync("doge-mixed", PayoutScheme.PROP,
            dogecoinReward);
    }

    private static async Task AssertBlockSchemesCreateExactBalancesAsync(string poolId,
        MatureReward reward)
    {
        foreach(var scheme in new[]
                { PayoutScheme.SOLO, PayoutScheme.PROP, PayoutScheme.PPLNS })
            await AssertSchemeCreatesExactBalanceAsync($"{poolId}-{scheme}", scheme,
                reward);
    }

    private static async Task AssertSchemeCreatesExactBalanceAsync(string poolId,
        PayoutScheme scheme, MatureReward evidence)
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var readConnection = Substitute.For<IDbConnection>();
        var writeConnection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var balanceRepository = Substitute.For<IBalanceRepository>();
        var payoutHandler = Substitute.For<IPayoutHandler>();
        var credits = new List<decimal>();
        var created = DateTime.UtcNow;
        var block = new Block
        {
            PoolId = poolId,
            BlockHeight = evidence.Height,
            Miner = $"{poolId}-finder",
            Created = created,
        };
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(new PoolConfig
        {
            Id = poolId,
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = scheme,
                PayoutSchemeConfig = scheme == PayoutScheme.PPLNS
                    ? JObject.FromObject(new { Factor = 1m })
                    : null,
            },
        });
        connectionFactory.OpenConnectionAsync().Returns(readConnection);
        payoutHandler.AdjustShareDifficulty(Arg.Any<double>())
            .Returns(x => x.Arg<double>());
        payoutHandler.FormatAmount(Arg.Any<decimal>())
            .Returns(x => x.Arg<decimal>().ToString(CultureInfo.InvariantCulture));
        balanceRepository.AddAmountAsync(writeConnection, transaction, poolId,
                Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>())
            .Returns(call =>
            {
                credits.Add(call.ArgAt<decimal>(4));
                return Task.CompletedTask;
            });
        shareRepository.CountSharesByMinerAsync(writeConnection, transaction,
                poolId, block.Miner, Arg.Any<CancellationToken>())
            .Returns(0L);
        shareRepository.CountSharesBeforeInclusiveAsync(writeConnection,
                transaction, poolId, created, Arg.Any<CancellationToken>())
            .Returns(0L);
        shareRepository.CountSharesBeforeAsync(writeConnection, transaction,
                poolId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(0L);
        shareRepository.ReadSharesBeforeAsync(readConnection, poolId, created,
                true, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                CreateShare(poolId, $"{poolId}-a", 40, 100, created),
                CreateShare(poolId, $"{poolId}-b", 60, 100,
                    created.AddSeconds(-1)),
            });

        IPayoutScheme payoutScheme = scheme switch
        {
            PayoutScheme.SOLO => new SOLOPaymentScheme(shareRepository,
                balanceRepository),
            PayoutScheme.PROP => new PROPPaymentScheme(connectionFactory,
                shareRepository, blockRepository, balanceRepository),
            PayoutScheme.PPLNS => new PPLNSPaymentScheme(connectionFactory,
                shareRepository, blockRepository, balanceRepository),
            _ => throw new ArgumentOutOfRangeException(nameof(scheme)),
        };
        await payoutScheme.UpdateBalancesAsync(writeConnection, transaction, pool,
            payoutHandler, block, evidence.Reward, CancellationToken.None);

        Assert.NotEmpty(credits);
        Assert.Equal(evidence.Reward, credits.Sum());
    }

    private static async Task AssertPpsCreatesExactlyOnceBalancesAsync(
        MatureReward litecoin, MatureReward dogecoin)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");
        var schema = $"miningcore_merged_payout_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            await connection.ExecuteAsync($@"
                CREATE SCHEMA {schema};
                SET search_path TO {schema}, public;
                CREATE TABLE shares(
                    poolid text NOT NULL, blockheight bigint NOT NULL,
                    difficulty double precision NOT NULL,
                    networkdifficulty double precision NOT NULL,
                    sharedifficulty double precision NULL,
                    actualdifficulty double precision NULL,
                    miner text NOT NULL, worker text NULL, useragent text NULL,
                    ipaddress text NOT NULL, source text NULL, sessionid text NULL,
                    created timestamptz NOT NULL);
                CREATE TABLE balances(
                    poolid text NOT NULL, address text NOT NULL,
                    amount decimal(28,12) NOT NULL DEFAULT 0,
                    created timestamptz NOT NULL, updated timestamptz NOT NULL,
                    PRIMARY KEY(poolid, address));
                CREATE TABLE balance_changes(
                    id bigserial PRIMARY KEY, poolid text NOT NULL,
                    address text NOT NULL, amount decimal(28,12) NOT NULL,
                    usage text NULL, tags text[] NULL, created timestamptz NOT NULL);
            ");
            var migrationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../Miningcore/Persistence/Postgres/Scripts/add_share_accounting.sql"));
            var migration = (await File.ReadAllTextAsync(migrationPath))
                .Replace("\\set ON_ERROR_STOP on", string.Empty,
                    StringComparison.Ordinal);
            await connection.ExecuteAsync(migration);
            var repository = new ShareRepository(AutoMapperFactory.CreateMapper());

            await InsertPpsProjectionAsync(repository, connection, "ltc-pps",
                "ltc-miner", litecoin);
            await InsertPpsProjectionAsync(repository, connection, "doge-pps",
                "doge-miner", dogecoin);

            foreach(var poolId in new[] { "ltc-pps", "doge-pps" })
            {
                var balance = await connection.ExecuteScalarAsync<decimal>(
                    "SELECT amount FROM balances WHERE poolid=@poolId",
                    new { poolId });
                Assert.True(balance > 0);
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM pps_share_credits WHERE poolid=@poolId",
                    new { poolId }));
            }
        }
        finally
        {
            await connection.ExecuteAsync("ROLLBACK; SET search_path TO public");
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    private static async Task InsertPpsProjectionAsync(ShareRepository repository,
        NpgsqlConnection connection, string poolId, string miner, MatureReward evidence)
    {
        var accountingId = Guid.NewGuid();
        var created = DateTime.UtcNow;
        var rewardSatoshis = checked((long) decimal.Round(
            evidence.Reward * 100_000_000m, 0, MidpointRounding.ToZero));
        var share = new Miningcore.Persistence.Model.Share
        {
            PoolId = poolId,
            BlockHeight = evidence.Height,
            Miner = miner,
            Worker = "regtest",
            UserAgent = "daemon-backed-regtest",
            Difficulty = 1,
            ShareDifficulty = 1,
            ActualDifficulty = 1,
            NetworkDifficulty = 100,
            IpAddress = IPAddress.Loopback.ToString(),
            Source = "regtest",
            SessionId = $"{poolId}-session",
            AccountingId = accountingId,
            AccountingRole = (short) ShareAccountingRole.Single,
            RewardBasisSatoshis = rewardSatoshis,
            Created = created,
        };
        var credit = new PpsShareCredit
        {
            PoolId = poolId,
            AccountingId = accountingId,
            Address = miner,
            CalculatedAmount = decimal.Round(evidence.Reward / 100m, 24,
                MidpointRounding.ToZero),
            Difficulty = 1,
            NetworkDifficulty = 100,
            RewardBasisSatoshis = rewardSatoshis,
            Created = created,
        };
        var batch = new ShareAccountingBatch
        {
            AccountingId = accountingId,
            Shares = new[] { share },
            PpsCredits = new[] { credit },
            Created = created,
        };
        batch = batch with
        {
            PayloadHash = ShareAccounting.ComputePayloadHash(accountingId,
                batch.Shares, batch.PpsCredits),
        };

        await using var transaction = await connection.BeginTransactionAsync();
        Assert.Equal(ShareAccountingInsertResult.Inserted,
            await repository.InsertAccountingBatchAsync(connection, transaction,
                batch, CancellationToken.None));
        Assert.Equal(ShareAccountingInsertResult.AlreadyCommitted,
            await repository.InsertAccountingBatchAsync(connection, transaction,
                batch, CancellationToken.None));
        await transaction.CommitAsync();
    }

    private static PersistedShare CreateShare(string poolId, string miner, double difficulty,
        double networkDifficulty, DateTime created) => new()
    {
        PoolId = poolId,
        Miner = miner,
        Difficulty = difficulty,
        NetworkDifficulty = networkDifficulty,
        Created = created,
    };

    private enum RegtestChain
    {
        Litecoin,
        Dogecoin,
    }

    private sealed record MatureReward(ulong Height, int Confirmations,
        decimal Reward);

    private sealed class RegtestNode : IAsyncDisposable
    {
        private const string RpcUser = "miningcore";
        private const string RpcPassword = "miningcore-regtest";
        private readonly RegtestChain chain;
        private readonly Process process;
        private readonly HttpClient httpClient = new();
        private readonly ConcurrentQueue<string> output = new();
        private readonly string dataDirectory;
        private readonly Uri rootUri;
        private Uri walletUri;

        private RegtestNode(RegtestChain chain, Process process,
            string dataDirectory, int rpcPort)
        {
            this.chain = chain;
            this.process = process;
            this.dataDirectory = dataDirectory;
            rootUri = new Uri($"http://127.0.0.1:{rpcPort}/");
            walletUri = rootUri;
            httpClient.Timeout = TimeSpan.FromSeconds(15);
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{RpcUser}:{RpcPassword}")));
            process.OutputDataReceived += (_, args) => Record(args.Data);
            process.ErrorDataReceived += (_, args) => Record(args.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public static async Task<RegtestNode> StartAsync(RegtestChain chain)
        {
            var variable = chain == RegtestChain.Litecoin
                ? MergedMiningDaemonIntegrationFactAttribute
                    .LitecoinBinaryEnvironmentVariable
                : MergedMiningDaemonIntegrationFactAttribute
                    .DogecoinBinaryEnvironmentVariable;
            var binary = Environment.GetEnvironmentVariable(variable);
            var directory = Path.Combine(Path.GetTempPath(),
                $"miningcore-{chain.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var rpcPort = GetAvailablePort();
            var peerPort = GetAvailablePort();
            var start = new ProcessStartInfo(binary)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach(var argument in new[]
            {
                "-regtest=1", "-server=1", "-listen=0", "-txindex=1",
                "-printtoconsole=1", $"-datadir={directory}",
                $"-rpcport={rpcPort}", $"-port={peerPort}",
                "-rpcbind=127.0.0.1", "-rpcallowip=127.0.0.1",
                $"-rpcuser={RpcUser}", $"-rpcpassword={RpcPassword}",
            })
                start.ArgumentList.Add(argument);

            var process = Process.Start(start) ??
                throw new InvalidOperationException($"Unable to start {chain} daemon");
            var node = new RegtestNode(chain, process, directory, rpcPort);

            try
            {
                await node.WaitUntilReadyAsync();
                if(chain == RegtestChain.Litecoin)
                {
                    await node.RpcAsync("createwallet", false, "miningcore");
                    node.walletUri = new Uri(node.rootUri, "wallet/miningcore");
                }

                return node;
            }
            catch
            {
                await node.DisposeAsync();
                throw;
            }
        }

        public async Task<MatureReward> MineMatureRewardAsync()
        {
            var address = (await RpcAsync("getnewaddress", true)).Value<string>();
            string firstHash;

            if(chain == RegtestChain.Litecoin)
            {
                firstHash = (await RpcAsync("generatetoaddress", false, 1, address))
                    .First.Value<string>();
                await RpcAsync("generatetoaddress", false, 110, address);
            }
            else
            {
                firstHash = (await RpcAsync("generate", true, 1))
                    .First.Value<string>();
                await RpcAsync("generate", true, 110);
            }

            var block = await RpcAsync("getblock", false, firstHash);
            var txId = block["tx"]!.First.Value<string>();
            var transaction = await RpcAsync("getrawtransaction", false, txId, true);
            var reward = transaction["vout"]!.Sum(x => x["value"]!.Value<decimal>());
            return new MatureReward(block["height"]!.Value<ulong>(),
                block["confirmations"]!.Value<int>(), reward);
        }

        private async Task WaitUntilReadyAsync()
        {
            Exception last = null;
            for(var attempt = 0; attempt < 120; attempt++)
            {
                if(process.HasExited)
                    throw new InvalidOperationException(
                        $"{chain} daemon exited: {string.Join(Environment.NewLine, output)}");

                try
                {
                    await RpcAsync("getblockchaininfo", false);
                    return;
                }
                catch(Exception ex) when(ex is HttpRequestException or
                    TaskCanceledException or InvalidOperationException)
                {
                    last = ex;
                    await Task.Delay(250);
                }
            }

            throw new TimeoutException($"{chain} daemon did not become ready", last);
        }

        private async Task<JToken> RpcAsync(string method, bool wallet,
            params object[] parameters)
        {
            var request = new JObject
            {
                ["jsonrpc"] = "1.0",
                ["id"] = Guid.NewGuid().ToString("N"),
                ["method"] = method,
                ["params"] = JArray.FromObject(parameters ?? Array.Empty<object>()),
            };
            using var content = new StringContent(request.ToString(Formatting.None),
                Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync(wallet ? walletUri : rootUri,
                content);
            var json = JObject.Parse(await response.Content.ReadAsStringAsync());
            if(!response.IsSuccessStatusCode || json["error"]?.Type is not
                   (JTokenType.Null or JTokenType.Undefined))
                throw new InvalidOperationException(
                    $"{chain} RPC {method} failed: {json["error"]}");

            return json["result"]!;
        }

        private static int GetAvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint) listener.LocalEndpoint).Port;
        }

        private void Record(string line)
        {
            if(!string.IsNullOrWhiteSpace(line))
                output.Enqueue(line);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if(!process.HasExited)
                    await RpcAsync("stop", false);
            }
            catch
            {
                // Bounded process termination below is authoritative.
            }

            if(!process.HasExited)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    await process.WaitForExitAsync(deadline.Token);
                }
                catch(OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }

            httpClient.Dispose();
            process.Dispose();
            if(Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, true);
        }
    }
}
