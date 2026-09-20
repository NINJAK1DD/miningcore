using System;
using Miningcore.Persistence.Postgres;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class PpsArithmeticSchemaContractTests
{
    [Fact]
    public void EmbeddedMigrationResolvesAllSafetyRoutines()
    {
        Assert.Contains("NEW.arithmeticversion<>expected", PpsArithmeticSchemaContract.CreditGuard);
        Assert.Contains("RAISE EXCEPTION", PpsArithmeticSchemaContract.TransitionGuard);
        Assert.Contains("LOCK TABLE pps_share_credits", PpsArithmeticSchemaContract.Activation);
        Assert.Contains("INSERT INTO pps_arithmetic_transitions", PpsArithmeticSchemaContract.Activation);
    }

    [Theory]
    [InlineData("$$")]
    [InlineData("$guard$")]
    public void RoutineHeadersCanContainDollarCharacters(string delimiter)
    {
        var migration = "CREATE OR REPLACE FUNCTION guard() RETURNS trigger " +
            "LANGUAGE plpgsql SET search_path TO \"$user\", public AS " + delimiter +
            " BEGIN RETURN NEW; END " + delimiter + ";";
        Assert.Equal("BEGIN RETURN NEW; END", PpsArithmeticSchemaContract.ReadBody(migration, "guard"));
    }

    [Fact]
    public void MissingRoutineHasAnActionableDiagnostic()
    {
        Assert.Contains("Missing PPS arithmetic routine: guard",
            Assert.Throws<InvalidOperationException>(() =>
                PpsArithmeticSchemaContract.ReadBody("", "guard")).Message);
    }
}
