using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Miningcore.Api.Extensions;

internal static class PaymentProcessingExtraSensitivityPolicy
{
    internal const string RedactedDiagnosticKey =
        "<redacted-sensitive-key>";
    internal const string EmptyDiagnosticKey = "<empty-key>";
    internal const int MaximumDiagnosticLabelCharacters = 80;

    // A sensitive-looking public setting must be reviewed and recorded by
    // fully-qualified member name. The set is intentionally empty today.
    // Keeping it in production makes diagnostics and architecture tests share
    // one authoritative sensitivity policy.
    internal static readonly FrozenSet<string>
        KnownBenignSensitivePaymentProperties = Array.Empty<string>()
            .ToFrozenSet(StringComparer.Ordinal);

    internal static bool IsSensitivePropertyName(string name) =>
        name?.Contains("Password", StringComparison.OrdinalIgnoreCase) == true ||
        name?.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase) == true ||
        name?.Contains("Passphrase", StringComparison.OrdinalIgnoreCase) == true ||
        name?.Contains("Mnemonic", StringComparison.OrdinalIgnoreCase) == true ||
        name?.Contains("Seed", StringComparison.OrdinalIgnoreCase) == true ||
        name?.Contains("Credential", StringComparison.OrdinalIgnoreCase) == true ||
        name?.Contains("Secret", StringComparison.OrdinalIgnoreCase) == true ||
        name?.Contains("Token", StringComparison.OrdinalIgnoreCase) == true ||
        name?.EndsWith("Key", StringComparison.OrdinalIgnoreCase) == true;

    internal static string CreateDiagnosticKey(string name,
        out bool redacted)
    {
        if(IsSensitivePropertyName(name))
        {
            redacted = true;
            return RedactedDiagnosticKey;
        }

        redacted = false;
        return CreateDiagnosticLabel(name, EmptyDiagnosticKey);
    }

    internal static string CreateDiagnosticLabel(string value,
        string emptyMarker)
    {
        if(string.IsNullOrEmpty(value))
            return emptyMarker;

        var result = new StringBuilder();

        for(var index = 0; index < value.Length; index++)
        {
            var representation = Escape(value[index]);
            var hasMoreCharacters = index + 1 < value.Length;
            var requiredLength = representation.Length +
                (hasMoreCharacters ? 1 : 0);

            if(result.Length + requiredLength >
               MaximumDiagnosticLabelCharacters)
            {
                if(result.Length < MaximumDiagnosticLabelCharacters)
                    result.Append('…');

                break;
            }

            result.Append(representation);
        }

        return result.ToString();
    }

    private static string Escape(char character)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(character);
        return char.IsControl(character) ||
            category is UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator or
                UnicodeCategory.Surrogate ||
            character is '\\' or '\'' or '"' or '<' or '>'
                ? $"\\u{(int) character:X4}"
                : character.ToString();
    }
}
