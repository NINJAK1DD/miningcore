using System;
using System.Linq;
using System.Threading.Tasks;
using Miningcore.Mining;
using Xunit;

namespace Miningcore.Tests.Mining;

public class PoolOperationGateTests
{
    [Fact]
    public async Task Close_RejectsNewOperationsButDrainsEveryPreviouslyOwnedOperation()
    {
        var gate = new PoolOperationGate();
        var first = gate.TryAcquire();
        var second = gate.TryAcquire();
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, gate.ActiveCount);
        Assert.True(gate.Close());
        await gate.Failure.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(gate.TryAcquire());
        Assert.False(gate.Drained.IsCompleted);
        first.Dispose();
        first.Dispose(); // idempotence must not release somebody else's operation
        Assert.Equal(1, gate.ActiveCount);
        Assert.False(gate.Drained.IsCompleted);
        Assert.False(gate.Close());
        second.Dispose();
        await gate.Drained.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, gate.ActiveCount);
        Assert.Null(gate.TryAcquire());
    }

    [Fact]
    public async Task ConcurrentClose_DoesNotAffectOtherPoolsOrLoseOwnedOperations()
    {
        var gate = new PoolOperationGate();
        var sibling = new PoolOperationGate();
        using var retained = gate.TryAcquire();
        await Task.WhenAll(Enumerable.Range(0, 128).Select(i => Task.Run(() =>
        {
            using var operation = gate.TryAcquire();
            if(i % 3 == 0) gate.Close();
            using var healthy = sibling.TryAcquire();
            Assert.NotNull(healthy);
        })));
        Assert.True(gate.IsClosed);
        Assert.False(gate.Drained.IsCompleted);
        retained.Dispose();
        await gate.Drained.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(sibling.IsClosed);
        Assert.False(sibling.Failure.IsCompleted);
        Assert.True(sibling.Close());
        Assert.True(sibling.Drained.IsCompletedSuccessfully);
    }
}
