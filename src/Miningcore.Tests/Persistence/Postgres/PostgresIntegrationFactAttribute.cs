using System;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

/// <summary>
/// Makes the optional PostgreSQL suite visibly skipped instead of reporting a passing test that
/// executed no assertions. CI supplies the connection string and therefore executes these tests.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    public PostgresIntegrationFactAttribute()
    {
        if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
               "MININGCORE_TEST_POSTGRES")))
            Skip = "Set MININGCORE_TEST_POSTGRES to run the PostgreSQL integration test";
    }
}
