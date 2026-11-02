using System.IO;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace Miningcore.Blockchain.Pearl;

internal static class PearlAddress
{
    internal static Script RewardScript(string address, string prefix)
    {
        if(prefix is not ("prl" or "tprl" or "rprl"))
            throw new ArgumentOutOfRangeException(nameof(prefix));
        if(string.IsNullOrEmpty(address) || address.Length > 90)
            throw new InvalidDataException("Invalid Pearl reward address");
        try
        {
            var program = Encoders.Bech32(prefix).Decode(address, out var version);
            if(version != 1 || program.Length != 32)
                throw new InvalidDataException("Pearl rewards require a 32-byte Taproot program");
            return new Script(new byte[] {0x51, 0x20}.Concat(program).ToArray());
        }
        catch(FormatException)
        {
            throw new InvalidDataException("Invalid Pearl reward address");
        }
    }
}
