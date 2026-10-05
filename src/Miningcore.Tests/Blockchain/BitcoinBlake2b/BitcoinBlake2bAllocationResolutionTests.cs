using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Tests.Util.Postgres;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

internal sealed class BitcoinBlake2bResolutionFactAttribute : FactAttribute
{
    public BitcoinBlake2bResolutionFactAttribute()
    {
        if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES")) ||
           string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN")))
            Skip = "Set MININGCORE_TEST_POSTGRES and MININGCORE_TEST_POSTGRES_BIN for audited resolution tests";
    }
}

public class BitcoinBlake2bAllocationResolutionTests
{
    [BitcoinBlake2bResolutionFact]
    public async Task AuditedResolution_RealPsqlDryRunApplyReplayAndInvalidCasesPreserveFinancialState()
    {
        await using var db = await BitcoinBlake2bLedgerProbe.CreateAsync(fullSchema: true);
        const string pool = "audit'quoted-pool";
        var caseId = Guid.NewGuid().ToString();
        var memo = $"Audited Bitcoin BLAKE2b recovery block 1 case {caseId}";
        await db.Observer.ExecuteAsync(@"
            INSERT INTO blocks(id,poolid,blockheight,networkdifficulty,status,type,transactionconfirmationdata,
                               hash,miner,reward,confirmationprogress,created)
            VALUES(1,@pool,100,1,'quarantined',NULL,@txid,@hash,'miner',50,1,now());
            INSERT INTO balances(poolid,address,amount,created,updated) VALUES(@pool,'miner',7,now(),now());
            INSERT INTO balance_changes(id,poolid,address,amount,usage,tags,created)
            VALUES(1,@pool,'miner',2,@memo,ARRAY['existing-tag'],now()),
                  (2,@pool,'miner',0,@memo,NULL,now());",
            new { pool, memo, txid = new string('b', 64), hash = new string('a', 64) });
        var schema = await db.Observer.ExecuteScalarAsync<string>("SELECT current_schema()");
        var inputs = new Dictionary<string, string>
        {
            ["pool_id"] = pool, ["block_id"] = "1", ["block_height"] = "100",
            ["block_hash"] = new string('a', 64), ["coinbase_txid"] = new string('b', 64),
            ["case_id"] = caseId, ["credit_change_ids"] = "[1,2]", ["approved_additional_credit"] = "2",
        };
        async Task AssertFinancialState()
        {
            Assert.Equal("quarantined", await db.Observer.ExecuteScalarAsync<string>("SELECT status FROM blocks WHERE id=1"));
            Assert.Equal(50m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT reward FROM blocks WHERE id=1"));
            Assert.Equal(7m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
            Assert.Equal(2m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balance_changes"));
            Assert.Equal(0, await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM payments"));
            Assert.Equal(0, await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM payment_batches"));
        }
        async Task AssertUnresolved()
        {
            await AssertFinancialState();
            Assert.Equal(new[] { "existing-tag" }, await db.Observer.ExecuteScalarAsync<string[]>("SELECT tags FROM balance_changes WHERE id=1"));
            Assert.Null(await db.Observer.ExecuteScalarAsync<string[]>("SELECT tags FROM balance_changes WHERE id=2"));
        }
        Assert.Equal(0, (await RunPsql(schema, inputs, null)).ExitCode); // Default is a real ROLLBACK.
        await AssertUnresolved();
        foreach(var (key, value) in new[]
        {
            ("pool_id", "another-pool"), ("block_id", "2"), ("block_height", "101"),
            ("block_hash", new string('c', 64)), ("coinbase_txid", new string('c', 64)),
            ("case_id", Guid.NewGuid().ToString()), ("credit_change_ids", "[]"),
            ("credit_change_ids", "[1,1]"), ("credit_change_ids", "[1,3]"),
            ("credit_change_ids", "[1]"), ("credit_change_ids", "[\"1\",2]"),
            ("credit_change_ids", "[1.5,2]"), ("credit_change_ids", "null"),
            ("credit_change_ids", "[9223372036854775808,2]"),
            ("approved_additional_credit", "1"), ("approved_additional_credit", "-1"),
            ("approved_additional_credit", "NaN"), ("approved_additional_credit", "Infinity"),
            ("approved_additional_credit", "0.0000000000001"),
        })
        {
            var invalid = new Dictionary<string, string>(inputs) { [key] = value };
            Assert.NotEqual(0, (await RunPsql(schema, invalid, true)).ExitCode);
            await AssertUnresolved();
        }
        foreach(var status in new[] { "pending", "confirmed", "orphaned" })
        {
            await db.Observer.ExecuteAsync("UPDATE blocks SET status=@status WHERE id=1", new { status });
            Assert.NotEqual(0, (await RunPsql(schema, inputs, true)).ExitCode);
        }
        await db.Observer.ExecuteAsync("UPDATE blocks SET status='quarantined',type='auxpow' WHERE id=1");
        Assert.NotEqual(0, (await RunPsql(schema, inputs, true)).ExitCode);
        await db.Observer.ExecuteAsync("UPDATE blocks SET type=NULL WHERE id=1");
        await db.Observer.ExecuteAsync("UPDATE balance_changes SET poolid='other' WHERE id=2");
        Assert.NotEqual(0, (await RunPsql(schema, inputs, true)).ExitCode);
        await db.Observer.ExecuteAsync("UPDATE balance_changes SET poolid=@pool WHERE id=2", new { pool });
        await AssertUnresolved();
        var applied = await RunPsql(schema, inputs, true);
        Assert.True(applied.ExitCode == 0, applied.Output);
        var tag = $"bitcoin-blake2b:allocation-resolved:block=1:case={caseId}";
        Assert.Equal(new[] { "existing-tag", tag }, await db.Observer.ExecuteScalarAsync<string[]>("SELECT tags FROM balance_changes WHERE id=1"));
        Assert.Equal(new[] { tag }, await db.Observer.ExecuteScalarAsync<string[]>("SELECT tags FROM balance_changes WHERE id=2"));
        Assert.Equal(0, (await RunPsql(schema, inputs, true)).ExitCode); // Idempotent, including zero receipt.
        await AssertFinancialState();
        Assert.Equal(2, await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM balance_changes WHERE cardinality(array_positions(tags,@tag))=1", new { tag }));
        var otherCase = Guid.NewGuid().ToString();
        await db.Observer.ExecuteAsync(@"INSERT INTO balance_changes(id,poolid,address,amount,usage,created)
            VALUES(3,@pool,'miner',0,@memo,now())", new { pool, memo = $"Audited Bitcoin BLAKE2b recovery block 1 case {otherCase}" });
        var conflicting = new Dictionary<string, string>(inputs)
        { ["case_id"] = otherCase, ["credit_change_ids"] = "[3]", ["approved_additional_credit"] = "0" };
        Assert.NotEqual(0, (await RunPsql(schema, conflicting, true)).ExitCode);
        await AssertFinancialState();
        // A reviewed zero-entitlement case still gets an explicit durable closure.
        await db.Observer.ExecuteAsync(@"INSERT INTO blocks(id,poolid,blockheight,networkdifficulty,status,
            transactionconfirmationdata,hash,miner,reward,created)
            VALUES(2,@pool,101,1,'quarantined',@txid,@hash,'miner',0,now());
            INSERT INTO balance_changes(id,poolid,address,amount,usage,created)
            VALUES(4,@pool,'miner',0,@memo,now())", new
            { pool, txid = new string('d', 64), hash = new string('c', 64), memo = $"Audited Bitcoin BLAKE2b recovery block 2 case {otherCase}" });
        var zero = new Dictionary<string, string>(inputs)
        {
            ["block_id"] = "2", ["block_height"] = "101", ["block_hash"] = new string('c', 64),
            ["coinbase_txid"] = new string('d', 64), ["case_id"] = otherCase,
            ["credit_change_ids"] = "[4]", ["approved_additional_credit"] = "0",
        };
        var zeroApplied = await RunPsql(schema, zero, true);
        Assert.True(zeroApplied.ExitCode == 0, zeroApplied.Output);
        Assert.Equal(new[] { $"bitcoin-blake2b:allocation-resolved:block=2:case={otherCase}" },
            await db.Observer.ExecuteScalarAsync<string[]>("SELECT tags FROM balance_changes WHERE id=4"));
        await AssertFinancialState();
        // Legacy databases can contain malformed direct metadata without the
        // newer guard. Model that only inside this test's disposable schema.
        await db.Observer.ExecuteAsync(@"ALTER TABLE blocks DROP CONSTRAINT CHK_BLOCKS_BITCOIN_DIRECT_SETTLEMENT;
            UPDATE blocks SET settlementmode='coinbase-direct' WHERE id=1");
        Assert.NotEqual(0, (await RunPsql(schema, inputs, true)).ExitCode);
        await AssertFinancialState();
    }

    private static async Task<(int ExitCode, string Output)> RunPsql(string schema, Dictionary<string, string> inputs, bool? apply)
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));
        var binary = Path.Combine(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN"), OperatingSystem.IsWindows() ? "psql.exe" : "psql");
        var start = new ProcessStartInfo(binary)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["PGPASSWORD"] = settings.Password;
        start.Environment["PGOPTIONS"] = "-c search_path=" + schema + ",public -c statement_timeout=15000 -c lock_timeout=5000";
        foreach(var arg in new[] { "-X", "--no-password", "-h", settings.Host, "-p", settings.Port.ToString(), "-U", settings.Username, "-d", settings.Database })
            start.ArgumentList.Add(arg);
        foreach(var pair in inputs)
        { start.ArgumentList.Add("-v"); start.ArgumentList.Add(pair.Key + "=" + pair.Value); }
        if(apply.HasValue)
        { start.ArgumentList.Add("-v"); start.ArgumentList.Add("apply=" + (apply.Value ? "true" : "false")); }
        start.ArgumentList.Add("-f"); start.ArgumentList.Add(PostgresTestScripts.PathFor("resolve_blake2b_allocation_hold.sql"));
        using var process = Process.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }
}
