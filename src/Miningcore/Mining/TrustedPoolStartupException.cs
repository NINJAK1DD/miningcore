using Newtonsoft.Json;

namespace Miningcore.Mining;

// Only explicitly audited, source-authored configuration diagnostics may use this
// marker. Never interpolate daemon error/response text or secret configuration values.
internal sealed class TrustedPoolStartupException : PoolStartupException
{
    internal TrustedPoolStartupException(string message, string poolId = null,
        Exception originalFailure = null) : base(message, poolId)
    {
        OriginalFailure = originalFailure;
    }

    // Deliberately not InnerException: parser failures may contain key material.
    [JsonIgnore]
    internal Exception OriginalFailure { get; }
}
