using System;
using Miningcore.Util;
using Xunit;

namespace Miningcore.Tests.Util;

public class FormatUtilTests
{
    [Theory]
    [InlineData(1000, "K")]
    [InlineData(1e9, "M")]
    [InlineData(1e100, "E")]
    [InlineData(double.MaxValue, "E")]
    [InlineData(double.PositiveInfinity, "E")]
    public void Quantity_UsesBoundedUnitIndexForLargeDiagnosticTotals(double quantity, string suffix)
    {
        Assert.EndsWith(suffix, FormatUtil.FormatQuantity(quantity));
    }
}
