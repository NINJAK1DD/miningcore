using System;
using Miningcore.Extensions;
using NBitcoin;

namespace Miningcore.Tests.Blockchain.Bitcoin;

internal static class BitcoinDirectSubmissionTestData
{
    internal static (string BlockHex, string BlockHash, string CoinbaseTxId)
        Create()
    {
        var network = Network.RegTest;
        var block = network.Consensus.ConsensusFactory.CreateBlock();
        block.Header.Version = 4;
        block.Header.HashPrevBlock = uint256.Zero;
        block.Header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        block.Header.Bits = network.GetGenesis().Header.Bits;
        block.Header.Nonce = 1;

        var coinbase = network.CreateTransaction();
        coinbase.Inputs.Add(TxIn.CreateCoinbase(1));
        coinbase.Outputs.Add(Money.Coins(50m), new Key().PubKey.Hash);
        block.Transactions.Add(coinbase);
        block.UpdateMerkleRoot();

        return (block.ToBytes().ToHexString(), block.GetHash().ToString(),
            coinbase.GetHash().ToString());
    }
}
