using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Miningcore.Blockchain.Pearl;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlMainnetActivationTests
{
    [Fact]
    public void SyncedMainnetTemplateBuildsCurrentDenseJobWithWitnessTransactions()
    {
        using var stream = GetType().Assembly.GetManifestResourceStream("Miningcore.Tests.Fixtures.Pearl.mainnet-template.json");
        using var reader = new StreamReader(stream);
        var template = JsonConvert.DeserializeObject<PearlBlockTemplate>(reader.ReadToEnd());
        Assert.Equal(136635u, template.Height);
        var program = Encoders.Bech32("rprl").Decode(PearlWorkTests.Address, out var version);
        var address = Encoders.Bech32("prl").Encode(version, program);
        var job = new PearlBlockJob(template, address, PearlNetwork.Mainnet, new byte[24]);
        Assert.Equal(template.Height, job.Height);
        Assert.Equal(76, job.Header.Length);
        Assert.True(job.ExportTransactions().Length > 10000);
        Assert.Equal(54, template.Transactions.Length);
    }

    [Fact]
    public void ProofValidatedMainnetBlocksPinActivationVersionsAndHeaderCommitments()
    {
        using var stream = typeof(PearlMainnetActivationTests).Assembly.GetManifestResourceStream("Miningcore.Tests.Fixtures.Pearl.mainnet-activations.json");
        using var reader = new StreamReader(stream);
        var evidence = JObject.Parse(reader.ReadToEnd());
        Assert.Equal(PearlJobManager.Genesis(PearlNetwork.Mainnet), evidence.Value<string>("genesis"));
        Assert.Equal(12, ((JArray) evidence["vectors"]).Count);
        foreach(var vector in evidence["vectors"])
        {
            var height = vector.Value<long>("height");
            var version = vector.Value<uint>("certificateVersion");
            Assert.Equal(PearlNetworkRules.At(PearlNetwork.Mainnet, height).CertificateVersion, version);
            var data = Convert.FromHexString(vector.Value<string>("publicData"));
            var header = Convert.FromHexString(vector.Value<string>("header"));
            var preimage = new byte[data.Length + 4];
            BinaryPrimitives.WriteUInt32LittleEndian(preimage, version);
            data.CopyTo(preimage, 4);
            Assert.Equal(SHA256.HashData(SHA256.HashData(preimage)), header[76..]);
            var hash = SHA256.HashData(SHA256.HashData(header));
            Array.Reverse(hash);
            Assert.Equal(vector.Value<string>("hash"), Convert.ToHexString(hash).ToLowerInvariant());
            if(height >= 96251) Assert.True(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) >= 128);
            if(version < 3) Assert.Throws<InvalidDataException>(() => PearlNetworkRules.RequireCurrentMining(PearlNetwork.Mainnet, height, version));
            else PearlNetworkRules.RequireCurrentMining(PearlNetwork.Mainnet, height, version);
        }
    }
}
