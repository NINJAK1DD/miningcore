using Miningcore.Api.Responses;
using Miningcore.Blockchain.Alephium.Configuration;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Conceal.Configuration;
using Miningcore.Blockchain.Cryptonote.Configuration;
using Miningcore.Blockchain.Ergo.Configuration;
using Miningcore.Blockchain.Ethereum.Configuration;
using Miningcore.Blockchain.Handshake.Configuration;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Blockchain.Warthog.Configuration;
using Miningcore.Blockchain.Xelis.Configuration;
using Miningcore.Blockchain.Zano.Configuration;
using Miningcore.Configuration;
using Newtonsoft.Json.Linq;

namespace Miningcore.Api.Extensions;

internal static class PaymentProcessingExtraProjection
{
    public static ApiPoolPaymentProcessingExtra Create(CoinFamily family,
        IDictionary<string, object> source)
    {
        if(source == null)
            return null;

        var result = new ApiPoolPaymentProcessingExtra();

        switch(family)
        {
            case CoinFamily.Alephium:
                Project<string>(source,
                    nameof(AlephiumPaymentProcessingConfigExtra.WalletName),
                    result.SetWalletName);
                Project<long?>(source,
                    nameof(AlephiumPaymentProcessingConfigExtra.
                        BlockRewardsLockTime),
                    result.SetBlockRewardsLockTime);
                Project<bool?>(source,
                    nameof(AlephiumPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    result.SetKeepTransactionFees);
                break;

            case CoinFamily.Bitcoin:
            case CoinFamily.Equihash:
            case CoinFamily.Nexa:
            case CoinFamily.Progpow:
            case CoinFamily.Satoshicash:
                Project<bool?>(source,
                    nameof(BitcoinPoolPaymentProcessingConfigExtra.
                        MinersPayTxFees),
                    result.SetMinersPayTxFees);
                break;

            case CoinFamily.Conceal:
                Project<decimal?>(source,
                    nameof(ConcealPoolPaymentProcessingConfigExtra.
                        MinimumPaymentToPaymentId),
                    result.SetMinimumPaymentToPaymentId);
                break;

            case CoinFamily.Cryptonote:
                Project<decimal?>(source,
                    nameof(CryptonotePoolPaymentProcessingConfigExtra.
                        MinimumPaymentToPaymentId),
                    result.SetMinimumPaymentToPaymentId);
                Project<int?>(source,
                    nameof(CryptonotePoolPaymentProcessingConfigExtra.
                        MaximumDestinationPerTransfer),
                    result.SetMaximumDestinationPerTransfer);
                break;

            case CoinFamily.Ergo:
                Project<int?>(source,
                    nameof(ErgoPaymentProcessingConfigExtra.
                        MinimumConfirmations),
                    result.SetMinimumConfirmations);
                break;

            case CoinFamily.Ethereum:
                Project<bool?>(source,
                    nameof(EthereumPoolPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    result.SetKeepTransactionFees);
                Project<bool?>(source,
                    nameof(EthereumPoolPaymentProcessingConfigExtra.
                        KeepUncles),
                    result.SetKeepUncles);
                Project<ulong?>(source,
                    nameof(EthereumPoolPaymentProcessingConfigExtra.Gas),
                    result.SetGas);
                Project<ulong?>(source,
                    nameof(EthereumPoolPaymentProcessingConfigExtra.
                        MaxFeePerGas),
                    result.SetMaxFeePerGas);
                Project<uint?>(source,
                    nameof(EthereumPoolPaymentProcessingConfigExtra.
                        BlockSearchOffset),
                    result.SetBlockSearchOffset);
                break;

            case CoinFamily.Handshake:
                Project<string>(source,
                    nameof(HandshakePoolPaymentProcessingConfigExtra.
                        WalletName),
                    result.SetWalletName);
                Project<string>(source,
                    nameof(HandshakePoolPaymentProcessingConfigExtra.
                        WalletAccount),
                    result.SetWalletAccount);
                Project<bool?>(source,
                    nameof(HandshakePoolPaymentProcessingConfigExtra.
                        MinersPayTxFees),
                    result.SetMinersPayTxFees);
                break;

            case CoinFamily.Kaspa:
                Project<int?>(source,
                    nameof(KaspaPaymentProcessingConfigExtra.
                        MinimumConfirmations),
                    result.SetMinimumConfirmations);
                Project<string>(source,
                    nameof(KaspaPaymentProcessingConfigExtra.
                        VersionEnablingMaxFee),
                    result.SetVersionEnablingMaxFee);
                Project<ulong?>(source,
                    nameof(KaspaPaymentProcessingConfigExtra.MaxFee),
                    result.SetMaxFee);
                break;

            case CoinFamily.Warthog:
                Project<decimal?>(source,
                    nameof(WarthogPaymentProcessingConfigExtra.
                        MaximumTransactionFees),
                    result.SetMaximumTransactionFees);
                Project<bool?>(source,
                    nameof(WarthogPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    result.SetKeepTransactionFees);
                Project<int?>(source,
                    nameof(WarthogPaymentProcessingConfigExtra.
                        MinimumConfirmations),
                    result.SetMinimumConfirmations);
                Project<int?>(source,
                    nameof(WarthogPaymentProcessingConfigExtra.
                        MaxDegreeOfParallelPayouts),
                    result.SetMaxDegreeOfParallelPayouts);
                break;

            case CoinFamily.Xelis:
                Project<int?>(source,
                    nameof(XelisPaymentProcessingConfigExtra.
                        MinimumConfirmations),
                    result.SetMinimumConfirmations);
                Project<int?>(source,
                    nameof(XelisPaymentProcessingConfigExtra.
                        MaximumDestinationPerTransfer),
                    result.SetMaximumDestinationPerTransfer);
                Project<bool?>(source,
                    nameof(XelisPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    result.SetKeepTransactionFees);
                break;

            case CoinFamily.Zano:
                Project<decimal?>(source,
                    nameof(ZanoPoolPaymentProcessingConfigExtra.
                        MinimumPaymentToPaymentId),
                    result.SetMinimumPaymentToPaymentId);
                Project<bool?>(source,
                    nameof(ZanoPoolPaymentProcessingConfigExtra.
                        RevealPoolAddress),
                    result.SetRevealPoolAddress);
                Project<bool?>(source,
                    nameof(ZanoPoolPaymentProcessingConfigExtra.
                        HideMinerAddress),
                    result.SetHideMinerAddress);
                Project<int?>(source,
                    nameof(ZanoPoolPaymentProcessingConfigExtra.
                        MaximumDestinationPerTransfer),
                    result.SetMaximumDestinationPerTransfer);
                Project<bool?>(source,
                    nameof(ZanoPoolPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    result.SetKeepTransactionFees);
                Project<ulong?>(source,
                    nameof(ZanoPoolPaymentProcessingConfigExtra.MaxFee),
                    result.SetMaxFee);
                break;

            // Beam has no payment-processing extension contract. Unknown
            // fields, including fields supplied to families without a contract,
            // deliberately produce an empty public object.
            case CoinFamily.Beam:
                break;
        }

        return result;
    }

    private static void Project<T>(IDictionary<string, object> source,
        string configuredName, Action<string, T> setter)
    {
        var matches = source.Keys
            .Where(key => string.Equals(key, configuredName,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();

        // Normal configuration loading rejects case-variant duplicates. If an
        // unvalidated/programmatic dictionary bypasses that boundary, omit the
        // ambiguous value instead of choosing one by insertion order.
        if(matches.Length != 1)
            return;

        if(!TryConvert(source[matches[0]], out T value))
            return;

        setter(matches[0], value);
    }

    private static bool TryConvert<T>(object source, out T value)
    {
        try
        {
            if(source is T typed)
            {
                value = typed;
                return true;
            }

            if(source == null || source is JValue { Type: JTokenType.Null })
            {
                value = default;
                return true;
            }

            var token = source as JToken ?? JToken.FromObject(source);
            value = token.ToObject<T>();
            return true;
        }
        catch(Exception ex) when(ex is Newtonsoft.Json.JsonException or
            FormatException or InvalidCastException or ArgumentException or
            OverflowException)
        {
            // Public API projection is fail-closed. Runtime consumers retain
            // their original configuration and validation behavior.
            value = default;
            return false;
        }
    }
}
