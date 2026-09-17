using System;
using System.Threading.Tasks;
using Xunit;

namespace Miningcore.Tests.Util.Postgres;

public class PostgresTestCleanupTests
{
    [Fact]
    public async Task BodyFailureSurvivesCleanupFailureAndAllRestorationsRun()
    {
        var bodyError = new InvalidOperationException("original test failure");
        var cleanupError = new InvalidOperationException("cleanup failure");
        var finalRestorationRan = false;
        var result = await Assert.ThrowsAsync<AggregateException>(() => PostgresTestCleanup.RunAsync(
            () => Task.FromException(bodyError),
            () => Task.FromException(cleanupError),
            () => { finalRestorationRan = true; return Task.CompletedTask; }));
        Assert.Equal(new[] { bodyError, cleanupError }, result.InnerExceptions);
        Assert.True(finalRestorationRan);
    }

    [Fact]
    public async Task CleanupFailureStillFailsAnOtherwiseSuccessfulTest()
    {
        var cleanupError = new InvalidOperationException("cleanup failure");
        var result = await Assert.ThrowsAsync<InvalidOperationException>(() => PostgresTestCleanup.RunAsync(
            () => Task.CompletedTask, () => Task.FromException(cleanupError)));
        Assert.Same(cleanupError, result);
    }

    [Fact]
    public async Task SuccessfulCleanupPreservesTheOriginalFailure()
    {
        var bodyError = new InvalidOperationException("original test failure");
        var result = await Assert.ThrowsAsync<InvalidOperationException>(() => PostgresTestCleanup.RunAsync(
            () => Task.FromException(bodyError), () => Task.CompletedTask));
        Assert.Same(bodyError, result);
    }
}
