using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.IO;
using Miningcore.Tests.Mining;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Miningcore.Tests.Util;

public class DefaultFloatingPointScopeTests
{
    private readonly ITestOutputHelper output;
    private readonly Action<string> diagnostic;

    public DefaultFloatingPointScopeTests(ITestOutputHelper output)
    {
        this.output = output;
        diagnostic = DefaultFloatingPointScope.Diagnostic(output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NumericFixtures_SurviveSupportedContaminationAndRestoreState(bool requestDaz)
    {
        if(!DefaultFloatingPointScope.IsSupported)
        {
            Assert.Null(DefaultFloatingPointScope.Enter(diagnostic));
            return;
        }
        RequireNativeProbe();
        using var baseline = DefaultFloatingPointScope.Enter(diagnostic);
        var daz = requestDaz && (FloatingPointNativeProbe.MxcsrMask() & 0x40u) != 0;
        output.WriteLine($"Synthetic control regression: request DAZ={requestDaz}, supported/enabled={daz}");

        // Warm the numeric paths under the default mode before deliberate changes.
        VarDiffManagerTests.VerifyExtremeRatio(1e-300, 1e-30, 1, double.Epsilon);
        CryptonoteDifficultyAssignmentTests.VerifyFrozenDeltaAndIncompatibleNormalizedStart();
        // Warm failure construction, numeric formatting and stack capture too.
        var warmLong = Record.Exception(() => Assert.Equal(1L, 0L));
        var warmDouble = Record.Exception(() => Assert.Equal(1d, 2d));
        var warmThrows = Record.Exception(() => { Assert.Throws<ArgumentOutOfRangeException>(() => { }); });
        Assert.IsType<EqualException>(warmLong);
        Assert.IsType<EqualException>(warmDouble);
        Assert.IsType<ThrowsException>(warmThrows);
        _ = warmLong.ToString();
        _ = warmDouble.ToString();
        _ = warmThrows.ToString();
        var diagnostics = new List<string>();
        var vardiff = new VarDiffManagerTests(diagnostics.Add);
        var cryptonote = new CryptonoteDifficultyAssignmentTests(diagnostics.Add);
        var original = DefaultFloatingPointScope.Capture();
        try
        {
            var native = original;
            native.ControlWord |= 0x0c3f;
            native.StatusWord |= 1;
            native.Mxcsr = (native.Mxcsr & ~0x40u) | 0xff80u | (daz ? 0x40u : 0u);
            DefaultFloatingPointScope.Restore(native);
            // Numeric/failure paths are warmed above; keep unrelated floating-point
            // work out of this deliberately contaminated window.
            var before = DefaultFloatingPointScope.Capture();
            Assert.Equal(daz ? 0x40u : 0u, before.Mxcsr & 0x40u);
            using(DefaultFloatingPointScope.Enter(diagnostics.Add))
            {
                var active = DefaultFloatingPointScope.Capture();
                Assert.Equal(0u, active.Mxcsr & 0xe040u);
                Assert.Equal(0, active.ControlWord & 0x0c00);
                Assert.Equal(0, active.StatusWord & 0x3f);
            }
            AssertSameBehavior(before);

            if(daz)
            {
                // Negative controls prove these exact paths fail without isolation.
                var underflow = Assert.IsType<EqualException>(Record.Exception(() =>
                    VarDiffManagerTests.VerifyExtremeRatio(1e-300, 1e-30, 1, double.Epsilon)));
                Assert.Matches(@"Expected:\s+1(?:\r?\n|$)", underflow.Message);
                Assert.Matches(@"Actual:\s+0(?:\r?\n|$)", underflow.Message);
                DefaultFloatingPointScope.Restore(before);
                var rejectedDelta = Assert.IsType<ThrowsException>(Record.Exception(
                    CryptonoteDifficultyAssignmentTests.VerifyFrozenDeltaAndIncompatibleNormalizedStart));
                Assert.Contains(nameof(ArgumentOutOfRangeException), rejectedDelta.Message);
                Assert.Contains("No exception was thrown", rejectedDelta.Message);
                DefaultFloatingPointScope.Restore(before);
            }

            vardiff.ExtremeRatios_AvoidIntermediateOverflowAndUnderflow(1e-300, 1e-30, 1, double.Epsilon);
            AssertSameBehavior(before);
            cryptonote.Configuration_RejectsFrozenDeltaAndIncompatibleNormalizedStart();
            AssertSameBehavior(before);
            Assert.Throws<InvalidOperationException>(() => DefaultFloatingPointScope.Run(
                () => throw new InvalidOperationException("fixture failed"), diagnostics.Add));
            AssertSameBehavior(before);
            Assert.Equal(4, diagnostics.Count);
            Assert.All(diagnostics, message =>
            {
                Assert.Contains("Non-default floating-point controls", message);
                Assert.Contains("MXCSR=", message);
            });

            var diagnosticFailure = new InvalidOperationException("diagnostic failed");
            Assert.Same(diagnosticFailure, Assert.Throws<InvalidOperationException>(() =>
                DefaultFloatingPointScope.Enter(_ => throw diagnosticFailure)));
            AssertSameBehavior(before);
        }
        finally
        {
            DefaultFloatingPointScope.Restore(original);
        }
    }

    [Fact]
    public void Enter_RejectsLiveX87StackWithoutResettingOrLosingItsValue()
    {
        if(!DefaultFloatingPointScope.IsSupported)
            return;
        RequireNativeProbe();
        using var baseline = DefaultFloatingPointScope.Enter(diagnostic);
        var original = DefaultFloatingPointScope.Capture();
        var diagnostics = new List<string>();
        var retainedValue = 0;
        FloatingPointNativeProbe.PushOne();
        try
        {
            var occupied = DefaultFloatingPointScope.Capture();
            Assert.NotEqual(ushort.MaxValue, occupied.TagWord);
            var error = Assert.Throws<InvalidOperationException>(() => DefaultFloatingPointScope.Enter(diagnostics.Add));
            Assert.Contains("empty x87 stack", error.Message);
            var after = DefaultFloatingPointScope.Capture();
            // Managed exception handling may set sticky inexact status. Check
            // mode, tags and stack position; the pop below verifies the value.
            Assert.True(occupied.HasSameControls(after), $"Expected: {occupied}; actual: {after}");
            Assert.Equal(occupied.TagWord, after.TagWord);
            Assert.Equal(occupied.StatusWord & 0x3800, after.StatusWord & 0x3800);
            Assert.Empty(diagnostics);
        }
        finally
        {
            retainedValue = FloatingPointNativeProbe.PopIsOne();
            DefaultFloatingPointScope.Restore(original);
        }
        Assert.Equal(1, retainedValue);
    }

    private static void AssertSameBehavior(DefaultFloatingPointScope.State expected)
    {
        var actual = DefaultFloatingPointScope.Capture();
        Assert.True(expected.HasSameBehavior(actual), $"Expected: {expected}{Environment.NewLine}Actual: {actual}");
    }

    private static void RequireNativeProbe() => Assert.True(
        File.Exists(Path.Combine(AppContext.BaseDirectory, "libminingcore_test_fenv.so")),
        "This regression requires a full glibc Linux x64 test build without MiningcoreStratumListenerSmoke=true");

    [Fact]
    public void EntryDiagnostic_IsRequiredEvenWhenIsolationIsUnsupported()
    {
        Assert.Throws<ArgumentNullException>(() => DefaultFloatingPointScope.Enter(null));
        var ran = false;
        Assert.Throws<ArgumentNullException>(() => DefaultFloatingPointScope.Run(() => ran = true, null));
        Assert.False(ran);
    }

    [Fact]
    public void RealDiagnostics_AlwaysUseCapturedTestOutput()
    {
        var testOutput = Substitute.For<ITestOutputHelper>();
        const string message = "mode 100%\r\n::error::not a command";
        DefaultFloatingPointScope.Diagnostic(testOutput)(message);
        testOutput.Received(1).WriteLine(message);
    }

    [Fact]
    public void ControlComparison_IgnoresStickyStatusAndTagsButDetectsChangedModes()
    {
        var expected = new DefaultFloatingPointScope.State { ControlWord = 0x037f, Mxcsr = 0x1f80 };
        var actual = expected;
        actual.StatusWord = 0x3f;
        actual.TagWord = 0xffff;
        actual.Mxcsr |= 0x3f;
        Assert.True(expected.HasSameControls(actual));
        actual.Mxcsr |= 0x8000; // FTZ, supported on the baseline.
        Assert.False(expected.HasSameControls(actual));
        actual = expected;
        actual.ControlWord ^= 0x0400;
        Assert.False(expected.HasSameControls(actual));
    }

    [Fact]
    public void Run_PreservesOriginalThrowSiteInStackTrace()
    {
        var scope = Substitute.For<IDisposable>();
        var failure = Assert.Throws<InvalidOperationException>(() =>
            DefaultFloatingPointScope.RunWithScope(ThrowFixtureFailure, scope));
        Assert.Contains(nameof(ThrowFixtureFailure), failure.StackTrace);
        scope.Received(1).Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFixtureFailure() => throw new InvalidOperationException("fixture failed");

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
        var failure = Record.Exception(() => DefaultFloatingPointScope.RunWithScope(() =>
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
