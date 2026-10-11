using System.Globalization;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

// Kept separate from the parallel budget fixture: this one scenario starts
// multiple real sessions, each subject to the production startup deadline.
[Collection(IntegrationDeadlineCollection.Name)]
public class BitcoinBlake2bCultureTests
{
    private readonly ITestOutputHelper output;
    public BitcoinBlake2bCultureTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task MalformedNumericStrings_AfterCultureScenarios_KeepAdmissionContract()
    {
        // Explicit order in one execution context: runner ordering between
        // separate theory rows cannot establish that cultures were restored.
        var scenarios = new BitcoinBlake2bDifficultyBudgetTests(output);
        var original = CultureInfo.CurrentCulture;
        foreach(var culture in new[] { "fr-FR", "tr-TR" })
        {
            await scenarios.DateScalars_PreserveAuthorizationAndSubscribeCompatibilityAcrossCultures(culture);
            Assert.Equal(original, CultureInfo.CurrentCulture);
        }
        await scenarios.SuggestAndConfigure_UseInvariantNumbersAndStringsAcrossCultures("de-DE");
        Assert.Equal(original, CultureInfo.CurrentCulture);
        await scenarios.CanonicalSuggestion_RetainsItsLegacyCultureConversion();
        Assert.Equal(original, CultureInfo.CurrentCulture);
        foreach(var value in new[] { "NaN", "Infinity", "1e999", "-1", "2,0" })
            await scenarios.MalformedNumericStrings_ConsumeBudgetThenDisconnect(value);

        // Even an explicitly inherited comma-decimal culture must not change
        // malformed-wire admission, its exact budget or terminal disconnect.
        try
        {
            foreach(var culture in new[] { "fr-FR", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                await scenarios.MalformedNumericStrings_ConsumeBudgetThenDisconnect("2,0");
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
