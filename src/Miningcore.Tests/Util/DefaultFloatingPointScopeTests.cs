using System;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Util;

public class DefaultFloatingPointScopeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Run_PreservesFixtureAndRestorationFailures(bool fixtureFails, bool restorationFails)
    {
        var fixtureFailure = new InvalidOperationException("fixture failed");
        var restorationFailure = new InvalidOperationException("restoration failed");
        var scope = Substitute.For<IDisposable>();
        if(restorationFails)
            scope.When(x => x.Dispose()).Do(_ => throw restorationFailure);
        var failure = Record.Exception(() => DefaultFloatingPointScope.Run(() =>
        {
            if(fixtureFails)
                throw fixtureFailure;
        }, scope));
        if(fixtureFails && restorationFails)
            Assert.Equal(new[] { fixtureFailure, restorationFailure },
                Assert.IsType<AggregateException>(failure).InnerExceptions);
        else if(fixtureFails)
            Assert.Same(fixtureFailure, failure);
        else if(restorationFails)
            Assert.Same(restorationFailure, failure);
        else
            Assert.Null(failure);
        scope.Received(1).Dispose();
    }
}
