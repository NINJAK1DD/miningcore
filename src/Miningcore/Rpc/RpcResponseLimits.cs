using Newtonsoft.Json;

namespace Miningcore.Rpc;

/// <summary>Opt-in limits for small consensus RPC responses, before JSON number parsing.</summary>
public sealed record RpcResponseLimits(int MaxBytes, int MaxNumericTokenChars)
{
    internal void ValidateNumbers(string json)
    {
        var quoted = false;
        for(var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if(quoted)
            {
                if(c == '\\') i++;
                else if(c == '"') quoted = false;
            }
            else if(c == '"') quoted = true;
            else if(c == '-' || c is >= '0' and <= '9')
            {
                var start = i;
                while(i < json.Length && (json[i] is >= '0' and <= '9' or '-' or '+' or '.' or 'e' or 'E'))
                {
                    if(i - start >= MaxNumericTokenChars)
                        throw new JsonReaderException("JSON-RPC numeric token exceeds its limit");
                    i++;
                }
                i--;
            }
        }
    }
}
