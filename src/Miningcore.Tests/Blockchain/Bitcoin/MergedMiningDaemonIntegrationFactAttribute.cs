using System;
using System.IO;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class MergedMiningDaemonIntegrationFactAttribute : FactAttribute
{
    public const string LitecoinBinaryEnvironmentVariable =
        "MININGCORE_TEST_LITECOIND";
    public const string DogecoinBinaryEnvironmentVariable =
        "MININGCORE_TEST_DOGECOIND";

    public MergedMiningDaemonIntegrationFactAttribute()
    {
        var litecoind = Environment.GetEnvironmentVariable(
            LitecoinBinaryEnvironmentVariable);
        var dogecoind = Environment.GetEnvironmentVariable(
            DogecoinBinaryEnvironmentVariable);
        var postgres = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");

        if(string.IsNullOrWhiteSpace(litecoind) || !File.Exists(litecoind) ||
           string.IsNullOrWhiteSpace(dogecoind) || !File.Exists(dogecoind) ||
           string.IsNullOrWhiteSpace(postgres))
            Skip = "Set pinned litecoind, dogecoind and PostgreSQL integration " +
                "environment variables to run merged-mining payout regtest";
    }
}
