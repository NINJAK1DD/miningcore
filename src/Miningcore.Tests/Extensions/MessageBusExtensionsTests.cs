using System.Collections.Generic;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence.Model;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Extensions;

public class MessageBusExtensionsTests
{
    [Theory]
    [InlineData("auxpow")]
    [InlineData("merged-parent")]
    public void NotifyBlockUnlocked_FallsBackToGenericExplorerLink(string blockType)
    {
        var messageBus = Substitute.For<IMessageBus>();
        var coin = new BitcoinTemplate
        {
            Symbol = "TEST",
            Name = "Test Coin",
            ExplorerBlockLinks = new Dictionary<string, string>
            {
                ["block"] = "https://explorer.test/block/$hash$",
            },
        };
        var block = new Block
        {
            BlockHeight = 100,
            Hash = "block-hash",
            Type = blockType,
            Status = BlockStatus.Confirmed,
        };

        messageBus.NotifyBlockUnlocked("test-pool", block, coin);

        messageBus.Received(1).SendMessage(
            Arg.Is<BlockUnlockedNotification>(x =>
                x.ExplorerLink == "https://explorer.test/block/block-hash" &&
                x.BlockType == blockType),
            Arg.Any<string>());
    }
}
