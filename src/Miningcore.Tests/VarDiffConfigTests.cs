using System.Linq;
using Miningcore.Configuration;
using Xunit;

namespace Miningcore.Tests;

public class VarDiffConfigTests
{
    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void EndpointRejectsInvalidDifficultyBounds(double invalid)
    {
        var options = new VarDiffConfig { MinDiff = 1, MaxDiff = invalid, TargetTime = 10,
            RetargetTime = 30, VariancePercent = 30 };
        var endpoint = new PoolEndpoint { Difficulty = 1, VarDiff = options };
        var validator = new PoolEndpointValidator();
        var result = validator.Validate(endpoint);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.PropertyName == "VarDiff.MaxDiff" && x.ErrorMessage.Contains("finite"));
        options.MaxDiff = null;
        options.MinDiff = invalid;
        result = validator.Validate(endpoint);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.PropertyName == "VarDiff.MinDiff" && x.ErrorMessage.Contains("finite"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1d)]
    [InlineData(double.MaxValue)]
    public void EndpointAcceptsPositiveBoundsAndOmittedMaximum(double? maximum)
    {
        var options = new VarDiffConfig { MinDiff = 1, MaxDiff = maximum, TargetTime = 10,
            RetargetTime = 30, VariancePercent = 30 };
        var result = new PoolEndpointValidator().Validate(new PoolEndpoint { Difficulty = 1, VarDiff = options });
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(x => x.ErrorMessage)));
    }
}
