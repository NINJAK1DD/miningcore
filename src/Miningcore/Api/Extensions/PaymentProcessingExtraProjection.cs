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
                Project<string>(result, source,
                    nameof(AlephiumPaymentProcessingConfigExtra.WalletName),
                    result.SetWalletName);
                Project<long?>(result, source,
                    nameof(AlephiumPaymentProcessingConfigExtra.
                        BlockRewardsLockTime),
                    result.SetBlockRewardsLockTime);
                Project<bool?>(result, source,
                    nameof(AlephiumPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    result.SetKeepTransactionFees);
                break;

            case CoinFamily.Bitcoin:
            case CoinFamily.Equihash:
            case CoinFamily.Nexa:
            case CoinFamily.Progpow:
            case CoinFamily.Satoshicash:
                Project<bool?>(result, source,
                    nameof(BitcoinPoolPaymentProcessingConfigExtra.
                        MinersPayTxFees),
                    result.SetMinersPayTxFees);
                break;

            case CoinFamily.Conceal:
                Project<decimal?>(result, source,
                    nameof(ConcealPoolPaymentProcessingConfigExtra.
                        MinimumPaymentToPaymentId),
                    result.SetMinimumPaymentToPaymentId);
                break;

            case CoinFamily.Cryptonote:
                Project<decimal?>(result, source,
                    nameof(CryptonotePoolPaymentProcessingConfigExtra.
                        MinimumPaymentToPaymentId),
                    result.SetMinimumPaymentToPaymentId);
                Project<int?>(result, source,
                    nameof(CryptonotePoolPaymentProcessingConfigExtra.
                        MaximumDestinationPerTransfer),
                    result.SetMaximumDestinationPerTransfer);
                break;

            case CoinFamily.Ergo:
                Project<int?>(result, source,
                    nameof(ErgoPaymentProcessingConfigExtra.
                        MinimumConfirmations),
                    result.SetMinimumConfirmations);
                break;

            case CoinFamily.Ethereum:
                Project<bool?>(result, source,
                    nameof(EthereumPoolPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    result.SetKeepTransactionFees);
                Project<bool?>(result, source,
                    nameof(EthereumPoolPaymentProcessingConfigExtra.
                        KeepUncles),
                    result.SetKeepUncles);
                Project<ulong?>(result, source,
                    nameof(EthereumPoolPaymentProcessingConfigExtra.Gas),
                    result.SetGas);
                Project<ulong?>(result, source,
                    nameof(EthereumPoolPaymentProcessingConfigExtra.
                        MaxFeePerGas),
                    result.SetMaxFeePerGas);
                Project<uint?>(result, source,
                    nameof(EthereumPoolPaymentProcessingConfigExtra.
                        BlockSearchOffset),
                    result.SetBlockSearchOffset);
                break;

            case CoinFamily.Handshake:
                Project<string>(result, source,
                    nameof(HandshakePoolPaymentProcessingConfigExtra.
                        WalletName),
                    result.SetWalletName);
                Project<string>(result, source,
                    nameof(HandshakePoolPaymentProcessingConfigExtra.
                        WalletAccount),
                    result.SetWalletAccount);
                Project<bool?>(result, source,
                    nameof(HandshakePoolPaymentProcessingConfigExtra.
                        MinersPayTxFees),
                    result.SetMinersPayTxFees);
                break;

            case CoinFamily.Kaspa:
                Project<int?>(result, source,
                    nameof(KaspaPaymentProcessingConfigExtra.
                        MinimumConfirmations),
                    result.SetMinimumConfirmations);
                Project<string>(result, source,
                    nameof(KaspaPaymentProcessingConfigExtra.
                        VersionEnablingMaxFee),
                    result.SetVersionEnablingMaxFee);
                Project<ulong?>(result, source,
                    nameof(KaspaPaymentProcessingConfigExtra.MaxFee),
                    result.SetMaxFee);
                break;

            case CoinFamily.Warthog:
                Project<decimal?>(result, source,
                    nameof(WarthogPaymentProcessingConfigExtra.
                        MaximumTransactionFees),
                    result.SetMaximumTransactionFees);
                Project<bool?>(result, source,
                    nameof(WarthogPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    result.SetKeepTransactionFees);
                Project<int?>(result, source,
                    nameof(WarthogPaymentProcessingConfigExtra.
                        MinimumConfirmations),
                    result.SetMinimumConfirmations);
                Project<int?>(result, source,
                    nameof(WarthogPaymentProcessingConfigExtra.
                        MaxDegreeOfParallelPayouts),
                    result.SetMaxDegreeOfParallelPayouts);
                break;

            case CoinFamily.Xelis:
                Project<int?>(result, source,
                    nameof(XelisPaymentProcessingConfigExtra.
                        MinimumConfirmations),
                    result.SetMinimumConfirmations);
                Project<int?>(result, source,
                    nameof(XelisPaymentProcessingConfigExtra.
                        MaximumDestinationPerTransfer),
                    result.SetMaximumDestinationPerTransfer);
                Project<bool?>(result, source,
                    nameof(XelisPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    result.SetKeepTransactionFees);
                break;

            case CoinFamily.Zano:
                Project<decimal?>(result, source,
                    nameof(ZanoPoolPaymentProcessingConfigExtra.
                        MinimumPaymentToPaymentId),
                    result.SetMinimumPaymentToPaymentId);
                Project<bool?>(result, source,
                    nameof(ZanoPoolPaymentProcessingConfigExtra.
                        RevealPoolAddress),
                    result.SetRevealPoolAddress);
                Project<bool?>(result, source,
                    nameof(ZanoPoolPaymentProcessingConfigExtra.
                        HideMinerAddress),
                    result.SetHideMinerAddress);
                Project<int?>(result, source,
                    nameof(ZanoPoolPaymentProcessingConfigExtra.
                        MaximumDestinationPerTransfer),
                    result.SetMaximumDestinationPerTransfer);
                Project<bool?>(result, source,
                    nameof(ZanoPoolPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    result.SetKeepTransactionFees);
                Project<ulong?>(result, source,
                    nameof(ZanoPoolPaymentProcessingConfigExtra.MaxFee),
                    result.SetMaxFee);
                break;

            // Beam has no payment-processing extension contract. Unknown
            // fields, including fields supplied to families without a contract,
            // deliberately produce an empty public object.
            case CoinFamily.Beam:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(family), family,
                    "Payment-processing response fields require an explicit family classification");
        }

        return result;
    }

    private static void Project<T>(ApiPoolPaymentProcessingExtra result,
        IDictionary<string, object> source, string configuredName,
        Action<string, T> setter)
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

        if(!TryConvert(source[matches[0]], out T value,
               out var wireValue))
            return;

        setter(matches[0], value);
        result.PreserveWireValue(matches[0], wireValue);
    }

    internal static bool TryConvert<T>(object source, out T value,
        out JToken wireValue)
    {
        try
        {
            var token = source as JToken ?? (source == null ?
                JValue.CreateNull() : JToken.FromObject(source));

            // All approved response members are scalars. Never hide an
            // arbitrary object or array behind a typed public property.
            if(!ApiPoolPaymentProcessingExtra.IsSupportedWireValue(token))
            {
                value = default;
                wireValue = null;
                return false;
            }

            if(source is T typed)
            {
                value = typed;
                wireValue = token;
                return true;
            }

            if(token.Type == JTokenType.Null)
            {
                value = default;
                wireValue = token;
                return true;
            }

            value = token.ToObject<T>();
            wireValue = token;
            return true;
        }
        catch(Exception)
        {
            // Public API projection is fail-closed. Runtime consumers retain
            // their original configuration and validation behavior. This
            // deliberately mirrors SafeExtensionDataAs: an unexpected
            // converter failure must omit one public field, not fail the
            // complete pool response.
            value = default;
            wireValue = null;
            return false;
        }
    }
}
