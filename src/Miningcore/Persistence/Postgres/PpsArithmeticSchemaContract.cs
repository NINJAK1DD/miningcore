using System.Text.RegularExpressions;

namespace Miningcore.Persistence.Postgres;

// Compare installed routines with the shipped migration, not a second handwritten
// implementation. Whitespace inside SQL literals remains significant.
internal static class PpsArithmeticSchemaContract
{
    private static readonly string Migration = ReadMigration();
    internal static readonly string CreditGuard = Body("guard_pps_arithmetic_credit");
    internal static readonly string TransitionGuard = Body("guard_pps_arithmetic_transition");
    internal static readonly string Activation = Body("activate_pps_binary64");

    private static string ReadMigration()
    {
        using var stream = typeof(PpsArithmeticSchemaContract).Assembly.GetManifestResourceStream(
            "Miningcore.Persistence.Postgres.Scripts.add_pps_arithmetic_version.sql");
        using var reader = new StreamReader(stream ?? throw new InvalidOperationException(
            "Missing PPS arithmetic schema contract"));
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private static string Body(string name)
    {
        var match = Regex.Match(Migration,
            @"CREATE OR REPLACE FUNCTION " + name + @"\([^$]*?AS \$\$(.*?)\$\$;",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() :
            throw new InvalidOperationException("Missing PPS arithmetic routine: " + name);
    }
}
