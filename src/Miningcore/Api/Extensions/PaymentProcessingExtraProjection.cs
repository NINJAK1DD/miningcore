using System.Collections.Frozen;
using System.Reflection;
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
    private static readonly FrozenDictionary<CoinFamily,
        PaymentProcessingExtraContract> Contracts =
        CreateContracts();

    public static ApiPoolPaymentProcessingExtra Create(CoinFamily family,
        IDictionary<string, object> source) =>
        Project(family, source, false).Projection;

    internal static PaymentProcessingExtraProjectionResult Analyze(
        CoinFamily family, IDictionary<string, object> source) =>
        Project(family, source, true);

    private static PaymentProcessingExtraProjectionResult Project(
        CoinFamily family, IDictionary<string, object> source,
        bool collectOmissions)
    {
        var contract = GetContract(family);

        if(source == null)
            return new PaymentProcessingExtraProjectionResult(null,
                Array.Empty<PaymentProcessingExtraOmission>());

        var result = new ApiPoolPaymentProcessingExtra();
        var classifiedKeys = collectOmissions ?
            new HashSet<string>(StringComparer.Ordinal) : null;
        var omissions = collectOmissions ?
            new List<PaymentProcessingExtraOmission>() : null;

        foreach(var field in contract.PublicFields)
            field.Project(result, source, classifiedKeys, omissions);

        if(collectOmissions)
        {
            foreach(var runtimeOnlyName in contract.RuntimeOnlyNames)
            {
                var matches = FindMatches(source, runtimeOnlyName);
                foreach(var match in matches)
                    classifiedKeys.Add(match);

                if(matches.Length == 1)
                {
                    omissions.Add(PaymentProcessingExtraOmission.Create(
                        runtimeOnlyName,
                        PaymentProcessingExtraProjectionOutcome.RuntimeOnlyKey));
                }
                else if(matches.Length > 1)
                {
                    omissions.Add(PaymentProcessingExtraOmission.Create(
                        runtimeOnlyName,
                        PaymentProcessingExtraProjectionOutcome.
                            AmbiguousCaseVariant,
                        matches.Length));
                }
            }

            // This startup-only traversal runs after the family contract has
            // classified every public and runtime-only name. Subtraction is the
            // single source of unknown-key decisions and cannot drift from the
            // projection or the family's actual runtime binder.
            foreach(var key in source.Keys.Where(key =>
                         !classifiedKeys.Contains(key))
                        .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(key => key, StringComparer.Ordinal))
            {
                omissions.Add(PaymentProcessingExtraOmission.Create(key,
                    PaymentProcessingExtraProjectionOutcome.UnknownKey));
            }
        }

        return new PaymentProcessingExtraProjectionResult(result,
            omissions != null ? omissions :
                Array.Empty<PaymentProcessingExtraOmission>());
    }

    internal static Type GetRuntimeContractType(CoinFamily family) =>
        GetContract(family).RuntimeType;

    private static PaymentProcessingExtraContract GetContract(
        CoinFamily family)
    {
        if(Contracts.TryGetValue(family, out var contract))
            return contract;

        // An unknown enum member is a developer classification error, not
        // malformed operator data. Fail startup and API projection loudly so a
        // new family cannot acquire a public contract by accident.
        throw new ArgumentOutOfRangeException(nameof(family), family,
            "Payment-processing response fields require an explicit family classification");
    }

    private static FrozenDictionary<CoinFamily,
        PaymentProcessingExtraContract> CreateContracts()
    {
        var bitcoin = Contract<BitcoinPoolPaymentProcessingConfigExtra>(
            Field<bool?>(nameof(BitcoinPoolPaymentProcessingConfigExtra.
                    MinersPayTxFees),
                static (result, name, value, wireValue) =>
                    result.SetMinersPayTxFees(name, value, wireValue)));

        return new Dictionary<CoinFamily,
            PaymentProcessingExtraContract>
        {
            [CoinFamily.Alephium] =
                Contract<AlephiumPaymentProcessingConfigExtra>(
                Field<string>(nameof(AlephiumPaymentProcessingConfigExtra.
                        WalletName),
                    static (result, name, value, wireValue) =>
                        result.SetWalletName(name, value, wireValue)),
                Field<long?>(nameof(AlephiumPaymentProcessingConfigExtra.
                        BlockRewardsLockTime),
                    static (result, name, value, wireValue) =>
                        result.SetBlockRewardsLockTime(name, value, wireValue)),
                Field<bool?>(nameof(AlephiumPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    static (result, name, value, wireValue) =>
                        result.SetKeepTransactionFees(name, value, wireValue))),
            [CoinFamily.Beam] = Contract(),
            [CoinFamily.Bitcoin] = bitcoin,
            [CoinFamily.Conceal] =
                Contract<ConcealPoolPaymentProcessingConfigExtra>(
                Field<decimal?>(nameof(ConcealPoolPaymentProcessingConfigExtra.
                        MinimumPaymentToPaymentId),
                    static (result, name, value, wireValue) =>
                        result.SetMinimumPaymentToPaymentId(name, value,
                            wireValue))),
            [CoinFamily.Cryptonote] =
                Contract<CryptonotePoolPaymentProcessingConfigExtra>(
                Field<decimal?>(nameof(CryptonotePoolPaymentProcessingConfigExtra.
                        MinimumPaymentToPaymentId),
                    static (result, name, value, wireValue) =>
                        result.SetMinimumPaymentToPaymentId(name, value,
                            wireValue)),
                Field<int?>(nameof(CryptonotePoolPaymentProcessingConfigExtra.
                        MaximumDestinationPerTransfer),
                    static (result, name, value, wireValue) =>
                        result.SetMaximumDestinationPerTransfer(name, value,
                            wireValue))),
            [CoinFamily.Equihash] = bitcoin,
            [CoinFamily.Ergo] = Contract<ErgoPaymentProcessingConfigExtra>(
                Field<int?>(nameof(ErgoPaymentProcessingConfigExtra.
                        MinimumConfirmations),
                    static (result, name, value, wireValue) =>
                        result.SetMinimumConfirmations(name, value,
                            wireValue))),
            [CoinFamily.Ethereum] =
                Contract<EthereumPoolPaymentProcessingConfigExtra>(
                Field<bool?>(nameof(EthereumPoolPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    static (result, name, value, wireValue) =>
                        result.SetKeepTransactionFees(name, value, wireValue)),
                Field<bool?>(nameof(EthereumPoolPaymentProcessingConfigExtra.
                        KeepUncles),
                    static (result, name, value, wireValue) =>
                        result.SetKeepUncles(name, value, wireValue)),
                Field<ulong?>(nameof(EthereumPoolPaymentProcessingConfigExtra.Gas),
                    static (result, name, value, wireValue) =>
                        result.SetGas(name, value, wireValue)),
                Field<ulong?>(nameof(EthereumPoolPaymentProcessingConfigExtra.
                        MaxFeePerGas),
                    static (result, name, value, wireValue) =>
                        result.SetMaxFeePerGas(name, value, wireValue)),
                Field<uint?>(nameof(EthereumPoolPaymentProcessingConfigExtra.
                        BlockSearchOffset),
                    static (result, name, value, wireValue) =>
                        result.SetBlockSearchOffset(name, value, wireValue))),
            [CoinFamily.Handshake] =
                Contract<HandshakePoolPaymentProcessingConfigExtra>(
                Field<string>(nameof(HandshakePoolPaymentProcessingConfigExtra.
                        WalletName),
                    static (result, name, value, wireValue) =>
                        result.SetWalletName(name, value, wireValue)),
                Field<string>(nameof(HandshakePoolPaymentProcessingConfigExtra.
                        WalletAccount),
                    static (result, name, value, wireValue) =>
                        result.SetWalletAccount(name, value, wireValue)),
                Field<bool?>(nameof(HandshakePoolPaymentProcessingConfigExtra.
                        MinersPayTxFees),
                    static (result, name, value, wireValue) =>
                        result.SetMinersPayTxFees(name, value, wireValue))),
            [CoinFamily.Kaspa] = Contract<KaspaPaymentProcessingConfigExtra>(
                Field<int?>(nameof(KaspaPaymentProcessingConfigExtra.
                        MinimumConfirmations),
                    static (result, name, value, wireValue) =>
                        result.SetMinimumConfirmations(name, value, wireValue)),
                Field<string>(nameof(KaspaPaymentProcessingConfigExtra.
                        VersionEnablingMaxFee),
                    static (result, name, value, wireValue) =>
                        result.SetVersionEnablingMaxFee(name, value, wireValue)),
                Field<ulong?>(nameof(KaspaPaymentProcessingConfigExtra.MaxFee),
                    static (result, name, value, wireValue) =>
                        result.SetMaxFee(name, value, wireValue))),
            [CoinFamily.Nexa] = bitcoin,
            [CoinFamily.Progpow] = bitcoin,
            [CoinFamily.Satoshicash] = bitcoin,
            [CoinFamily.Warthog] =
                Contract<WarthogPaymentProcessingConfigExtra>(
                Field<decimal?>(nameof(WarthogPaymentProcessingConfigExtra.
                        MaximumTransactionFees),
                    static (result, name, value, wireValue) =>
                        result.SetMaximumTransactionFees(name, value,
                            wireValue)),
                Field<bool?>(nameof(WarthogPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    static (result, name, value, wireValue) =>
                        result.SetKeepTransactionFees(name, value, wireValue)),
                Field<int?>(nameof(WarthogPaymentProcessingConfigExtra.
                        MinimumConfirmations),
                    static (result, name, value, wireValue) =>
                        result.SetMinimumConfirmations(name, value, wireValue)),
                Field<int?>(nameof(WarthogPaymentProcessingConfigExtra.
                        MaxDegreeOfParallelPayouts),
                    static (result, name, value, wireValue) =>
                        result.SetMaxDegreeOfParallelPayouts(name, value,
                            wireValue))),
            [CoinFamily.Xelis] = Contract<XelisPaymentProcessingConfigExtra>(
                Field<int?>(nameof(XelisPaymentProcessingConfigExtra.
                        MinimumConfirmations),
                    static (result, name, value, wireValue) =>
                        result.SetMinimumConfirmations(name, value, wireValue)),
                Field<int?>(nameof(XelisPaymentProcessingConfigExtra.
                        MaximumDestinationPerTransfer),
                    static (result, name, value, wireValue) =>
                        result.SetMaximumDestinationPerTransfer(name, value,
                            wireValue)),
                Field<bool?>(nameof(XelisPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    static (result, name, value, wireValue) =>
                        result.SetKeepTransactionFees(name, value, wireValue))),
            [CoinFamily.Zano] =
                Contract<ZanoPoolPaymentProcessingConfigExtra>(
                Field<decimal?>(nameof(ZanoPoolPaymentProcessingConfigExtra.
                        MinimumPaymentToPaymentId),
                    static (result, name, value, wireValue) =>
                        result.SetMinimumPaymentToPaymentId(name, value,
                            wireValue)),
                Field<bool?>(nameof(ZanoPoolPaymentProcessingConfigExtra.
                        RevealPoolAddress),
                    static (result, name, value, wireValue) =>
                        result.SetRevealPoolAddress(name, value, wireValue)),
                Field<bool?>(nameof(ZanoPoolPaymentProcessingConfigExtra.
                        HideMinerAddress),
                    static (result, name, value, wireValue) =>
                        result.SetHideMinerAddress(name, value, wireValue)),
                Field<int?>(nameof(ZanoPoolPaymentProcessingConfigExtra.
                        MaximumDestinationPerTransfer),
                    static (result, name, value, wireValue) =>
                        result.SetMaximumDestinationPerTransfer(name, value,
                            wireValue)),
                Field<bool?>(nameof(ZanoPoolPaymentProcessingConfigExtra.
                        KeepTransactionFees),
                    static (result, name, value, wireValue) =>
                        result.SetKeepTransactionFees(name, value, wireValue)),
                Field<ulong?>(nameof(ZanoPoolPaymentProcessingConfigExtra.MaxFee),
                    static (result, name, value, wireValue) =>
                        result.SetMaxFee(name, value, wireValue))),
        }.ToFrozenDictionary();
    }

    private static PaymentProcessingExtraContract Contract(
        params IPaymentProcessingExtraFieldProjection[] publicFields) =>
        new(null, publicFields);

    private static PaymentProcessingExtraContract Contract<TRuntime>(
        params IPaymentProcessingExtraFieldProjection[] publicFields) =>
        new(typeof(TRuntime), publicFields);

    private static IPaymentProcessingExtraFieldProjection Field<T>(string name,
        Action<ApiPoolPaymentProcessingExtra, string, T, JToken> setter) =>
        new PaymentProcessingExtraFieldProjection<T>(name, setter);

    private interface IPaymentProcessingExtraFieldProjection
    {
        string Name { get; }

        void Project(ApiPoolPaymentProcessingExtra result,
            IDictionary<string, object> source,
            ISet<string> classifiedKeys,
            ICollection<PaymentProcessingExtraOmission> omissions);
    }

    private sealed class PaymentProcessingExtraFieldProjection<T>(string name,
        Action<ApiPoolPaymentProcessingExtra, string, T, JToken> setter) :
        IPaymentProcessingExtraFieldProjection
    {
        public string Name => name;

        public void Project(ApiPoolPaymentProcessingExtra result,
            IDictionary<string, object> source,
            ISet<string> classifiedKeys,
            ICollection<PaymentProcessingExtraOmission> omissions)
        {
            var matches = FindMatches(source, name);

            foreach(var match in matches)
                classifiedKeys?.Add(match);

            if(matches.Length == 0)
                return;

            // Normal configuration loading rejects case-variant duplicates.
            // If an unvalidated/programmatic dictionary bypasses that boundary,
            // report one defect for the approved field and choose none by
            // insertion order.
            if(matches.Length > 1)
            {
                omissions?.Add(PaymentProcessingExtraOmission.Create(name,
                    PaymentProcessingExtraProjectionOutcome.
                        AmbiguousCaseVariant,
                    matches.Length));

                return;
            }

            var outcome = TryConvert(source[matches[0]], out T value,
                out var wireValue);

            if(outcome != PaymentProcessingExtraProjectionOutcome.Projected)
            {
                omissions?.Add(PaymentProcessingExtraOmission.Create(
                    matches[0], outcome));
                return;
            }

            // The typed value, exact configured name and detached wire scalar
            // are registered together. No name is reinterpreted as an enum
            // identity at this boundary.
            setter(result, matches[0], value, wireValue);
        }
    }

    private static string[] FindMatches(IDictionary<string, object> source,
        string name) => source.Keys
        .Where(key => string.Equals(key, name,
            StringComparison.OrdinalIgnoreCase))
        .OrderBy(key => key, StringComparer.Ordinal)
        .ToArray();

    private sealed class PaymentProcessingExtraContract
    {
        public PaymentProcessingExtraContract(Type runtimeType,
            IPaymentProcessingExtraFieldProjection[] publicFields)
        {
            RuntimeType = runtimeType;
            PublicFields = publicFields;

            var publicNames = publicFields.Select(field => field.Name)
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
            RuntimeOnlyNames = runtimeType == null ? Array.Empty<string>() :
                runtimeType.GetProperties(BindingFlags.Instance |
                        BindingFlags.Public)
                    .Where(property => property.CanWrite &&
                        property.GetIndexParameters().Length == 0 &&
                        !publicNames.Contains(property.Name))
                    .Select(property => property.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
        }

        public Type RuntimeType { get; }
        public IPaymentProcessingExtraFieldProjection[] PublicFields { get; }
        public string[] RuntimeOnlyNames { get; }
    }

    private static PaymentProcessingExtraProjectionOutcome TryConvert<T>(
        object source, out T value, out JToken wireValue)
    {
        try
        {
            var token = source as JToken ?? (source == null ?
                JValue.CreateNull() : JToken.FromObject(source));

            // All approved response members are scalars. Never hide an
            // arbitrary object, array or Newtonsoft-only Date token behind a
            // typed public property.
            if(!ApiPoolPaymentProcessingExtra.IsSupportedWireValue(token))
            {
                value = default;
                wireValue = null;
                return PaymentProcessingExtraProjectionOutcome.NonScalarValue;
            }

            if(source is T typed)
            {
                value = typed;
                wireValue = token;
                return PaymentProcessingExtraProjectionOutcome.Projected;
            }

            if(token.Type == JTokenType.Null)
            {
                value = default;
                wireValue = token;
                return PaymentProcessingExtraProjectionOutcome.Projected;
            }

            value = token.ToObject<T>();
            wireValue = token;
            return PaymentProcessingExtraProjectionOutcome.Projected;
        }
        catch(Exception)
        {
            // Classification is fail-closed and deliberately discards the
            // converter exception: exception text can include the rejected
            // value and must never enter startup diagnostics.
            value = default;
            wireValue = null;
            return PaymentProcessingExtraProjectionOutcome.ConversionFailure;
        }
    }
}

internal sealed record PaymentProcessingExtraProjectionResult(
    ApiPoolPaymentProcessingExtra Projection,
    IReadOnlyList<PaymentProcessingExtraOmission> Omissions);

internal sealed record PaymentProcessingExtraOmission(string DiagnosticKey,
    bool KeyWasRedacted, PaymentProcessingExtraProjectionOutcome Outcome,
    int VariantCount)
{
    public static PaymentProcessingExtraOmission Create(string key,
        PaymentProcessingExtraProjectionOutcome outcome,
        int variantCount = 1)
    {
        if(variantCount < 1)
            throw new ArgumentOutOfRangeException(nameof(variantCount));

        var diagnosticKey = PaymentProcessingExtraSensitivityPolicy.
            CreateDiagnosticKey(key, out var redacted);
        return new PaymentProcessingExtraOmission(diagnosticKey, redacted,
            outcome, variantCount);
    }
}

internal enum PaymentProcessingExtraProjectionOutcome
{
    Projected,
    RuntimeOnlyKey,
    UnknownKey,
    AmbiguousCaseVariant,
    NonScalarValue,
    ConversionFailure,
}
