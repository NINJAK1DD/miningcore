using System;
using System.IO;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class NexaDaemonIntegrationFactAttribute : FactAttribute
{
    public const string BinaryEnvironmentVariable = "MININGCORE_TEST_NEXAD";
    public NexaDaemonIntegrationFactAttribute()
    {
        var binary = Environment.GetEnvironmentVariable(BinaryEnvironmentVariable);
        if(string.IsNullOrWhiteSpace(binary) || !File.Exists(binary))
            Skip = "Set MININGCORE_TEST_NEXAD to the checksum-verified Nexa 2.2.0.0 daemon";
    }
}
