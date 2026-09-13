using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

// Test-only cleanup: attempt every restoration, and report cleanup failures alongside
// the original test failure rather than replacing it or silently passing a damaged fixture.
internal static class PostgresTestCleanup
{
    internal static async Task RunAsync(Func<Task> body, params Func<Task>[] cleanup)
    {
        var errors = new List<Exception>();
        var bodyError = await Record.ExceptionAsync(body);
        if(bodyError != null)
            errors.Add(bodyError);
        foreach(var action in cleanup)
        {
            var error = await Record.ExceptionAsync(action);
            if(error != null)
                errors.Add(error);
        }
        if(errors.Count == 1)
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if(errors.Count > 1)
            throw new AggregateException("PostgreSQL test and cleanup failures (original failure first)", errors);
    }
}

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
