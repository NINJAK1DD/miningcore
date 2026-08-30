using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class BlockRepositoryTests
{
    [Fact]
    public async Task DeclaredBlockOnlyCandidateTypesAllHaveConflictRules()
    {
        var expectedTypes = new[]
        {
            "bitcoin-direct",
            "auxpow",
            "auxpow-claim",
            "merged-parent",
            "merged-parent-uncertain",
        };
        Assert.Equal(expectedTypes.OrderBy(x => x, StringComparer.Ordinal),
            BlockOnlyCandidatePersistenceRules.DeclaredTypes
                .OrderBy(x => x, StringComparer.Ordinal));
        var repository = new BlockRepository(AutoMapperFactory.CreateMapper());

        foreach(var type in expectedTypes)
        {
            Assert.True(BlockOnlyCandidatePersistenceRules.TryGet(type,
                out var rule));
            Assert.True(BlockOnlyCandidatePersistenceRules
                .RequiresIdempotencyIndexes(new global::Miningcore.Blockchain.Share
                {
                    IsBlockCandidate = true,
                    BlockType = type,
                }));
            var connection = new RecordingDbConnection();

            await repository.InsertAsync(connection, null, new Block
            {
                PoolId = "idempotency-test",
                BlockHeight = 100,
                Type = type,
                Hash = type + "-hash",
                Status = BlockStatus.Pending,
                TransactionConfirmationData = type + ":identity:0",
                Created = DateTime.UtcNow,
            });

            Assert.Contains(rule.ConflictClause, connection.CommandText,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RecoveryIndexRequirementExcludesRecordsThatCannotInsertBlocks()
    {
        Assert.False(BlockOnlyCandidatePersistenceRules
            .RequiresIdempotencyIndexes(new global::Miningcore.Blockchain.Share
            {
                IsBlockCandidate = false,
                BlockType = "auxpow",
            }));
        Assert.False(BlockOnlyCandidatePersistenceRules
            .RequiresIdempotencyIndexes(new global::Miningcore.Blockchain.Share
            {
                IsBlockCandidate = true,
                BlockRecordEmitted = true,
                BlockType = "auxpow",
            }));
        Assert.False(BlockOnlyCandidatePersistenceRules
            .RequiresIdempotencyIndexes(new global::Miningcore.Blockchain.Share
            {
                IsBlockCandidate = true,
                BlockType = "ordinary",
            }));
    }

    [Fact]
    public async Task InsertAsync_DeduplicatesAuxPowBlocksByPoolAndHash()
    {
        var mapper = AutoMapperFactory.CreateMapper();
        var repository = new BlockRepository(mapper);
        var connection = new RecordingDbConnection();
        var block = new Block
        {
            PoolId = "doge-test",
            BlockHeight = 100,
            Type = "auxpow",
            Hash = "doge-block",
            Status = BlockStatus.Pending,
            TransactionConfirmationData = "auxpow-uncertain:doge-block",
            Created = DateTime.UtcNow,
        };

        var inserted = await repository.InsertAsync(connection, null, block);

        Assert.True(inserted);
        Assert.Contains("ON CONFLICT (poolid, hash) WHERE type = 'auxpow' DO NOTHING",
            connection.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("auxpow", connection.Parameters["type"]);
        Assert.Equal(block.Hash, connection.Parameters["hash"]);
    }

    [Fact]
    public async Task InsertAsync_DeduplicatesOnlyIdenticalAuxPowClaims()
    {
        var mapper = AutoMapperFactory.CreateMapper();
        var repository = new BlockRepository(mapper);
        var connection = new RecordingDbConnection();
        var block = new Block
        {
            PoolId = "doge-test",
            BlockHeight = 100,
            Type = "auxpow-claim",
            Hash = "doge-block",
            Status = BlockStatus.Pending,
            TransactionConfirmationData = "auxpow-claim:doge-block:parent-header:0",
            Created = DateTime.UtcNow,
        };

        var inserted = await repository.InsertAsync(connection, null, block);

        Assert.True(inserted);
        Assert.Contains("ON CONFLICT (poolid, hash, (regexp_replace(transactionconfirmationdata, ':[0-9]+$', ''))) WHERE type = 'auxpow-claim' DO NOTHING",
            connection.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InsertAsync_OrdinaryBlocksDoNotRequireAuxPowIndex()
    {
        var mapper = AutoMapperFactory.CreateMapper();
        var repository = new BlockRepository(mapper);
        var connection = new RecordingDbConnection();
        var block = new Block
        {
            PoolId = "btc-test",
            BlockHeight = 100,
            Hash = "btc-block",
            Status = BlockStatus.Pending,
            TransactionConfirmationData = "coinbase-txid",
            Created = DateTime.UtcNow,
        };

        var inserted = await repository.InsertAsync(connection, null, block);

        Assert.True(inserted);
        Assert.DoesNotContain("ON CONFLICT", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("merged-parent")]
    [InlineData("merged-parent-uncertain")]
    public async Task InsertAsync_DeduplicatesMergedParentBlocksByPoolAndHash(string type)
    {
        var mapper = AutoMapperFactory.CreateMapper();
        var repository = new BlockRepository(mapper);
        var connection = new RecordingDbConnection();
        var block = new Block
        {
            PoolId = "ltc-test",
            BlockHeight = 100,
            Type = type,
            Hash = "ltc-block",
            Status = BlockStatus.Pending,
            TransactionConfirmationData = "coinbase-or-marker",
            Created = DateTime.UtcNow,
        };

        var inserted = await repository.InsertAsync(connection, null, block);

        Assert.True(inserted);
        Assert.Contains("ON CONFLICT (poolid, hash) WHERE type IN ('merged-parent', 'merged-parent-uncertain') DO NOTHING",
            connection.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GetPoolBlockCountAsync")]
    [InlineData("GetTotalPendingBlocksAsync")]
    [InlineData("GetMinerBlockCountAsync")]
    [InlineData("GetLastPoolBlockTimeAsync")]
    [InlineData("GetLastMinerBlockTimeAsync")]
    public async Task PublicStatistics_ExcludeUnresolvedMergedMiningClaims(string method)
    {
        var mapper = AutoMapperFactory.CreateMapper();
        var repository = new BlockRepository(mapper);
        var connection = new RecordingDbConnection { ScalarResult = 0 };

        switch(method)
        {
            case "GetPoolBlockCountAsync":
                connection.ScalarResult = 0;
                await repository.GetPoolBlockCountAsync(connection, "doge-test", CancellationToken.None);
                break;
            case "GetTotalPendingBlocksAsync":
                connection.ScalarResult = 0;
                await repository.GetTotalPendingBlocksAsync(connection, "doge-test", CancellationToken.None);
                break;
            case "GetMinerBlockCountAsync":
                connection.ScalarResult = 0;
                await repository.GetMinerBlockCountAsync(connection, "doge-test", "miner", CancellationToken.None);
                break;
            case "GetLastPoolBlockTimeAsync":
                connection.ScalarResult = DateTime.UtcNow;
                await repository.GetLastPoolBlockTimeAsync(connection, "doge-test", CancellationToken.None);
                break;
            case "GetLastMinerBlockTimeAsync":
                connection.ScalarResult = DateTime.UtcNow;
                await repository.GetLastMinerBlockTimeAsync(connection, "doge-test", "miner", CancellationToken.None);
                break;
        }

        Assert.Contains("type NOT IN ('auxpow-claim', 'parent-uncertain', 'merged-parent-uncertain')",
            connection.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HasMergedMiningBlockIndexesAsync_ChecksAllRequiredIndexes()
    {
        var mapper = AutoMapperFactory.CreateMapper();
        var repository = new BlockRepository(mapper);
        var connection = new RecordingDbConnection { ScalarResult = true };

        var result = await repository.HasMergedMiningBlockIndexesAsync(connection,
            CancellationToken.None);

        Assert.True(result);
        Assert.Contains("idx_blocks_auxpow_pool_hash", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idx_blocks_auxpow_claim", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idx_blocks_merged_parent_pool_hash", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idx_blocks_bitcoin_direct_pool_hash", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_index", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("indisunique", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("indisvalid", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("i.indrelid = to_regclass('blocks')", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_get_expr", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_namespace", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a.key_expressions = e.key_expressions", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a.predicate = e.predicate", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ILIKE", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("public.blocks", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateBlockAsync_PersistsTransactionConfirmationData()
    {
        var mapper = AutoMapperFactory.CreateMapper();
        var repository = new BlockRepository(mapper);
        var connection = new RecordingDbConnection();
        var block = new Block
        {
            Id = 42,
            PoolId = "doge-test",
            BlockHeight = 100,
            Status = BlockStatus.Pending,
            TransactionConfirmationData = "resolved-coinbase-txid",
        };

        var updated = await repository.UpdateBlockAsync(connection, null, block);

        Assert.True(updated);
        Assert.Contains("transactionconfirmationdata = @transactionconfirmationdata",
            connection.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(block.TransactionConfirmationData,
            connection.Parameters["transactionconfirmationdata"]);
    }

    [Fact]
    public async Task UpdateBlockAsync_ReturnsFalseWhenPromotionGuardDoesNotUpdateRow()
    {
        var mapper = AutoMapperFactory.CreateMapper();
        var repository = new BlockRepository(mapper);
        var connection = new RecordingDbConnection { NonQueryResult = 0 };
        var block = new Block
        {
            Id = 42,
            PoolId = "doge-test",
            BlockHeight = 100,
            Type = "auxpow",
            Hash = "doge-block",
            Status = BlockStatus.Pending,
            TransactionConfirmationData = "resolved-coinbase-txid",
        };

        var updated = await repository.UpdateBlockAsync(connection, null, block);

        Assert.False(updated);
        Assert.Contains("NOT EXISTS", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ON CONFLICT", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBlockBeforeCountAsync_UsesCountScalar()
    {
        var mapper = AutoMapperFactory.CreateMapper();
        var repository = new BlockRepository(mapper);
        var connection = new RecordingDbConnection { ScalarResult = 0 };

        await repository.GetBlockBeforeCountAsync(connection, "doge-test",
            new[] { BlockStatus.Pending }, DateTime.UtcNow);

        Assert.Contains("SELECT COUNT(*)", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT *", connection.CommandText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuxPowMigration_IsAtomicAndRecreatesEveryRequiredIndex()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../Miningcore/Persistence/Postgres/Scripts/add_auxpow_block_idempotency.sql"));
        var script = File.ReadAllText(path);
        var indexNames = new[]
        {
            "IDX_BLOCKS_AUXPOW_POOL_HASH",
            "IDX_BLOCKS_AUXPOW_CLAIM",
            "IDX_BLOCKS_MERGED_PARENT_POOL_HASH",
            "IDX_BLOCKS_BITCOIN_DIRECT_POOL_HASH",
        };

        Assert.Contains("BEGIN;", script, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("COMMIT;", script.Trim(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE UNIQUE INDEX IF NOT EXISTS", script,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("to_regclass('blocks')", script,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("format('DROP INDEX IF EXISTS %I.%I'", script,
            StringComparison.OrdinalIgnoreCase);

        foreach(var indexName in indexNames)
        {
            Assert.Contains($"'{indexName.ToLowerInvariant()}'", script,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"CREATE UNIQUE INDEX {indexName}", script,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class RecordingDbConnection : DbConnection
    {
        public string CommandText { get; private set; }
        public IReadOnlyDictionary<string, object> Parameters { get; private set; }
        public object ScalarResult { get; set; }
        public int NonQueryResult { get; set; } = 1;

        public override string ConnectionString { get; set; }
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            throw new NotSupportedException();
        }

        protected override DbCommand CreateDbCommand()
        {
            return new RecordingDbCommand(this);
        }

        public void Record(RecordingDbCommand command)
        {
            CommandText = command.CommandText;
            Parameters = command.ParametersList
                .Cast<DbParameter>()
                .ToDictionary(x => x.ParameterName.TrimStart('@'), x => x.Value,
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class RecordingDbCommand : DbCommand
    {
        private readonly RecordingDbConnection connection;
        private readonly RecordingParameterCollection parameters = new();

        public RecordingDbCommand(RecordingDbConnection connection)
        {
            this.connection = connection;
        }

        public RecordingParameterCollection ParametersList => parameters;

        public override string CommandText { get; set; }
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection DbConnection { get => connection; set { } }
        protected override DbParameterCollection DbParameterCollection => parameters;
        protected override DbTransaction DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery()
        {
            connection.Record(this);
            return connection.NonQueryResult;
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ExecuteNonQuery());
        }

        public override object ExecuteScalar()
        {
            connection.Record(this);
            return connection.ScalarResult;
        }

        public override Task<object> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ExecuteScalar());
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter()
        {
            return new RecordingDbParameter();
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        public override string ParameterName { get; set; }
        public override int Size { get; set; }
        public override string SourceColumn { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override object Value { get; set; }

        public override void ResetDbType()
        {
        }
    }

    private sealed class RecordingParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> parameters = new();

        public override int Count => parameters.Count;
        public override object SyncRoot => ((ICollection) parameters).SyncRoot;

        public override int Add(object value)
        {
            parameters.Add((DbParameter) value);
            return parameters.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach(var value in values)
                Add(value);
        }

        public override void Clear()
        {
            parameters.Clear();
        }

        public override bool Contains(object value)
        {
            return parameters.Contains((DbParameter) value);
        }

        public override bool Contains(string value)
        {
            return IndexOf(value) >= 0;
        }

        public override void CopyTo(Array array, int index)
        {
            ((ICollection) parameters).CopyTo(array, index);
        }

        public override IEnumerator GetEnumerator()
        {
            return parameters.GetEnumerator();
        }

        public override int IndexOf(object value)
        {
            return parameters.IndexOf((DbParameter) value);
        }

        public override int IndexOf(string parameterName)
        {
            return parameters.FindIndex(x => string.Equals(x.ParameterName, parameterName,
                StringComparison.OrdinalIgnoreCase));
        }

        public override void Insert(int index, object value)
        {
            parameters.Insert(index, (DbParameter) value);
        }

        public override void Remove(object value)
        {
            parameters.Remove((DbParameter) value);
        }

        public override void RemoveAt(int index)
        {
            parameters.RemoveAt(index);
        }

        public override void RemoveAt(string parameterName)
        {
            var index = IndexOf(parameterName);
            if(index >= 0)
                RemoveAt(index);
        }

        protected override DbParameter GetParameter(int index)
        {
            return parameters[index];
        }

        protected override DbParameter GetParameter(string parameterName)
        {
            return parameters[IndexOf(parameterName)];
        }

        protected override void SetParameter(int index, DbParameter value)
        {
            parameters[index] = value;
        }

        protected override void SetParameter(string parameterName, DbParameter value)
        {
            var index = IndexOf(parameterName);
            if(index >= 0)
                parameters[index] = value;
            else
                parameters.Add(value);
        }
    }
}
