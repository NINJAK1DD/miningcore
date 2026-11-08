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
        Assert.Equal(PayoutRecoveryResult.Kept, await RunAsync(recovery, answers, output));
        Assert.Equal(0, recovery.Releases);
        Assert.Contains("Protection was kept", output.ToString());
    }

    [Fact]
    public async Task ExactConfirmations_ReleaseInspectedOwner()
    {
        var recovery = new Recovery();
        var output = new StringWriter();
        Assert.Equal(PayoutRecoveryResult.Ready, await RunAsync(recovery,
            $"3\nSTOPPED\nCHECKED\nincident-267\nRELEASE {recovery.Snapshot.OwnerId}\n", output));
        Assert.Equal(1, recovery.Releases);
        Assert.Same(recovery.Snapshot, recovery.Released);
        Assert.Contains("does NOT prove no payment was sent", output.ToString());
        Assert.Contains("Normal startup must acquire new ownership", output.ToString());
    }

    [Fact]
    public async Task RedirectedInput_CannotAuthorizeRelease()
    {
        var recovery = new Recovery();
        Assert.Equal(PayoutRecoveryResult.Kept, await RunAsync(recovery,
            $"3\nSTOPPED\nCHECKED\nincident-267\nRELEASE {recovery.Snapshot.OwnerId}\n", new StringWriter(), false));
        Assert.Equal(0, recovery.Releases);
    }

    [Fact]
    public async Task ActiveLock_RefusesBeforePrompting()
    {
        var recovery = new Recovery();
        recovery.Snapshot = recovery.Snapshot with { LockHeld = true };
        var output = new StringWriter();
        Assert.Equal(PayoutRecoveryResult.Busy, await RunAsync(recovery, "3\n", output));
        Assert.DoesNotContain("Choice", output.ToString());
        Assert.Equal(0, recovery.Releases);
    }

    [Fact]
    public async Task NoOwner_ContinuesWithoutPromptOrMutation()
    {
        var recovery = new Recovery();
        recovery.Snapshot = recovery.Snapshot with { OwnerId = null };
        Assert.Equal(PayoutRecoveryResult.Ready, await RunAsync(recovery, "", new StringWriter(), false));
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

    private static Task<PayoutRecoveryResult> RunAsync(Recovery recovery, string answers, TextWriter output,
        bool interactive = true) => PayoutOwnershipRecoveryAssistant.RunAsync(recovery,
            new StringReader(answers), output, interactive, CancellationToken.None);

    [Fact]
    public async Task UnattendedTerminal_ExpiresWithoutReleasing()
    {
        var recovery = new Recovery();
        var output = new StringWriter();
        var result = await PayoutOwnershipRecoveryAssistant.RunAsync(recovery, new WaitingReader(), output,
            true, CancellationToken.None, promptTimeout: TimeSpan.FromMilliseconds(30));
        Assert.Equal(PayoutRecoveryResult.Kept, result);
        Assert.Equal(0, recovery.Releases);
        Assert.Contains("timed out", output.ToString());
    }

    [Fact]
    public async Task StartupWithoutOwner_IsQuiet()
    {
        var recovery = new Recovery();
        recovery.Snapshot = recovery.Snapshot with { OwnerId = null };
        var output = new StringWriter();
        Assert.Equal(PayoutRecoveryResult.Ready, await PayoutOwnershipRecoveryAssistant.RunAsync(recovery,
            new StringReader(""), output, true, CancellationToken.None, quietIfUnowned: true));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task ConfirmationsTrimWhitespace_AndRecordIncident()
    {
        var recovery = new Recovery();
        Assert.Equal(PayoutRecoveryResult.Ready, await RunAsync(recovery,
            $" 3 \n STOPPED \n CHECKED \n incident-267 \n RELEASE {recovery.Snapshot.OwnerId} \n", new StringWriter()));
        Assert.Equal("incident-267", recovery.Incident);
        Assert.Equal(2, recovery.Inspections);
    }

    [Fact]
    public async Task ChangedOwnerBeforeConfirmation_IsRefused()
    {
        var recovery = new Recovery { ChangeOwnerOnRefresh = true };
        Assert.Equal(PayoutRecoveryResult.Kept, await RunAsync(recovery, "3\n", new StringWriter()));
        Assert.Equal(0, recovery.Releases);
    }

    [Fact]
    public async Task EvidenceIsRefreshedForEveryMenuReview()
    {
        var recovery = new Recovery();
        Assert.Equal(PayoutRecoveryResult.Kept, await RunAsync(recovery, "2\n2\n1\n", new StringWriter()));
        Assert.Equal(5, recovery.Inspections);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void StartupPrompt_RequiresTerminalAndAllowsOptOut(bool terminal, bool disabled, bool expected)
    {
        var config = new Miningcore.Configuration.ClusterConfig
        {
            PaymentProcessing = new() { Enabled = true },
            Pools = [new() { Enabled = true, PaymentProcessing = new() { Enabled = true } }],
        };
        Assert.Equal(expected, Program.ShouldOfferPayoutRecovery(config, terminal, disabled));
    }

    private sealed class WaitingReader : StringReader
    {
        public WaitingReader() : base("") { }
        public override async ValueTask<string> ReadLineAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }
    }

    private sealed class Recovery : IPayoutOwnershipRecovery
    {
        public PayoutOwnershipSnapshot Snapshot = new()
        {
            Database = "test", DatabaseRole = "test", Schema = "public", OwnerId = Guid.NewGuid(),
            Generation = 7, OwnerHost = "pool-host", OwnerProcessId = 123,
        };
        public int Releases;
        public int Inspections;
        public bool ChangeOwnerOnRefresh;
        public string Incident;
        public PayoutOwnershipSnapshot Released;

        public Task<PayoutOwnershipSnapshot> InspectAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(++Inspections > 1 && ChangeOwnerOnRefresh)
                Snapshot = Snapshot with { OwnerId = Guid.NewGuid() };
            return Task.FromResult(Snapshot);
        }

        public Task<PayoutRecoveryReceipt> ReleaseAsync(PayoutOwnershipSnapshot inspected, string incidentReference, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Releases++;
            Incident = incidentReference;
            Released = inspected;
            return Task.FromResult(new PayoutRecoveryReceipt(Guid.NewGuid(), DateTime.UtcNow));
        }
    }
}
