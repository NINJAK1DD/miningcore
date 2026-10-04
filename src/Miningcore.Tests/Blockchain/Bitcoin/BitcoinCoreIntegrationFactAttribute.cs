using System;
using System.IO;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

/// <summary>
/// Makes the optional Bitcoin Core suite visibly skipped unless a real bitcoind binary is supplied.
/// CI installs a pinned official binary and therefore executes these tests.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class BitcoinCoreIntegrationFactAttribute : FactAttribute
{
    public const string BinaryEnvironmentVariable = "MININGCORE_TEST_BITCOIND";

    public BitcoinCoreIntegrationFactAttribute(bool requiresPostgres = false)
    {
        if(requiresPostgres && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES")))
            Skip = "Set MININGCORE_TEST_POSTGRES to run the Bitcoin/PPS database integration test";

        var binary = Environment.GetEnvironmentVariable(BinaryEnvironmentVariable);

        if(string.IsNullOrWhiteSpace(binary) || !File.Exists(binary))
            Skip = $"Set {BinaryEnvironmentVariable} to a bitcoind binary to run the Bitcoin Core integration test";
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public class BitcoinCorePayoutIntegrationCollection
{
    public const string Name = "Bitcoin Core payout integration";
}
