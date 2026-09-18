using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Miningcore.Tests.Blockchain.Bitcoin;
using Miningcore.Tests.Blockchain.BitcoinBlake2b;
using Miningcore.Tests.Mining;
using Miningcore.Tests.Payments;
using Miningcore.Tests.Persistence.Postgres;
using Miningcore.Tests.Rpc;
using Miningcore.Tests.Stratum;
using Miningcore.Tests.Util;
using Miningcore.Tests.Util.Postgres;
using Xunit;

namespace Miningcore.Tests;

public class NonParallelCollectionTests
{
    // Discover live suites from fixture injection, then account for every member
    // so a constructor refactor cannot silently remove a suite from the guard.
    private static Type[] IsolatedPostgresSuites()
    {
        var members = CollectionAssertions.Members(typeof(PostgresPolicyCollection)).ToArray();
        var liveSuites = members
            .Where(type => type.GetConstructors().Any(constructor => constructor.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(IsolatedPostgresServer))))
            .ToArray();
        // These exceptions are unit-only. Keep live database cases in server-injected
        // suites: the structural guard cannot detect database access inside a method.
        var unitSuites = new[] { typeof(PostgresConnectionPolicyTests), typeof(PostgresCommandTimeoutTests) };
        foreach(var unitSuite in unitSuites)
            Assert.Contains(unitSuite, members);
        foreach(var member in members)
        {
            var isLive = liveSuites.Contains(member);
            var isUnit = unitSuites.Contains(member);
            Assert.True(isLive != isUnit,
                $"{member.FullName} must be exactly one of a server-injected live suite or an explicit unit-only suite " +
                $"(server injection: {isLive}, unit exception: {isUnit}). Review fixture injection and collection classification.");
        }
        return liveSuites;
    }

    [Fact]
    public void PostgresPolicy_UsesOneCollectionServerForAllLiveTestClasses()
    {
        Assert.Contains(typeof(ICollectionFixture<IsolatedPostgresServer>),
            typeof(PostgresPolicyCollection).GetInterfaces());
        var suites = IsolatedPostgresSuites();
        Assert.NotEmpty(suites);
        foreach(var testClass in suites)
        {
            Assert.DoesNotContain(typeof(IClassFixture<IsolatedPostgresServer>), testClass.GetInterfaces());
            Assert.Same(typeof(PostgresPolicyCollection), CollectionAssertions.NonParallelDefinition(testClass));
        }
    }

    [Fact]
    public void IsolatedPostgresSuites_RequireOptInOnEveryTestMethod()
    {
        var suites = IsolatedPostgresSuites();
        Assert.NotEmpty(suites);
        foreach(var testClass in suites)
        {
            var methods = testClass.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.GetCustomAttributes<FactAttribute>(inherit: true).Any()).ToArray();
            Assert.NotEmpty(methods);
            foreach(var method in methods)
            {
                var attributes = method.GetCustomAttributes<FactAttribute>(inherit: true).ToArray();
                Assert.True(attributes.Length == 1,
                    $"{testClass.Name}.{method.Name} must declare exactly one isolated PostgreSQL opt-in attribute; " +
                    $"found {attributes.Length}: {string.Join(", ", attributes.Select(attribute => attribute.GetType().Name))}.");
                var attribute = attributes[0];
                Assert.True(attribute is IsolatedPostgresFactAttribute or IsolatedPostgresTheoryAttribute ||
                    (testClass == typeof(PostgresTlsIntegrationTests) && attribute is PostgresTlsUnixFactAttribute),
                    $"{testClass.Name}.{method.Name} must use an isolated PostgreSQL opt-in attribute, not {attribute.GetType().Name}.");
            }
        }
    }

    [Fact]
    public void NonParallelDefinitions_HaveExactlyTheReviewedInventory()
    {
        var expected = ReviewedCollections().Select(row => (Type) row[0])
            .Append(typeof(IntegrationDeadlineCollection));
        var actual = typeof(NonParallelCollectionTests).Assembly.GetTypes().Where(type =>
            type.GetCustomAttribute<CollectionDefinitionAttribute>(inherit: false)?.DisableParallelization == true);

        Assert.Equal(expected.Select(x => x.FullName).OrderBy(x => x),
            actual.Select(x => x.FullName).OrderBy(x => x));
    }

    public static IEnumerable<object[]> ReviewedCollections()
    {
        // The deadline collection has its own exact-membership contract.
        yield return new object[] { typeof(AdminApiEnvironmentCollection), new[] { typeof(AdminApiSecurityTests) } };
        yield return new object[] { typeof(BitcoinCorePayoutIntegrationCollection),
            new[] { typeof(BitcoinDirectSoloRegtestTests), typeof(BitcoinPayoutHandlerRegtestTests),
                typeof(BitcoinVersionRollingRegtestTests), typeof(BitcoinBlake2bRegtestTests),
                typeof(BitcoinBlake2bStartupTests), typeof(MergedMiningPayoutRegtestTests) } };
        yield return new object[] { typeof(RpcDiagnosticCollection),
            new[] { typeof(RpcDiagnosticTests), typeof(RpcConsumerDiagnosticTests) } };
        yield return new object[] { typeof(PayoutManagerLoggingCollection), new[] { typeof(PayoutManagerLoggingTests) } };
        yield return new object[] { typeof(StratumAdmissionMetricsCollection), new[] { typeof(StratumAdmissionTests) } };
        yield return new object[] { typeof(IPAccessWhitelistLoggingCollection), new[] { typeof(IPAccessWhitelistLoggingTests) } };
        yield return new object[] { typeof(ShareRecoveryLoggingCollection),
            new[] { typeof(ShareRecorderTests), typeof(ShareRecoveryPathOwnershipTests) } };
        yield return new object[] { typeof(PostgresConfigurationLoggingCollection), new[] { typeof(PostgresConfigurationLoggingTests) } };
        // These fixtures change process-wide logging, JSON settings, or PGSSLROOTCERT.
        // Timeout and payout tests share this isolation so configuration reads and physical opens
        // cannot observe the TLS fixtures' temporary process-wide settings.
        yield return new object[] { typeof(PostgresPolicyCollection),
            new[] { typeof(PostgresConnectionPolicyTests), typeof(PostgresTlsIntegrationTests),
                typeof(PostgresCommandTimeoutTests), typeof(PostgresCommandTimeoutIntegrationTests),
                typeof(PayoutHandlerPersistenceIntegrationTests) } };
    }

    [Theory]
    [MemberData(nameof(ReviewedCollections))]
    public void NonParallelCollection_HasExactlyTheReviewedMembers(Type definition, Type[] expected)
    {
        foreach(var fixture in expected)
            Assert.Same(definition, CollectionAssertions.NonParallelDefinition(fixture));

        Assert.Equal(expected.Select(x => x.FullName).OrderBy(x => x),
            CollectionAssertions.Members(definition).Select(x => x.FullName).OrderBy(x => x));
    }
}
