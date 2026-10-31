using System.Text;

namespace Miningcore.Pushover;

// Pushover counts UTF-8 characters, not UTF-16 code units. Keep both the
// incident summary and the trailing operator instruction when detail is long.
internal static class PushoverMessagePolicy
{
    internal const int MaxMessageCharacters = 1024;
    internal const int MaxTitleCharacters = 250;
    private const string Omission =
        "\n… [details omitted; see Miningcore logs]\n";

    internal static string Title(string title)
    {
        var runes = title.EnumerateRunes().ToArray();
        if(runes.Length <= MaxTitleCharacters)
            return title;

        return string.Concat(runes.Take(MaxTitleCharacters - 1)
            .Select(rune => rune.ToString())) + "…";
    }

    internal static string Message(string message)
    {
        var runes = message.EnumerateRunes().ToArray();
        if(runes.Length <= MaxMessageCharacters)
            return message;

        var available = MaxMessageCharacters -
            Omission.EnumerateRunes().Count();
        var prefix = available * 2 / 3;
        var suffix = available - prefix;
        return string.Concat(runes.Take(prefix)
                .Select(rune => rune.ToString())) + Omission +
            string.Concat(runes.Skip(runes.Length - suffix)
                .Select(rune => rune.ToString()));
    }
}
