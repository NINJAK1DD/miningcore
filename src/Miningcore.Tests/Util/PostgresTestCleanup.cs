using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Xunit;

namespace Miningcore.Tests.Util;

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
