using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Miningcore.Api.Extensions;

internal static class PaymentProcessingExtraSensitivityPolicy
{
    internal const string RedactedDiagnosticKey =
        "<redacted-sensitive-key>";
    internal const string EmptyDiagnosticKey = "<empty-key>";
    internal const int MaximumDiagnosticKeyCharacters = 80;

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

    internal static bool IsSensitivePaymentProperty(Type declaringType,
        string name)
    {
        if(!IsSensitivePropertyName(name))
            return false;

        var identity = $"{declaringType.FullName}.{name}";
        return !KnownBenignSensitivePaymentProperties.Contains(identity);
    }

    internal static string CreateDiagnosticKey(string name,
        out bool redacted)
    {
        if(IsSensitivePropertyName(name))
        {
            redacted = true;
            return RedactedDiagnosticKey;
        }

        redacted = false;
        if(string.IsNullOrEmpty(name))
            return EmptyDiagnosticKey;

        var result = new StringBuilder();
        var characters = 0;

        foreach(var character in name)
        {
            if(characters == MaximumDiagnosticKeyCharacters)
            {
                result.Append('…');
                break;
            }

            characters++;
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if(char.IsControl(character) ||
               category is UnicodeCategory.Format or
                   UnicodeCategory.LineSeparator or
                   UnicodeCategory.ParagraphSeparator or
                   UnicodeCategory.Surrogate ||
               character is '\\' or '\'')
                result.Append($"\\u{(int) character:X4}");
            else
                result.Append(character);
        }

        return result.ToString();
    }
}
