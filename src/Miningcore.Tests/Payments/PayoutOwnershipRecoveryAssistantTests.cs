using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Payments;
using Xunit;

namespace Miningcore.Tests.Payments;

public class PayoutOwnershipRecoveryAssistantTests
{
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("1\n")]
    [InlineData("3\nno\n")]
    [InlineData("3\nSTOPPED\nno\n")]
    [InlineData("3\nSTOPPED\nCHECKED\nRELEASE wrong-owner\n")]
    [InlineData("2\n1\n")]
    [InlineData("invalid\n1\n")]
    public async Task DefaultsAndIncompleteConfirmations_KeepProtection(string answers)
    {
        var recovery = new Recovery();
        var output = new StringWriter();
        Assert.False(await RunAsync(recovery, answers, output));
        Assert.Equal(0, recovery.Releases);
        Assert.Contains("Protection was kept", output.ToString());
    }

    [Fact]
    public async Task ExactConfirmations_ReleaseInspectedOwner()
    {
        var recovery = new Recovery();
        var output = new StringWriter();
        Assert.True(await RunAsync(recovery,
            $"3\nSTOPPED\nCHECKED\nRELEASE {recovery.Snapshot.OwnerId}\n", output));
        Assert.Equal(1, recovery.Releases);
        Assert.Same(recovery.Snapshot, recovery.Released);
        Assert.Contains("does NOT prove no payment was sent", output.ToString());
        Assert.Contains("Normal startup must acquire new ownership", output.ToString());
    }

    [Fact]
    public async Task RedirectedInput_CannotAuthorizeRelease()
    {
        var recovery = new Recovery();
        Assert.False(await RunAsync(recovery,
            $"3\nSTOPPED\nCHECKED\nRELEASE {recovery.Snapshot.OwnerId}\n", new StringWriter(), false));
        Assert.Equal(0, recovery.Releases);
    }

    [Fact]
    public async Task ActiveLock_RefusesBeforePrompting()
    {
        var recovery = new Recovery();
        recovery.Snapshot = recovery.Snapshot with { LockHeld = true };
        var output = new StringWriter();
        Assert.False(await RunAsync(recovery, "3\n", output));
        Assert.DoesNotContain("Choice", output.ToString());
        Assert.Equal(0, recovery.Releases);
    }

    [Fact]
    public async Task NoOwner_ContinuesWithoutPromptOrMutation()
    {
        var recovery = new Recovery();
        recovery.Snapshot = recovery.Snapshot with { OwnerId = null };
        Assert.True(await RunAsync(recovery, "", new StringWriter(), false));
        Assert.Equal(0, recovery.Releases);
    }

    [Fact]
    public async Task Cancellation_DoesNotRelease()
    {
        var recovery = new Recovery();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PayoutOwnershipRecoveryAssistant.RunAsync(recovery, new StringReader("3\n"),
                new StringWriter(), true, new CancellationToken(true)));
        Assert.Equal(0, recovery.Releases);
    }

    [Fact]
    public async Task DatabaseText_CannotInjectTerminalPrompts()
    {
        var recovery = new Recovery();
        recovery.Snapshot = recovery.Snapshot with { OwnerHost = "host\nFAKE PROMPT\u001b[2J" };
        var output = new StringWriter();
        await RunAsync(recovery, "1\n", output);
        Assert.DoesNotContain("host\nFAKE PROMPT", output.ToString());
        Assert.DoesNotContain('\u001b', output.ToString());
    }

    private static Task<bool> RunAsync(Recovery recovery, string answers, TextWriter output,
        bool interactive = true) => PayoutOwnershipRecoveryAssistant.RunAsync(recovery,
            new StringReader(answers), output, interactive, CancellationToken.None);

    private sealed class Recovery : IPayoutOwnershipRecovery
    {
        public PayoutOwnershipSnapshot Snapshot = new()
        {
            Database = "test", DatabaseRole = "test", Schema = "public", OwnerId = Guid.NewGuid(),
            Generation = 7, OwnerHost = "pool-host", OwnerProcessId = 123,
        };
        public int Releases;
        public PayoutOwnershipSnapshot Released;

        public Task<PayoutOwnershipSnapshot> InspectAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Snapshot);
        }

        public Task ReleaseAsync(PayoutOwnershipSnapshot inspected, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Releases++;
            Released = inspected;
            return Task.CompletedTask;
        }
    }
}
