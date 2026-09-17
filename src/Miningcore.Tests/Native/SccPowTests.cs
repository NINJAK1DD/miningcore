using System;
using Miningcore.Native;
using Xunit;

namespace Miningcore.Tests.Native;

public class SccPowTests
{
    [Fact]
    public void EpochContext_CanBeCreatedAndDestroyed()
    {
        var context = SccPow.CreateContext(0);

        try
        {
            Assert.NotEqual(IntPtr.Zero, context);
        }
        finally
        {
            if(context != IntPtr.Zero)
                SccPow.DestroyContext(context);
        }
    }
}
