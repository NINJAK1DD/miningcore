using System;
using Xunit;

namespace Miningcore.Tests.Util;

/// <summary>
/// Opts into theory cases that create an isolated server from PostgreSQL binaries,
/// rather than using the MININGCORE_TEST_POSTGRES service connection string.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class IsolatedPostgresTheoryAttribute : TheoryAttribute
{
    internal const string SkipReason = "Set MININGCORE_TEST_POSTGRES_BIN to the PostgreSQL bin directory to run isolated live PostgreSQL tests";

    public IsolatedPostgresTheoryAttribute()
    {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN")))
            Skip = SkipReason;
    }
}

/// <summary>
/// Opts into a fact that creates an isolated server from PostgreSQL binaries,
/// rather than using the MININGCORE_TEST_POSTGRES service connection string.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class IsolatedPostgresFactAttribute : FactAttribute
{
    public IsolatedPostgresFactAttribute()
    {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN")))
            Skip = IsolatedPostgresTheoryAttribute.SkipReason;
    }
}
