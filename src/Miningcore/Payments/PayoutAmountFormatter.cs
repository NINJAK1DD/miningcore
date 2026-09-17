using System.Globalization;

namespace Miningcore.Payments;

internal static class PayoutAmountFormatter
{
    private const string ExactFormat = "0.############################";

    public static string FormatExact(decimal amount) =>
        amount.ToString(ExactFormat, CultureInfo.InvariantCulture);
}
