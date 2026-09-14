using System;
using Xunit;

namespace Miningcore.Tests.Util;

internal sealed class PostgresLiveTheoryAttribute : TheoryAttribute
{
    internal const string SkipReason = "Set MININGCORE_TEST_POSTGRES_BIN to run isolated live PostgreSQL tests";

    public PostgresLiveTheoryAttribute()
    {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN")))
            Skip = SkipReason;
    }
}

internal sealed class PostgresLiveFactAttribute : FactAttribute
{
    public PostgresLiveFactAttribute()
    {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN")))
            Skip = PostgresLiveTheoryAttribute.SkipReason;
    }
}
