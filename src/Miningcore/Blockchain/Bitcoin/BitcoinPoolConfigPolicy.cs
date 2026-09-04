using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Configuration;
using Miningcore.Mining;
using Newtonsoft.Json;

namespace Miningcore.Blockchain.Bitcoin;

internal enum BitcoinDirectSoloPayoutMode
{
    Disabled,
    Implicit,
    Explicit,
}

internal static class BitcoinPoolConfigPolicy
{
    private const bool Bip54CoinbaseDefault = true;

    internal static bool ResolveBip54Coinbase(PoolConfig pool,
        BitcoinPoolConfigExtra config, Exception bindingError = null)
    {
        if(config?.Bip54Coinbase is bool configured)
            return configured;

        if(HasRawProperty(pool, "bip54Coinbase"))
        {
            throw BindingError(pool, "bip54Coinbase", bindingError);
        }

        return Bip54CoinbaseDefault;
    }

    internal static bool ResolveSoloCoinbasePayout(PoolConfig pool,
        BitcoinPoolConfigExtra config, Exception bindingError = null) =>
        ResolveSoloCoinbasePayoutMode(pool, config, bindingError) !=
        BitcoinDirectSoloPayoutMode.Disabled;

    internal static BitcoinDirectSoloPayoutMode
        ResolveSoloCoinbasePayoutMode(PoolConfig pool,
            BitcoinPoolConfigExtra config, Exception bindingError = null)
    {
        if(config?.SoloCoinbasePayout is bool configured)
        {
            return configured ? BitcoinDirectSoloPayoutMode.Explicit :
                BitcoinDirectSoloPayoutMode.Disabled;
        }

        // The raw loader owns canonical casing and Boolean-token validation.
        // Protected settlement/coinbase choices fail closed if the surrounding
        // extension object cannot bind. Other Bitcoin extension settings retain
        // the historical best-effort behavior when neither protected key exists.
        if(HasRawProperty(pool, "soloCoinbasePayout"))
            throw BindingError(pool, "soloCoinbasePayout", bindingError);

        // Runtime template identity is not assigned during the first config
        // pass. ValidateBitcoinDirectSoloDeployment owns the stricter family,
        // symbol and canonical-name check before listeners are reserved.
        return string.Equals(pool?.Coin, "bitcoin",
                   StringComparison.Ordinal) &&
               pool.PaymentProcessing?.PayoutScheme == PayoutScheme.SOLO
            ? BitcoinDirectSoloPayoutMode.Implicit
            : BitcoinDirectSoloPayoutMode.Disabled;
    }

    private static PoolStartupException BindingError(PoolConfig pool,
        string property, Exception error) => new(
        $"Pool '{pool.Id}' could not safely bind Bitcoin pool extension " +
        $"data while {property} is present; correct malformed or mistyped " +
        "extension fields before startup." + DescribeBindingError(error),
        pool.Id, error);

    private static bool HasRawProperty(PoolConfig pool, string name) =>
        pool?.Extra?.Keys.Any(key => string.Equals(key, name,
            StringComparison.OrdinalIgnoreCase)) == true;

    private static string DescribeBindingError(Exception error)
    {
        for(var current = error; current != null;
            current = current.InnerException)
        {
            var path = current switch
            {
                JsonSerializationException jsonError => jsonError.Path,
                JsonReaderException jsonError => jsonError.Path,
                _ => null,
            };
            if(!string.IsNullOrWhiteSpace(path))
                return $" Invalid extension path: '{path}'.";
        }

        return error == null
            ? string.Empty
            : $" Binding error type: {error.GetType().Name}.";
    }
}
