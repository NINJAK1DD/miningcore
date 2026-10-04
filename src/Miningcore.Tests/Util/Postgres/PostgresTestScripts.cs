using System;
using System.IO;

namespace Miningcore.Tests.Util.Postgres;

internal static class PostgresTestScripts
{
    public static string PathFor(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Postgres", name);
}
