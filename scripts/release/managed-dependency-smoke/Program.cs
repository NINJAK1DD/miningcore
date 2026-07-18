using NBitcoin.Zcash;

const string expectedHash = "a8e32ff1cd45abfa87778f7747dfe5d1a1da9960b02eabe8b243e526c051570e";

using var writer = new BLAKE2bWriter(new byte[16]);
var hash = writer.GetHash().ToString();

if(hash != expectedHash)
    throw new InvalidOperationException($"NBitcoin.Zcash BLAKE2b produced '{hash}', expected '{expectedHash}'");

Console.WriteLine($"NBitcoin.Zcash BLAKE2b runtime binding succeeded: {hash}");
