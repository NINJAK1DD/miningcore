using System;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Util;

public class DefaultFloatingPointScopeTests
{
    [Fact]
    public void BehaviorComparison_IgnoresInstructionBookkeeping()
    {
        var expected = new DefaultFloatingPointScope.State { ControlWord = 0x037f, TagWord = 0xffff, Mxcsr = 0x1f80 };
        var actual = expected;
        actual.InstructionPointer = 1;
        actual.DataPointer = 2;
        actual.Opcode = 3;
        actual.CodeSelector = 4;
        actual.DataSelector = 5;
        Assert.True(expected.HasSameBehavior(actual));
        Assert.True(actual.HasSameBehavior(expected));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BehaviorComparison_RejectsChangedControlStatusTagsOrMxcsr(int field)
    {
        var expected = new DefaultFloatingPointScope.State();
        var actual = expected;
        switch(field)
        {
            case 0: actual.ControlWord = 1; break;
            case 1: actual.StatusWord = 1; break;
            case 2: actual.TagWord = 1; break;
            case 3: actual.Mxcsr = 1; break;
        }
        Assert.False(expected.HasSameBehavior(actual), $"Expected: {expected}; actual: {actual}");
    }

    [Fact]
    public void StateDiagnostics_NameEachBehaviorWordInHex()
    {
        var state = new DefaultFloatingPointScope.State
        {
            ControlWord = 0x037f, StatusWord = 1, TagWord = 0xffff, Mxcsr = 0x9fc0,
        };
        var message = state.ToString();
        Assert.Contains("control=0x037f", message);
        Assert.Contains("status=0x0001", message);
        Assert.Contains("tags=0xffff", message);
        Assert.Contains("MXCSR=0x00009fc0", message);
    }

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
