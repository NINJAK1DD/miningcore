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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingReleasePermission_KeepsBeforeConfirmations(bool revokedOnRefresh)
    {
        var recovery = new Recovery { DenyOnRefresh = revokedOnRefresh };
        if(!revokedOnRefresh)
            recovery.Snapshot = recovery.Snapshot with { ReleaseWarning = PostgresPayoutOwnershipRecovery.ReleaseDenied };
        var output = new StringWriter();
        Assert.Equal(PayoutRecoveryResult.Kept, await RunAsync(recovery, "3\nSTOPPED\nCHECKED\n", output));
        Assert.Contains("dedicated recovery identity", output.ToString());
        Assert.DoesNotContain("Type STOPPED", output.ToString());
        if(!revokedOnRefresh) Assert.DoesNotContain("Choice", output.ToString());
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

    [Theory]
    [InlineData("add_payout_recovery_audit.sql")]
    [InlineData("createdb.sql")]
    public void AuditFunctionLockKeys_MatchThePayoutLease(string script)
    {
        var sql = File.ReadAllText(Miningcore.Tests.Util.Postgres.PostgresTestScripts.PathFor(script));
        var body = sql.Split("CREATE OR REPLACE FUNCTION %I.record_payout_recovery(")[1]
            .Split("$body$ $ddl$")[0];
        var keys = System.Text.RegularExpressions.Regex.Match(body,
            @"classid\s*=\s*(\d+)\s+AND\s+objid\s*=\s*(\d+)\s+AND\s+objsubid\s*=\s*2");
        Assert.True(keys.Success, "The audit function must check the two-integer payout lock identity");
        Assert.Equal(PostgresPayoutManagerLease.LockNamespace,
            int.Parse(keys.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(PostgresPayoutManagerLease.LockKey,
            int.Parse(keys.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
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
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    public void StartupPrompt_RequiresTerminalAndAllowsOptOut(bool terminal, bool disabled, bool paused, bool expected)
    {
        var config = new Miningcore.Configuration.ClusterConfig
        {
            PaymentProcessing = new() { Enabled = true, Paused = paused },
            Pools = [
                new() { Id = "ltc", Enabled = true, PaymentProcessing = new() { Enabled = true } },
                new() { Id = "doge", Enabled = true, PaymentProcessing = new() { Enabled = true } },
            ],
        };
        Assert.Equal(expected, Program.ShouldOfferPayoutRecovery(config, terminal, disabled));
    }

    [Fact]
    public async Task CancellationWrappedByProvider_IsReportedWithoutStackTrace()
    {
        var output = new StringWriter();
        var recovery = new PostgresPayoutOwnershipRecovery(new CancelledOpenFactory());
        Assert.Equal(PayoutRecoveryResult.Kept, await PayoutOwnershipRecoveryAssistant.RunAsync(recovery,
            new StringReader(""), output, true, CancellationToken.None, promptTimeout: TimeSpan.FromMilliseconds(30)));
        Assert.Contains("timed out", output.ToString());
        Assert.DoesNotContain("provider-private-details", output.ToString());
    }

    [Fact]
    public async Task DeadlineDuringRelease_KeepsProtectionAndExplainsReinspection()
    {
        var recovery = new Recovery { WaitDuringRelease = true };
        var output = new StringWriter();
        var answers = $"3\nSTOPPED\nCHECKED\nincident\nRELEASE {recovery.Snapshot.OwnerId}\n";
        Assert.Equal(PayoutRecoveryResult.Kept, await PayoutOwnershipRecoveryAssistant.RunAsync(recovery,
            new StringReader(answers), output, true, CancellationToken.None, promptTimeout: TimeSpan.FromMilliseconds(50)));
        Assert.Contains("timed out", output.ToString());
        Assert.Equal(0, recovery.Releases);
    }

    [Fact]
    public async Task ActiveOperator_GetsFreshIdleBudgetForEachPrompt()
    {
        var recovery = new Recovery();
        var answers = $"3\nSTOPPED\nCHECKED\nincident\nRELEASE {recovery.Snapshot.OwnerId}\n";
        var output = new StringWriter();
        Assert.Equal(PayoutRecoveryResult.Ready, await PayoutOwnershipRecoveryAssistant.RunAsync(recovery,
            new SlowReader(answers), output, true, CancellationToken.None, promptTimeout: TimeSpan.FromMilliseconds(500)));
        Assert.DoesNotContain("--no-payout-recovery-prompt", output.ToString());
    }

    [Fact]
    public async Task InvalidAnswers_ExplainTheRequiredInput()
    {
        var output = new StringWriter();
        await RunAsync(new Recovery(), "wrong\n3\nSTOPPED\nCHECKED\n\n", output);
        Assert.Contains("Enter 1, 2 or 3", output.ToString());
        Assert.Contains("Invalid incident reference", output.ToString());
    }

    private sealed class SlowReader(string answers) : StringReader(answers)
    {
        public override async ValueTask<string> ReadLineAsync(CancellationToken ct)
        {
            await Task.Delay(150, ct);
            return await base.ReadLineAsync(ct);
        }
    }

    private sealed class CancelledOpenFactory : Miningcore.Persistence.IConnectionFactory, Miningcore.Persistence.ICancellableConnectionFactory
    {
        public Task<System.Data.IDbConnection> OpenConnectionAsync() => throw new InvalidOperationException();
        public async Task<System.Data.IDbConnection> OpenConnectionAsync(CancellationToken ct)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch(OperationCanceledException) { throw new Npgsql.NpgsqlException("provider-private-details"); }
            throw new InvalidOperationException();
        }
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
        public bool WaitDuringRelease;
        public int Releases;
        public int Inspections;
        public bool ChangeOwnerOnRefresh;
        public bool DenyOnRefresh;
        public string Incident;
        public PayoutOwnershipSnapshot Released;

        public Task<PayoutOwnershipSnapshot> InspectAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(++Inspections > 1 && ChangeOwnerOnRefresh)
                Snapshot = Snapshot with { OwnerId = Guid.NewGuid() };
            if(Inspections > 1 && DenyOnRefresh)
                Snapshot = Snapshot with { ReleaseWarning = PostgresPayoutOwnershipRecovery.ReleaseDenied };
            return Task.FromResult(Snapshot);
        }

        public async Task<PayoutRecoveryReceipt> ReleaseAsync(PayoutOwnershipSnapshot inspected, string incidentReference, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(WaitDuringRelease) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            Releases++;
            Incident = incidentReference;
            Released = inspected;
            return new PayoutRecoveryReceipt(Guid.NewGuid(), DateTime.UtcNow);
        }
    }
}
