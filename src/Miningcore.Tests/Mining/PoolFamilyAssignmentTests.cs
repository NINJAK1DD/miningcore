using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reactive;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Banning;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Alephium;
using Miningcore.Blockchain.Beam;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Conceal;
using Miningcore.Blockchain.Cryptonote;
using Miningcore.Blockchain.Ergo;
using Miningcore.Blockchain.Equihash;
using Miningcore.Blockchain.Ethereum;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Blockchain.Progpow;
using Miningcore.Blockchain.Warthog;
using Miningcore.Blockchain.Xelis;
using Miningcore.Blockchain.Zano;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Progpow;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain;
using Miningcore.Tests.Util;
using Miningcore.Time;
using Miningcore.VarDiff;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Mining;

// Exercise concrete pools, their real request handlers and their real broadcast /
// difficulty hooks. Only daemon responses and ready job templates are fixtures;
// the synchronization, protocol mutations, publication and connection are real.
public class PoolFamilyAssignmentTests : TestBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string[] Families = { "Alephium", "Beam", "Bitcoin", "Conceal", "Cryptonote",
        "Equihash", "Ergo", "Ethereum", "Handshake", "Kaspa", "Nexa", "Progpow", "Satoshicash", "Warthog", "Xelis", "Zano" };
    private static bool NeedsNative(string family) => family is "Conceal" or "Cryptonote" or "Zano";

    [Fact]
    public async Task XelisAuthorizationRetirementRaceClosesQuietlyAndReleasesAssignmentGate()
    {
        await using var fixture = await Fixture.Create(this, "Xelis");
        fixture.Time.Block = true;
        var authorization = Task.Run(() => fixture.Authorize());
        try
        {
            // Static difficulty processing is inside the assignment gate after
            // the final suspension check, immediately before job creation.
            await fixture.Time.Entered.Task.WaitAsync(Timeout);
            fixture.SuspendXelisWork();
        }
        finally
        {
            fixture.Time.Block = false;
            fixture.Time.Release.Set();
        }
        await authorization.WaitAsync(Timeout);
        await fixture.WaitDisconnected();
        Assert.True(fixture.Connection.IsDisconnectRequested);
        Assert.False(fixture.Connection.Context.IsAuthorized);
        Assert.Equal(1, fixture.Connection.Context.AssignmentGate.CurrentCount);
        Assert.DoesNotContain(fixture.Messages, m => m["method"]?.Value<string>() == "mining.notify");
        Assert.Empty(fixture.Errors.Logs);
        fixture.AssertNoBan();
    }

    [Fact]
    public async Task XelisListenerRefusesSuspendedTcpBeforeDispatchAndReopensAfterRecovery()
    {
        await using var fixture = await Fixture.Create(this, "Xelis");
        var endpoint = await fixture.StartXelisListener();
        fixture.SuspendXelisWork();
        var calls = fixture.DaemonCalls;
        using(var rejected = new TcpClient(AddressFamily.InterNetwork))
        {
            var rejectedDuringConnect = false;
            try { await rejected.ConnectAsync(endpoint.Address, endpoint.Port).WaitAsync(Timeout); }
            catch(SocketException ex) when(ex.SocketErrorCode is SocketError.ConnectionReset or
                SocketError.ConnectionAborted or SocketError.Shutdown)
            {
                // Fast abort can win ConnectAsync completion (EPIPE on Linux).
                // Refusal and timeout remain failures; no unrelated error is accepted.
                rejectedDuringConnect = true;
            }
            try
            {
                if(!rejectedDuringConnect)
                {
                    var received = await rejected.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(Timeout);
                    Assert.Equal(0, received);
                }
            }
            catch(SocketException ex) when(ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { }
            catch(System.IO.IOException ex) when(ex.InnerException is SocketException socket &&
                socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { }
        }
        Assert.Equal((0, 0), fixture.Pool.ConnectionAdmission.Snapshot);
        Assert.Equal(0, fixture.Pool.TrackedConnectionTaskCount);
        Assert.Equal(calls, fixture.DaemonCalls);
        fixture.AssertNoBan();
        await fixture.RefreshXelis("xel/v1");
        using var recovered = new TcpClient(AddressFamily.InterNetwork);
        await recovered.ConnectAsync(endpoint.Address, endpoint.Port).WaitAsync(Timeout);
        await recovered.GetStream().WriteAsync(Encoding.UTF8.GetBytes(
            "{\"id\":1,\"method\":\"mining.subscribe\",\"params\":[\"fixture\"]}\n"));
        using var reader = new System.IO.StreamReader(recovered.GetStream());
        var response = JObject.Parse(await reader.ReadLineAsync().WaitAsync(Timeout));
        Assert.Equal(1, response["id"].Value<int>());
        Assert.NotNull(response["result"]);
        Assert.True(response["error"] == null || response["error"].Type == JTokenType.Null);
        Assert.Equal(1, fixture.Pool.ConnectionAdmission.Snapshot.Active);
        Assert.Empty(fixture.Errors.Logs);
        fixture.AssertNoBan();
    }

    [Theory]
    [InlineData("mining.subscribe")] [InlineData("mining.authorize")]
    public async Task XelisSuspensionDisconnectsNewRequestsWithoutAddressRpcsOrBan(string method)
    {
        await using var fixture = await Fixture.Create(this, "Xelis");
        fixture.SuspendXelisWork();
        fixture.UseWire();
        var calls = fixture.DaemonCalls;
        await fixture.DispatchDisconnectingXelisRequest(method);
        await fixture.WaitDisconnected();
        Assert.True(fixture.Connection.IsDisconnectRequested);
        Assert.Equal(calls, fixture.DaemonCalls);
        fixture.AssertNoBan();
        Assert.DoesNotContain(fixture.Messages, x => x["method"]?.Value<string>() is "mining.notify" or "mining.set_difficulty");
        Assert.Empty(fixture.Errors.Logs);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task XelisRetirementRejectionsPastBanThresholdDoNotPenalizeMiners(bool recovered)
    {
        await using var fixture = await Fixture.Create(this, "Xelis");
        await fixture.Authorize();
        await fixture.Flush();
        var worker = fixture.Connection.ContextAs<XelisWorkerContext>();
        var old = worker.validJobs.Last();
        fixture.Pool.Config.Banning = new PoolShareBasedBanningConfig
            { Enabled = true, CheckThreshold = 2, InvalidPercent = 50, Time = 60 };
        // Hold the disconnect notification to exercise queued submissions and the
        // recovery tail independently of the active-session disconnect policy.
        fixture.SuspendXelisWork();
        if(recovered) fixture.RecoverXelisWork();
        for(var i = 0; i < 5; i++)
        {
            await fixture.SubmitXelis(old.JobId, 100 + i);
            fixture.AssertError(100 + i, StratumError.JobNotFound, "mining work unavailable");
        }
        Assert.Equal(0, worker.Stats.InvalidShares);
        Assert.Equal(0, worker.Stats.ValidShares);
        Assert.False(fixture.Connection.IsDisconnectRequested);
        fixture.AssertNoBan();
        if(recovered)
        {
            // Unknown work still follows the ordinary invalid-share policy.
            await fixture.SubmitXelis("unknown-job", 200);
            Assert.Equal(1, worker.Stats.InvalidShares);
        }
    }

    [Fact]
    public async Task XelisTransitionDisconnectsActualTcpSessionsAndRecoveryAllowsReauthorization()
    {
        await using var fixture = await Fixture.Create(this, "Xelis");
        fixture.UseWire();
        await fixture.DispatchLogin("mining.authorize", null, 1);
        fixture.AssertOneSuccess(1);
        var oldJob = fixture.Connection.ContextAs<XelisWorkerContext>().validJobs.Last();
        fixture.ObserveXelisSuspension();
        await fixture.RefreshXelis("xel/v3");
        await fixture.WaitDisconnected();
        fixture.AssertNoBan();
        Assert.True(oldJob.IsRetired);
        await fixture.RefreshXelis("xel/v1");
        await fixture.ReconnectXelis();
        fixture.UseWire();
        await fixture.DispatchLogin("mining.authorize", null, 1);
        fixture.AssertOneSuccess(1);
        Assert.True(fixture.Connection.Context.IsAuthorized);
        Assert.False(fixture.Connection.IsDisconnectRequested);
        Assert.Contains(fixture.Messages, m => m["method"]?.Value<string>() == "mining.notify");
        Assert.True(oldJob.IsRetired);
        fixture.AssertNoBan();
    }
    [Theory]
    [MemberData(nameof(XelisLifecycleTests.MalformedSubmissions), MemberType = typeof(XelisLifecycleTests))]
    public async Task XelisMalformedSubmissionReturnsControlledErrorAndKeepsSession(string json)
    {
        await using var fixture = await Fixture.Create(this, "Xelis");
        await fixture.Authorize();
        await fixture.Flush();
        var worker = fixture.Connection.ContextAs<XelisWorkerContext>();
        var jobs = worker.validJobs.ToArray();
        var calls = fixture.DaemonCalls;
        await fixture.SubmitXelisParameters(json == null ? null : JToken.Parse(json), 100);
        fixture.AssertError(100, StratumError.Other, "invalid params");
        Assert.Equal(1, worker.Stats.InvalidShares);
        Assert.Equal(0, worker.Stats.ValidShares);
        Assert.False(fixture.Connection.IsDisconnectRequested);
        Assert.True(worker.IsAuthorized);
        Assert.Equal(jobs, worker.validJobs.ToArray());
        Assert.Equal(calls, fixture.DaemonCalls);
        fixture.AssertNoBan();
        Assert.Empty(fixture.Errors.Logs);
    }

    public static IEnumerable<object[]> ManagedFamilies => Cases(false);
    public static IEnumerable<object[]> NativeFamilies => Cases(true);
    private static IEnumerable<object[]> Cases(bool native) => Families.Where(f => NeedsNative(f) == native)
        .SelectMany(f => new[] { new object[] { f, false }, new object[] { f, true } });

    [Theory]
    [MemberData(nameof(ManagedFamilies))]
    public Task ConcretePool_AuthorizationIdleAndBroadcastCompleteUnderContention(string family, bool idleOwnsGate) =>
        Race(family, idleOwnsGate);

    [LinuxNativeTheory]
    [MemberData(nameof(NativeFamilies))]
    public Task NativeConcretePool_AuthorizationIdleAndBroadcastCompleteUnderContention(string family, bool idleOwnsGate) =>
        Race(family, idleOwnsGate);

    [Fact]
    public Task Ethereum_AuthorizationCancellationDoesNotCommitQueuedDifficulty() => CanceledAuthorization("Ethereum");

    [Theory]
    [InlineData("null", false)]
    [InlineData("null", true)]
    [InlineData("\"invalid\"", false)]
    [InlineData("\"invalid\"", true)]
    [InlineData("{}", false)]
    [InlineData("{}", true)]
    [InlineData("[\"miner\",\"verified\",{},\"0xheader\",\"0xmix\"]", false)]
    [InlineData("[\"miner\",\"verified\",{},\"0xheader\",\"0xmix\"]", true)]
    [InlineData("[\"miner\",\"verified\",[],\"0xheader\",\"0xmix\"]", false)]
    [InlineData("[\"miner\",\"verified\",[],\"0xheader\",\"0xmix\"]", true)]
    [InlineData("[\"miner\",\"verified\",1,\"0xheader\",\"0xmix\"]", false)]
    [InlineData("[\"miner\",\"verified\",1,\"0xheader\",\"0xmix\"]", true)]
    [InlineData("[\"miner\",\"verified\",null,\"0xheader\",\"0xmix\"]", false)]
    [InlineData("[\"miner\",\"verified\",null,\"0xheader\",\"0xmix\"]", true)]
    [InlineData("[]", false)]
    [InlineData("[]", true)]
    [InlineData("[\"miner\",\"verified\",\"0xnonce\",\"0xheader\",\"0xmix\",\"extra\"]", false)]
    [InlineData("[\"miner\",\"verified\",\"0xnonce\",\"0xheader\",\"0xmix\",\"extra\"]", true)]
    public async Task ProgpowMalformedJsonIsRecoverableOnTcpBeforeUppercaseFirstProof(string malformed, bool configuredBanning)
    {
        await using var fixture = await Fixture.Create(this, "Progpow");
        if(configuredBanning)
            fixture.Pool.Config.Banning = new PoolShareBasedBanningConfig { Enabled = true, CheckThreshold = 100, InvalidPercent = 50, Time = 30 };
        var (cache, parameters) = fixture.PrepareProgpowProductionSubmission();
        await fixture.SubmitProgpow(JToken.Parse(malformed), 100);
        fixture.AssertError(100, StratumError.Other, "invalid params");
        Assert.Equal(0, cache.Calls);
        Assert.True(fixture.Connection.IsAlive);
        Assert.False(fixture.Connection.IsDisconnectRequested);
        await fixture.SubmitProgpow(parameters, 101);
        fixture.AssertOneSuccess(101);
        Assert.Equal(1, cache.Calls);
        Assert.Equal(1, fixture.Connection.Context.Stats.ValidShares);
        Assert.True(fixture.Connection.IsAlive);
        var alias = new JArray(parameters.Select(x => x.Value<string>().ToLowerInvariant()));
        await fixture.SubmitProgpow(alias, 102);
        fixture.AssertError(102, StratumError.DuplicateShare, "duplicate share");
        Assert.Equal(1, cache.Calls);
        Assert.Equal(1, fixture.Connection.Context.Stats.ValidShares);
        Assert.True(fixture.Connection.IsAlive);
        Assert.Empty(fixture.Errors.Logs);
    }

    [LinuxNativeTheory]
    [InlineData("Conceal")]
    [InlineData("Cryptonote")]
    [InlineData("Zano")]
    public Task NativePool_AuthorizationCancellationDoesNotCommitQueuedDifficulty(string family) => CanceledAuthorization(family);

    private async Task CanceledAuthorization(string family)
    {
        await using var fixture = await Fixture.Create(this, family);
        var worker = fixture.Connection.Context;
        var original = worker.VarDiff;
        using var cancel = new CancellationTokenSource();
        await worker.AssignmentGate.WaitAsync();
        try
        {
            var authorize = fixture.Authorize(cancel.Token);
            Assert.False(authorize.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorize.WaitAsync(Timeout));
            Assert.Same(original, worker.VarDiff);
            Assert.Equal(10, worker.Difficulty);
            Assert.False(worker.HasPendingDifficulty);
        }
        finally { worker.AssignmentGate.Release(); }
        await fixture.Flush();
        Assert.Equal(1, worker.AssignmentGate.CurrentCount);
        Assert.True(fixture.Connection.IsAlive);
        Assert.Empty(fixture.Errors.Logs);
    }

    [Theory]
    [InlineData("mining.authorize", "array")]
    [InlineData("eth_submitLogin", "array")]
    [InlineData("login", "object")]
    [InlineData("login", "top-level")]
    [InlineData("login", "nicehash-object")]
    [InlineData("login", "nicehash-top-level")]
    public async Task SharedAdmissionChargesOnlyDifficultyHintsAcrossLoginShapes(string method, string shape)
    {
        await using var fixture = await Fixture.Create(this, "Bitcoin");
        var admission = typeof(PoolBase).GetMethod("AdmitDifficultyRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Task<bool> Admit(bool hint)
        {
            var password = hint && !shape.StartsWith("nicehash", StringComparison.Ordinal) ? "d=40" : "x";
            var agent = hint && shape.StartsWith("nicehash", StringComparison.Ordinal) ? "NiceHash/1.0" : "miner";
            var request = new JsonRpcRequest(method, shape == "array" ? new JArray("miner", password) :
                shape.EndsWith("object", StringComparison.Ordinal) ? new JObject { ["login"] = "miner", ["pass"] = password, ["agent"] = agent } : null, 1);
            if(shape.EndsWith("top-level", StringComparison.Ordinal))
                request.Extra = new Dictionary<string, object> { ["login"] = "miner", ["pass"] = password, ["agent"] = agent };
            return (Task<bool>) admission.Invoke(fixture.Pool, new object[] { fixture.Connection, request });
        }
        for(var i = 0; i < 20; i++) Assert.True(await Admit(false));
        for(var i = 0; i < 8; i++) Assert.True(await Admit(true));
        Assert.False(await Admit(true));
        Assert.True(await Admit(false));
        Assert.False(fixture.Connection.IsDisconnectRequested);
        Assert.Equal(10, fixture.Connection.Context.Difficulty);
    }

    private async Task Race(string family, bool idleOwnsGate)
    {
        await using var fixture = await Fixture.Create(this, family);
        var worker = fixture.Connection.Context;
        Task idle;
        if(idleOwnsGate)
        {
            fixture.Time.Block = true;
            idle = Task.Run(fixture.Idle);
            await fixture.Time.Entered.Task.WaitAsync(Timeout);
        }
        else
        {
            await worker.AssignmentGate.WaitAsync();
            idle = fixture.Idle();
        }
        Task authorize = null, broadcast = null;
        try
        {
            authorize = fixture.Authorize();
            broadcast = fixture.Broadcast();
            if(!idleOwnsGate)
            {
                await idle.WaitAsync(Timeout); // Busy idle skips, never joins the queue.
                Assert.False(authorize.IsCompleted);
                Assert.Null(worker.VarDiff.LastUpdate);
            }
        }
        finally
        {
            if(idleOwnsGate)
            {
                fixture.Time.Block = false;
                fixture.Time.Release.Set();
            }
            else
                worker.AssignmentGate.Release();
        }
        await Task.WhenAll(idle, authorize, broadcast).WaitAsync(Timeout);
        await fixture.Flush();
        Assert.True(worker.IsAuthorized);
        Assert.Equal(55, worker.Difficulty);
        Assert.Null(worker.VarDiff);
        Assert.False(worker.HasPendingDifficulty);
        Assert.False(worker.ApplyPendingDifficulty());
        Assert.True(fixture.Connection.IsAlive);
        Assert.False(fixture.Connection.IsDisconnectRequested);
        Assert.Equal(1, worker.AssignmentGate.CurrentCount);
        Assert.Empty(fixture.Errors.Logs); // Broadcast Guard must not hide a failure.
        fixture.AssertFinalWireAssignment();
        await fixture.Idle().WaitAsync(Timeout);
        Assert.Equal(55, worker.Difficulty);
    }

    [Theory]
    [InlineData("Bitcoin")]
    [InlineData("Handshake")]
    [InlineData("Nexa")]
    [InlineData("Progpow")]
    [InlineData("Satoshicash")]
    public async Task StaticAuthorization_PublishesReplacementWithoutTemplateBroadcast(string family)
    {
        await using var fixture = await Fixture.Create(this, family);
        // Issue subscription-era work, then authorize at a different assignment.
        await fixture.Broadcast();
        await fixture.Flush();
        var oldNotice = fixture.Messages.Last(m => m["method"]?.Value<string>() == "mining.notify");
        fixture.Connection.Context.IsAuthorized = false;
        fixture.Messages.Clear();
        await fixture.Authorize();
        await fixture.Flush();
        fixture.AssertFinalWireAssignment();
        var replacement = Assert.Single(fixture.Messages.Where(m => m["method"]?.Value<string>() == "mining.notify"));
        Assert.NotEqual(oldNotice["params"][0].Value<string>(), replacement["params"][0].Value<string>());
        var targetIndex = fixture.Messages.FindLastIndex(m => m["method"]?.Value<string>() is "mining.set_difficulty" or "mining.set_target");
        Assert.True(fixture.Messages.IndexOf(replacement) > targetIndex);
        Assert.Empty(fixture.Errors.Logs);
    }

    [Theory]
    [InlineData("Bitcoin")]
    [InlineData("Nexa")]
    [InlineData("Satoshicash")]
    public async Task SuggestedDifficulty_PublishesReplacementWithoutTemplateBroadcast(string family)
    {
        await using var fixture = await Fixture.Create(this, family);
        await fixture.Broadcast();
        await fixture.Flush();
        var oldNotice = fixture.Messages.Last(m => m["method"]?.Value<string>() == "mining.notify");
        fixture.Messages.Clear();
        await fixture.SuggestDifficulty();
        fixture.AssertOneSuccess(2);
        fixture.AssertFinalWireAssignment();
        var replacement = Assert.Single(fixture.Messages.Where(m => m["method"]?.Value<string>() == "mining.notify"));
        Assert.NotEqual(oldNotice["params"][0].Value<string>(), replacement["params"][0].Value<string>());
        var targetIndex = fixture.Messages.FindLastIndex(m => m["method"]?.Value<string>() == "mining.set_difficulty");
        Assert.True(fixture.Messages.IndexOf(replacement) > targetIndex);
        Assert.Empty(fixture.Errors.Logs);
    }

    [Fact]
    public void ConcreteFamilyMatrix_CoversEveryGenericCoinFamilyPool()
    {
        var expected = typeof(PoolBase).Assembly.GetTypes().Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(PoolBase)) &&
            t.GetCustomAttribute<CoinFamilyAttribute>(inherit: false) != null &&
            t.Name is not ("BitcoinBlake2bPool" or "MergedMiningBitcoinPool"))
            .Select(t => t.Name[..^4]).OrderBy(x => x);
        // Pearl's object protocol is exercised by its dedicated concrete-pool fixture.
        var pearl = Blockchain.Pearl.PearlPoolTests.CoveredPool.Name[..^4];
        Assert.Equal(expected, Families.Append(pearl).OrderBy(x => x));
    }

    [Theory]
    [InlineData("Bitcoin", false)]
    [InlineData("Nexa", false)]
    [InlineData("Satoshicash", false)]
    [InlineData("Bitcoin", true)]
    [InlineData("Nexa", true)]
    [InlineData("Satoshicash", true)]
    [InlineData("Handshake", false)]
    [InlineData("Progpow", false)]
    [InlineData("Ethereum", false)]
    [InlineData("Kaspa", false)]
    [InlineData("Beam", false)]
    public async Task ReplacementConstructionFailureInvalidatesSessionBeforeGateRelease(string family, bool suggest)
    {
        await using var fixture = await Fixture.Create(this, family);
        await fixture.Broadcast();
        await fixture.Flush();
        var worker = fixture.Connection.Context;
        fixture.RemoveTemplateForFault();
        await Assert.ThrowsAnyAsync<Exception>(() => suggest ? fixture.SuggestDifficulty() : fixture.Authorize());
        Assert.True(fixture.Connection.IsDisconnectRequested);
        Assert.False(worker.IsAuthorized);
        Assert.Equal(1, worker.AssignmentGate.CurrentCount);
        Assert.False(worker.TryValidateShareDifficulty(double.PositiveInfinity, true, new object(), out _));
        Assert.False(worker.TryValidateShareDifficulty(double.PositiveInfinity, false, new object(), out _));
        if(family == "Bitcoin")
        {
            Assert.Single(fixture.AssignmentDiagnostics.Logs.Where(x => x.Contains("AssignmentPublicationFailure")));
            Assert.Empty(fixture.Errors.Logs); // Terminal cleanup must not duplicate the family diagnostic.
        }
        else
            Assert.NotEmpty(fixture.Errors.Logs);
    }

    [Theory]
    [InlineData("Bitcoin")]
    [InlineData("Nexa")]
    [InlineData("Satoshicash")]
    [InlineData("Handshake")]
    [InlineData("Progpow")]
    [InlineData("Ethereum")]
    [InlineData("Kaspa")]
    [InlineData("Beam")]
    public async Task VarDiffReplacementFailureClosesSessionBeforeGateRelease(string family)
    {
        await using var fixture = await Fixture.Create(this, family);
        await fixture.Broadcast();
        await fixture.Flush();
        fixture.Time.Timestamp = 30 * TimeSpan.TicksPerSecond;
        fixture.RemoveTemplateForFault();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Idle());
        var worker = fixture.Connection.Context;
        Assert.True(fixture.Connection.IsDisconnectRequested);
        Assert.False(worker.IsAuthorized);
        Assert.Equal(1, worker.AssignmentGate.CurrentCount);
        Assert.False(worker.TryValidateShareDifficulty(double.PositiveInfinity, true, new object(), out _));
        if(family != "Bitcoin") // Bitcoin retains its existing once-only Info diagnostic.
            Assert.NotEmpty(fixture.Errors.Logs);
    }

    [LinuxNativeTheory]
    [InlineData("Conceal", 0.5)]
    [InlineData("Conceal", 0.1)]
    [InlineData("Conceal", 0.00390625)]
    [InlineData("Cryptonote", 0.5)]
    [InlineData("Cryptonote", 0.1)]
    [InlineData("Cryptonote", 0.00390625)]
    [InlineData("Zano", 0.5)]
    [InlineData("Zano", 0.1)]
    [InlineData("Zano", 0.00390625)]
    public async Task NativePool_SubUnitRequestCannotPublishUnderweightedWork(string family, double invalid)
    {
        await using var fixture = await Fixture.Create(this, family);
        var worker = fixture.Connection.Context;
        var original = worker.VarDiff;
        var method = family == "Zano" ? "mining.authorize" : "login";
        await fixture.PrepareFreshLogin(method);
        await fixture.DispatchLogin(method, invalid, 1);
        fixture.AssertOneSuccess(1);
        Assert.True(worker.IsAuthorized);
        Assert.True(worker.IsSubscribed);
        Assert.Equal(10, worker.Difficulty);
        Assert.Same(original, worker.VarDiff);
        Assert.False(worker.HasPendingDifficulty);
        await fixture.DispatchLogin(method, 1, 2);
        fixture.AssertOneSuccess(2);
        Assert.Equal(1, worker.Difficulty);
        Assert.Null(worker.VarDiff);
        if(worker is ConcealWorkerContext conceal) Assert.Equal(1, conceal.validJobs.Last().Difficulty);
        if(worker is CryptonoteWorkerContext cryptonote) Assert.Equal(1, cryptonote.validJobs.Last().Difficulty);
        if(worker is ZanoWorkerContext zano) Assert.Equal(1, zano.validJobs.Last().Difficulty);
        var targets = fixture.Messages.Select(m => m["method"]?.Value<string>() == "job" ? m["params"] : (m["result"] as JObject)?["job"])
            .OfType<JObject>().Where(j => j["target"] != null).ToArray();
        if(family != "Zano") Assert.Equal("ffffffff", targets.Last()["target"].Value<string>());
        Assert.Empty(fixture.Errors.Logs);
        Assert.True(fixture.Connection.IsAlive);
    }

    public static IEnumerable<object[]> NativeFractionalHints =>
        from family in new[] { "Conceal", "Cryptonote" }
        from nicehash in new[] { false, true }
        from hint in new[] { 1.5, 1.99, 2.5 }
        select new object[] { family, nicehash, hint };

    [LinuxNativeTheory]
    [MemberData(nameof(NativeFractionalHints))]
    public async Task NativePool_DispatchedFractionalHintPublishesIntegerCredit(string family, bool nicehash, double hint)
    {
        await using var fixture = await Fixture.Create(this, family);
        await fixture.PrepareFreshLogin("login");
        if(nicehash) fixture.SetNicehashHint(hint);
        await fixture.DispatchLogin("login", nicehash ? null : hint, 1, nicehash);
        fixture.AssertOneSuccess(1);
        var expected = Math.Floor(hint);
        var worker = fixture.Connection.Context;
        Assert.Equal(expected, worker.Difficulty);
        Assert.Null(worker.VarDiff);
        Assert.True(worker.IsAuthorized && worker.IsSubscribed);
        await fixture.Broadcast();
        await fixture.Flush();
        if(worker is ConcealWorkerContext conceal) Assert.All(conceal.validJobs, job => Assert.Equal(expected, job.Difficulty));
        if(worker is CryptonoteWorkerContext cryptonote) Assert.All(cryptonote.validJobs, job => Assert.Equal(expected, job.Difficulty));
        var job = fixture.Messages.Last(message => message["method"]?.Value<string>() == "job")["params"];
        var raw = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString(job["target"].Value<string>()));
        var minerTarget = ulong.MaxValue / (uint.MaxValue / (ulong) raw);
        Assert.InRange((double) ulong.MaxValue / minerTarget / expected, 0.99, 1.01);
        Assert.Empty(fixture.Errors.Logs);
        Assert.Empty(fixture.Warnings.Logs);
        Assert.True(fixture.Connection.IsAlive);
    }

    public static IEnumerable<object[]> NativeLoginHints =>
        from path in new[] { ("Conceal", "login"), ("Cryptonote", "login"),
            ("Zano", "login"), ("Zano", "eth_submitLogin"), ("Zano", "mining.authorize") }
        from nicehash in new[] { false, true }
        from oversized in new[] { false, true }
        select new object[] { path.Item1, path.Item2, nicehash, oversized };

    [LinuxNativeTheory]
    [MemberData(nameof(NativeLoginHints))]
    public async Task NativePool_DispatchedHintsKeepOneResponseAndValidWork(string family, string protocol, bool nicehash, bool oversized)
    {
        await using var fixture = await Fixture.Create(this, family);
        var worker = fixture.Connection.Context;
        await fixture.PrepareFreshLogin(protocol);
        var original = worker.VarDiff;
        var invalid = oversized ? Math.BitIncrement(worker.MaximumDifficulty) : 0.5;
        if(nicehash) fixture.SetNicehashHint(invalid);
        await fixture.DispatchLogin(protocol, nicehash ? null : invalid, 1, nicehash);
        fixture.AssertOneSuccess(1);
        Assert.True(worker.IsAuthorized);
        Assert.True(worker.IsSubscribed);
        Assert.Equal(10, worker.Difficulty);
        Assert.Same(original, worker.VarDiff);
        Assert.False(worker.HasPendingDifficulty);
        Assert.Equal(oversized ? 1 : 0, fixture.Warnings.Logs.Count);
        if(oversized) Assert.Contains("CryptonoteDifficulty.StaticHintIgnored", fixture.Warnings.Logs.Single());
        await fixture.Broadcast();
        await fixture.Flush();
        if(worker is ConcealWorkerContext conceal) Assert.All(conceal.validJobs, j => Assert.Equal(10, j.Difficulty));
        if(worker is CryptonoteWorkerContext cryptonote) Assert.All(cryptonote.validJobs, j => Assert.Equal(10, j.Difficulty));
        if(worker is ZanoWorkerContext zano) Assert.All(zano.validJobs, j => Assert.Equal(10, j.Difficulty));
        if(nicehash) fixture.SetNicehashHint(1);
        await fixture.DispatchLogin(protocol, 1, 2, nicehash);
        fixture.AssertOneSuccess(2);
        Assert.Equal(1, worker.Difficulty);
        Assert.Null(worker.VarDiff);
        await fixture.Broadcast();
        await fixture.Flush();
        Assert.Empty(fixture.Errors.Logs);
        Assert.True(fixture.Connection.IsAlive);
    }

    [LinuxNativeTheory]
    [InlineData("Conceal", "login")]
    [InlineData("Cryptonote", "login")]
    [InlineData("Zano", "login")]
    [InlineData("Zano", "eth_submitLogin")]
    [InlineData("Zano", "mining.authorize")]
    public async Task NativePool_DispatchedLoginWaitsBeforeAuthorizing(string family, string protocol)
    {
        await using var fixture = await Fixture.Create(this, family);
        await fixture.PrepareFreshLogin(protocol);
        var worker = fixture.Connection.Context;
        await worker.AssignmentGate.WaitAsync();
        Task request;
        try
        {
            request = fixture.DispatchLogin(protocol, 55, 1);
            await fixture.RequestEntered.Task.WaitAsync(Timeout);
            Assert.False(worker.IsAuthorized);
            Assert.Equal(protocol == "mining.authorize", worker.IsSubscribed);
            Assert.DoesNotContain(fixture.Messages, m => m["id"]?.Type == JTokenType.Integer && m["id"].Value<int>() == 1);
            Assert.Equal(10, worker.Difficulty);
        }
        finally { worker.AssignmentGate.Release(); }
        await request.WaitAsync(Timeout);
        fixture.AssertOneSuccess(1);
        Assert.True(worker.IsAuthorized);
        Assert.Equal(55, worker.Difficulty);
    }

    public static IEnumerable<object[]> CandidateManagedFamilies => Families.Where(f => f != "Bitcoin" && !NeedsNative(f)).Select(f => new object[] { f });
    public static IEnumerable<object[]> CandidateNativeFamilies => Families.Where(NeedsNative).Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(CandidateManagedFamilies))]
    public Task CandidateCeilingsBoundQueuedAndFallingAssignmentsOnTcp(string family) => CandidateCeilingPublication(family);

    [LinuxNativeTheory]
    [MemberData(nameof(CandidateNativeFamilies))]
    public Task NativeCandidateCeilingsBoundQueuedAndFallingAssignmentsOnTcp(string family) => CandidateCeilingPublication(family);

    [Fact]
    public Task EthereumV1_CandidateCeilingsBoundEmbeddedTargetsOnTcp() => CandidateCeilingPublication("Ethereum", 1);

    private async Task CandidateCeilingPublication(string family, int protocol = 2)
    {
        await using var fixture = await Fixture.Create(this, family);
        var worker = fixture.Connection.Context;
        if(worker is EthereumWorkerContext ethereum) ethereum.ProtocolVersion = protocol;
        worker.EnqueueNewDifficulty(80);
        fixture.SetCandidateCeiling(20);
        await fixture.Broadcast();
        await fixture.Flush();
        Assert.InRange(worker.Difficulty, 19.99, 20.01);
        Assert.False(worker.HasPendingDifficulty);
        fixture.SetCandidateCeiling(5);
        await fixture.Broadcast();
        await fixture.Flush();
        Assert.InRange(worker.Difficulty, 4.99, 5.01);
        worker.VarDiff = null;
        worker.SetDifficulty(100);
        fixture.SetCandidateCeiling(3);
        await fixture.Broadcast();
        await fixture.Flush();
        Assert.Equal(100, worker.Difficulty);
        var warnings = fixture.Warnings.Logs.Count(x => x.Contains("Assignment ", StringComparison.Ordinal));
        Assert.Equal(1, warnings);
        fixture.SetCandidateCeiling(2);
        await fixture.Broadcast();
        await fixture.Flush();
        Assert.Equal(warnings, fixture.Warnings.Logs.Count(x => x.Contains("Assignment ", StringComparison.Ordinal)));
        Assert.Equal(100, worker.Difficulty);
        fixture.SetCandidateCeiling(200);
        await fixture.Broadcast();
        fixture.SetCandidateCeiling(3);
        await fixture.Broadcast();
        await fixture.Flush();
        Assert.Equal(warnings + 1, fixture.Warnings.Logs.Count(x => x.Contains("Assignment ", StringComparison.Ordinal)));
        Assert.True(fixture.Connection.IsAlive);
        Assert.Empty(fixture.Errors.Logs);
    }

    [Theory]
    [InlineData("Equihash")]
    [InlineData("Nexa")]
    [InlineData("Satoshicash")]
    public async Task ExplicitDeviceRequestsSurviveNetworkFallsAndLaterVarDiff(string family)
    {
        await using var fixture = await Fixture.Create(this, family);
        var worker = fixture.Connection.Context;
        var dynamicContext = worker.VarDiff;
        fixture.SetCandidateCeiling(20);
        await fixture.DeviceRequest(100);
        await fixture.Broadcast();
        await fixture.Flush();
        Assert.Same(dynamicContext, worker.VarDiff);
        Assert.InRange(worker.Difficulty, 99.99, 100.01);
        Assert.InRange(worker.ExplicitDifficultyFloor.Value, 99.99, 100.01);
        fixture.SetCandidateCeiling(5);
        worker.EnqueueNewDifficulty(900);
        await fixture.Broadcast();
        await fixture.Flush();
        Assert.InRange(worker.Difficulty, 99.99, 100.01);
        fixture.SetCandidateCeiling(200);
        worker.EnqueueNewDifficulty(150);
        await fixture.Broadcast();
        await fixture.Flush();
        Assert.Equal(150, worker.Difficulty);
        worker.EnqueueNewDifficulty(10);
        await fixture.Broadcast();
        await fixture.Flush();
        Assert.InRange(worker.Difficulty, 99.99, 100.01);
        Assert.Empty(fixture.Errors.Logs);
    }

    [Fact]
    public async Task BitcoinDoesNotInstallNonBitcoinAssignmentCallbacks()
    {
        await using var fixture = await Fixture.Create(this, "Bitcoin");
        Assert.Null(fixture.Connection.Context.BeforeApplyPendingDifficulty);
        Assert.Null(fixture.Connection.Context.AfterApplyPendingDifficulty);
    }

    [Fact]
    public async Task KaspaDisabledStaticHintIsAutomaticSeedCappedBeforeFirstWork()
    {
        await using var fixture = await Fixture.Create(this, "Kaspa");
        fixture.ConfigureKaspaStatic(false);
        fixture.SetCandidateCeiling(20);
        await fixture.Authorize(difficulty: 1000);
        await fixture.Flush();
        var worker = fixture.Connection.Context;
        Assert.NotNull(worker.VarDiff);
        Assert.Null(worker.ExplicitDifficultyFloor);
        Assert.InRange(worker.Difficulty, 19.99, 20.01);
        Assert.False(worker.HasPendingDifficulty);
        Assert.Empty(fixture.Errors.Logs);
    }

    [Theory]
    [InlineData("Nexa")] [InlineData("Satoshicash")]
    public async Task RepeatedSuggestRecordsFloorWithoutRepublishingIdenticalWork(string family)
    {
        await using var fixture = await Fixture.Create(this, family);
        var worker = fixture.Connection.Context;
        worker.SetDifficulty(55);
        worker.EnqueueNewDifficulty(80);
        await fixture.DeviceRequest(55, 10);
        await fixture.Flush();
        Assert.Equal(55, worker.ExplicitDifficultyFloor);
        Assert.False(worker.HasPendingDifficulty);
        var updates = fixture.Messages.Count(m => m["method"]?.Value<string>() is "mining.set_difficulty" or "mining.notify");
        Assert.Equal(0, updates);
        await fixture.DeviceRequest(55, 11);
        await fixture.Flush();
        Assert.Equal(updates, fixture.Messages.Count(m => m["method"]?.Value<string>() is "mining.set_difficulty" or "mining.notify"));
        fixture.AssertOneSuccess(10);
        fixture.AssertOneSuccess(11);
    }

    [LinuxNativeTheory]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task ZanoTemplateUpdateBetweenApplyAndSnapshotCannotSilentlyRetarget(int protocol, bool getJob)
    {
        await using var fixture = await Fixture.Create(this, "Zano");
        var worker = fixture.Connection.ContextAs<ZanoWorkerContext>();
        worker.ProtocolVersion = protocol;
        fixture.SetCandidateCeiling(20);
        var original = worker.AfterApplyPendingDifficulty;
        var applications = 0;
        worker.AfterApplyPendingDifficulty = () =>
        {
            original?.Invoke();
            if(++applications == 1) fixture.SetCandidateCeiling(5);
        };
        if(getJob) await fixture.ZanoGetJob(); else await fixture.Broadcast();
        await fixture.Flush();
        Assert.Equal(1, applications);
        Assert.Equal(10, worker.Difficulty);
        Assert.Equal(worker.Difficulty, worker.validJobs.Last().Difficulty);
        var start = fixture.Messages.Count;
        await fixture.Broadcast();
        await fixture.Flush();
        Assert.Equal(2, applications);
        Assert.Equal(5, worker.Difficulty);
        Assert.Equal(5, worker.validJobs.Last().Difficulty);
        if(protocol == 2)
        {
            var messages = fixture.Messages.Skip(start).ToArray();
            var difficulty = Array.FindIndex(messages, m => m["method"]?.Value<string>() == "mining.set_difficulty");
            var job = Array.FindIndex(messages, m => m["method"]?.Value<string>() == "mining.notify");
            Assert.True(difficulty >= 0 && job > difficulty);
            Assert.Equal(5, messages[difficulty]["params"][0].Value<double>());
        }
        Assert.Empty(fixture.Errors.Logs);
    }

    private sealed class LinuxNativeTheoryAttribute : TheoryAttribute
    {
        public LinuxNativeTheoryAttribute()
        {
            if(!OperatingSystem.IsLinux())
                Skip = "Native address/blob fixtures require the Linux test lab";
        }
    }

    private sealed class TimestampProvider : TimeProvider
    {
        internal volatile bool Block;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal long Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            if(Block)
            {
                Entered.TrySetResult();
                if(!Release.Wait(Timeout))
                    throw new TimeoutException("Concrete pool idle calculation did not release");
            }
            return Timestamp;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private ILifetimeScope scope;
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        private readonly CancellationTokenSource stop = new();
        private TcpClient client = new(AddressFamily.InterNetwork);
        private Task dispatch;
        private Task wireRead;
        private Task listenerLifetime;
        private readonly DaemonStub daemon = new();
        private readonly LogFactory logs = new();
        private readonly RecyclableMemoryStreamManager streams = new();
        private string family;
        private ClusterConfig cluster;
        private object job;
        private object manager;
        private MockMasterClock clock;
        private JsonSerializerSettings serializerSettings;
        private ILogger logger;
        private readonly IBanManager bans = Substitute.For<IBanManager>();
        internal int DaemonCalls => daemon.Calls;
        private TaskCompletionSource flushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource requestHandled;
        internal TaskCompletionSource RequestEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TimestampProvider Time = new();
        internal readonly NLog.Targets.MemoryTarget Errors = new() { Layout = "${level}|${message}" };
        internal readonly NLog.Targets.MemoryTarget Warnings = new() { Layout = "${level}|${message}" };
        internal readonly NLog.Targets.MemoryTarget AssignmentDiagnostics = new() { Layout = "${level}|${message}" };
        internal readonly List<JObject> Messages = new();
        internal PoolBase Pool { get; private set; }
        internal StratumConnection Connection { get; private set; }

        internal static async Task<Fixture> Create(PoolFamilyAssignmentTests test, string family)
        {
            var fixture = new Fixture();
            try
            {
                fixture.Initialize(test, family);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private void Initialize(PoolFamilyAssignmentTests test, string family)
        {
            this.family = family;
            serializerSettings = test.jsonSerializerSettings;
            clock = new MockMasterClock { CurrentTime = DateTime.UnixEpoch.AddDays(20000) };
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new RestStub()));
            scope = test.container.BeginLifetimeScope(b =>
            {
                b.RegisterInstance(Substitute.For<IBlockRepository>());
                b.RegisterInstance(Substitute.For<IShareRepository>());
                b.RegisterInstance(factory);
            });
            var extraNonce = Substitute.For<IExtraNonceProvider>();
            extraNonce.Next().Returns("00000001");
            extraNonce.ByteSize.Returns(4);
            var managerType = typeof(PoolBase).Assembly.GetType($"Miningcore.Blockchain.{family}.{family}JobManager", true);
            var constructor = managerType.GetConstructors().Single();
            manager = constructor.Invoke(constructor.GetParameters().Select(p =>
                p.ParameterType == typeof(IComponentContext) ? (object) scope :
                p.ParameterType == typeof(IMasterClock) ? clock :
                p.ParameterType == typeof(IExtraNonceProvider) ? extraNonce :
                p.ParameterType == typeof(IHttpClientFactory) ? factory : p.HasDefaultValue ? p.DefaultValue : scope.Resolve(p.ParameterType)).ToArray());
            var poolType = typeof(PoolBase).Assembly.GetType($"Miningcore.Blockchain.{family}.{family}Pool", true);
            Pool = (PoolBase) Activator.CreateInstance(poolType, scope, test.jsonSerializerSettings,
                Substitute.For<IConnectionFactory>(), Substitute.For<IStatsRepository>(), test.container.Resolve<IMapper>(),
                clock, Substitute.For<IMessageBus>(), streams, new NicehashService(factory, cache));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint) listener.LocalEndpoint;
            var options = new VarDiffConfig { MinDiff = 1, TargetTime = 10, RetargetTime = 5, VariancePercent = 1 };
            var port = new PoolEndpoint { Difficulty = 10, VarDiff = options };
            var coin = Coin(family);
            var config = new PoolConfig { Id = "concrete-" + family, EnableInternalStratum = true, Template = coin,
                Extra = new Dictionary<string, object> { ["z-address"] = "fixture", ["enableStaticDifficulty"] = true },
                Ports = new Dictionary<int, PoolEndpoint> { [endpoint.Port] = port } };
            cluster = new ClusterConfig { Banning = new ClusterBanningConfig { BanOnLoginFailure = false } };
            Pool.Configure(config, cluster);
            Pool.VarDiffTimeProvider = Time;
            SetField(Pool, "manager", manager);
            SetField(Pool, "banManager", bans);
            SetField(manager, "poolConfig", config);
            SetField(manager, "clusterConfig", cluster);
            SetField(manager, "coin", coin);
            // Name/type mismatches must fail for a family that relies on this
            // state. Do not silently skip a renamed member or leave enum zero.
            if(family is "Bitcoin" or "Equihash" or "Handshake" or "Nexa" or "Progpow" or "Satoshicash")
            {
                SetField(manager, "network", NBitcoin.Network.Main);
                SetProperty(manager, "maxActiveJobs", 8);
            }
            else if(family is "Alephium" or "Ergo" or "Kaspa" or "Xelis")
                SetField(manager, "network", "mainnet");
            else if(family == "Warthog") SetField(manager, "network", WarthogNetworkType.Mainnet);
            else if(family == "Ethereum") SetField(manager, "networkType", EthereumNetworkType.Main);
            else if(family == "Conceal") SetField(manager, "networkType", ConcealNetworkType.Main);
            else if(family == "Cryptonote") SetField(manager, "networkType", CryptonoteNetworkType.Main);
            else if(family == "Zano") SetField(manager, "networkType", ZanoNetworkType.Main);
            if(family is "Alephium" or "Beam" or "Ergo" or "Kaspa" or "Warthog" or "Xelis")
                SetField(manager, "maxActiveJobs", 8);
            if(family == "Beam") SetField(manager, "PoolNoncePrefix", "00000001");
            var logConfig = new NLog.Config.LoggingConfiguration();
            logConfig.AddRule(LogLevel.Error, LogLevel.Fatal, Errors);
            logConfig.AddRule(LogLevel.Warn, LogLevel.Warn, Warnings);
            logConfig.AddRule(LogLevel.Info, LogLevel.Info, AssignmentDiagnostics);
            logs.Configuration = logConfig;
            logger = logs.GetLogger(config.Id);
            SetField(Pool, "logger", logger);
            SetField(manager, "logger", logger);
            var rpc = new RpcClient(new DaemonEndpointConfig { Host = "127.0.0.1", Port = daemon.Port,
                User = "fixture", Password = "fixture" }, test.jsonSerializerSettings, Substitute.For<IMessageBus>(), config.Id);
            if(family is "Beam") SetField(manager, "walletRpc", rpc);
            else if(family is "Alephium")
            {
                var api = Substitute.For<AlephiumClient>("http://fixture", new HttpClient(new RestStub()));
                api.GetAddressesAddressGroupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new Group { Group1 = 0 });
                SetField(manager, "rpc", api);
            }
            else if(family is "Ergo")
            {
                var api = Substitute.For<ErgoClient>("http://fixture", new HttpClient(new RestStub()));
                api.CheckAddressValidityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AddressValidity { IsValid = true });
                SetField(manager, "rpc", api);
            }
            else if(family is "Warthog")
                SetField(manager, "restClient", new Miningcore.Rest.SimpleRestClient(factory, "http://fixture"));
            else if(family is not ("Ethereum" or "Kaspa" or "Conceal" or "Cryptonote" or "Zano"))
                SetField(manager, "rpc", rpc);
            job = ReadyJob(family, config, cluster);
            NonBitcoinCandidateCreditTests.ConfigurePolicy(job);
            SetField(manager, "currentJob", job);
            if(family == "Xelis")
                ((XelisJob) job).BindEpoch(XelisEpoch());
            if(family == "Equihash")
                SetProperty(manager, "ChainConfig", ((EquihashCoinTemplate) coin).GetNetwork(NBitcoin.ChainName.Mainnet));
            Connect(endpoint, port);
            Assert.Null(VarDiffManager.Update(Connection.Context, options, clock));
            Time.Timestamp = 5 * TimeSpan.TicksPerSecond;
        }

        private void Connect(IPEndPoint endpoint, PoolEndpoint port)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var socketEndpoint = (IPEndPoint) listener.LocalEndpoint;
            client.Connect(socketEndpoint);
            var socket = listener.AcceptSocket();
            Connection = new StratumConnection(logger, streams, clock, "concrete", false);
            Invoke(Pool, "OnConnect", Connection, endpoint);
            var worker = Connection.Context;
            worker.IsSubscribed = worker.IsAuthorized = true;
            worker.UserAgent = "fixture";
            if(family is not ("Conceal" or "Cryptonote" or "Zano"))
                SetProperty(worker, "ExtraNonce1", "00000001");
            if(family is "Ethereum" or "Zano") SetProperty(worker, "ProtocolVersion", 2);
            Invoke(Pool, "RegisterConnection", Connection);
            var onRequest = Pool.GetType().GetMethod("OnRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(StratumConnection), typeof(Timestamped<JsonRpcRequest>), typeof(CancellationToken) }, null);
            Assert.NotNull(onRequest);
            Connection.SendMessageOverride = (message, _) =>
            {
                RecordMessage(JObject.Parse(JsonConvert.SerializeObject(message, serializerSettings)));
                return Task.CompletedTask;
            };
            dispatch = Connection.DispatchAsync(socket, stop.Token, new StratumEndpoint(endpoint, port),
                (IPEndPoint) socket.RemoteEndPoint, null, async (connection, request, ct) =>
                {
                    RequestEntered.TrySetResult();
                    try { await (Task) onRequest.Invoke(Pool, new object[] { connection,
                        new Timestamped<JsonRpcRequest>(request, clock.Now), ct }); requestHandled?.TrySetResult(); }
                    catch(Exception ex) { requestHandled?.TrySetException(ex); throw; }
                }, _ => { }, (_, _) => { });
        }

        internal async Task ReconnectXelis()
        {
            await WaitDisconnected();
            if(wireRead != null) await wireRead.WaitAsync(Timeout);
            client.Dispose();
            Invoke(Pool, "UnregisterConnection", Connection);
            client = new TcpClient(AddressFamily.InterNetwork);
            Messages.Clear();
            var port = Pool.Config.Ports.First();
            Connect(new IPEndPoint(IPAddress.Loopback, port.Key), port.Value);
        }

        internal Task WaitDisconnected() => dispatch.WaitAsync(Timeout);
        internal async Task<IPEndPoint> StartXelisListener()
        {
            Connection.Disconnect();
            await WaitDisconnected();
            Invoke(Pool, "UnregisterConnection", Connection);
            var port = Pool.Config.Ports.Single();
            var endpoint = new StratumEndpoint(new IPEndPoint(IPAddress.Loopback, port.Key), port.Value);
            var reservation = new StratumListenerReservation(Pool.Config.Id, endpoint,
                StratumServer.CreateBoundSocket(endpoint.IPEndPoint));
            reservation.Activate();
            listenerLifetime = ((StratumServer) Pool).RunAsync(stop.Token, reservation);
            return endpoint.IPEndPoint;
        }
        internal void UseWire()
        {
            Connection.SendMessageOverride = null;
            wireRead = ReadWireMessages();
        }
        internal void AssertNoBan() => bans.DidNotReceiveWithAnyArgs().Ban(default, default);
        internal void ObserveXelisSuspension() => ((XelisPool) Pool).ObserveWorkSuspension();
        private XelisWorkEpoch XelisEpoch() => (XelisWorkEpoch) typeof(XelisJobManager)
            .GetField("workEpoch", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
        internal void RecoverXelisWork()
        {
            SetField(manager, "workEpoch", new XelisWorkEpoch());
            SetCandidateCeiling(100);
            ((XelisJob) job).BindEpoch(XelisEpoch());
        }
        internal async Task RefreshXelis(string algorithm)
        {
            daemon.XelisAlgorithm = algorithm;
            await (Task<bool>) Invoke(manager, "UpdateJob", stop.Token, null);
            job = ((XelisJobManager) manager).GetJobForStratum();
        }

        internal Task Idle() => (Task) Invoke(Pool, "UpdateVarDiffAsync", Connection, true, stop.Token);
        internal void SetCandidateCeiling(double ceiling)
        {
            job = ReadyJob(family, Pool.Config, cluster);
            NonBitcoinCandidateCreditTests.ConfigurePolicy(job, ceiling);
            SetField(manager, "currentJob", job);
        }

        internal void RemoveTemplateForFault() => SetField(manager, "currentJob", null);
        internal void SuspendXelisWork()
        {
            XelisEpoch().Retire();
        }
        internal void ConfigureKaspaStatic(bool enabled) => SetField(Pool, "extraPoolConfig",
            new Miningcore.Blockchain.Kaspa.Configuration.KaspaPoolConfigExtra { EnableStaticDifficulty = enabled });
        internal Task DeviceRequest(double difficulty, int id = 2) => DispatchRequest(new JsonRpcRequest(
            family == "Equihash" ? "mining.suggest_target" : "mining.suggest_difficulty",
            family == "Equihash" ? (object) new[] { (EquihashConstants.ZCashDiff1b / new System.Numerics.BigInteger(difficulty)).ToString("x").PadLeft(64, '0') } :
            family == "Nexa" ? (object) difficulty : new object[] { difficulty }, id));
        internal Task ZanoGetJob() => DispatchRequest(new JsonRpcRequest(ZanoStratumMethods.GetWork, Array.Empty<object>(), 30));
        internal Task SuggestDifficulty() => DispatchRequest(new JsonRpcRequest("mining.suggest_difficulty",
            family == "Nexa" ? (object) 55d : new object[] { 55d }, 2));
        internal Task Authorize(CancellationToken? ct = null, double difficulty = 55)
        {
            var address = family switch
            {
                "Ethereum" => "0x" + new string('1', 40),
                "Kaspa" => "kaspa:qzdtdjatlzecrt9u4v22p5vgud6w6ylvemly9df6zpu0gp0yks9xxp24q79pu",
                "Conceal" or "Cryptonote" or "Zano" => "48nhyWcSey31ngSEhV8j8NPm6B8PistCQJBjjDjmTvRSTWYg6iocAw131vE2JPh3ps33vgQDKLrUx3fcErusYWcMJBxpm1d",
                _ => "fixture",
            };
            var login = family is "Conceal" or "Cryptonote";
            var password = "d=" + difficulty.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            var request = new JsonRpcRequest(login ? "login" : family == "Beam" ? "login" : "mining.authorize",
                login ? JObject.FromObject(new { login = address, pass = password, agent = "fixture" }) : (object) new[] { address + ".worker", password }, 1);
            if(family == "Beam")
                request = JsonConvert.DeserializeObject<JsonRpcRequest>("{\"id\":1,\"method\":\"login\",\"api_key\":\"fixture.worker\",\"pass\":\"d=55\"}");
            if(family == "Xelis")
                request.Params = new[] { address + ".worker", "worker", "d=55" };
            return (Task) Invoke(Pool, login || family == "Beam" ? "OnLoginAsync" : "OnAuthorizeAsync", Connection,
                new Timestamped<JsonRpcRequest>(request, DateTimeOffset.UtcNow), ct ?? stop.Token);
        }
        internal Task Broadcast()
        {
            var method = FindMethod(Pool.GetType(), "OnNewJobAsync");
            if(method.GetParameters().Length == 0)
                return (Task) method.Invoke(Pool, null);
            object parameters = method.GetParameters()[0].ParameterType == typeof(Unit) ? Unit.Default :
                family == "Alephium" ? ((AlephiumJob) job).GetJobParams() :
                family == "Progpow" ? ((Miningcore.Blockchain.Progpow.ProgpowJob) job).GetJobParams(false) : new object[] { "ready", false };
            return (Task) method.Invoke(Pool, new[] { parameters });
        }
        internal async Task Flush()
        {
            flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await Connection.NotifyAsync("test.assignment.fence", Array.Empty<object>());
            await flushed.Task.WaitAsync(Timeout);
        }

        internal async Task PrepareFreshLogin(string protocol)
        {
            // These cases exercise both directions of the production TCP transport.
            Connection.SendMessageOverride = null;
            wireRead = ReadWireMessages();
            Connection.Context.IsAuthorized = Connection.Context.IsSubscribed = false;
            if(family == "Zano")
            {
                SetProperty(Connection.Context, "ProtocolVersion", 0);
                if(protocol == "mining.authorize")
                    await DispatchRequest(new JsonRpcRequest("mining.subscribe", new[] { "fixture", "fixture" }, 0));
            }
            RequestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void RecordMessage(JObject message)
        {
            Messages.Add(message);
            if(message["method"]?.Value<string>() == "test.assignment.fence")
                flushed.TrySetResult();
        }

        private async Task ReadWireMessages()
        {
            using var reader = new System.IO.StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true);
            try
            {
                while(await reader.ReadLineAsync(stop.Token) is { } line)
                    RecordMessage(JObject.Parse(line));
            }
            catch(Exception) when(stop.IsCancellationRequested) { }
            catch(System.IO.IOException) when(Connection.IsDisconnectRequested) { }
        }

        internal Task DispatchLogin(string method, double? difficulty, int id, bool nicehash = false)
        {
            const string address = "48nhyWcSey31ngSEhV8j8NPm6B8PistCQJBjjDjmTvRSTWYg6iocAw131vE2JPh3ps33vgQDKLrUx3fcErusYWcMJBxpm1d";
            var password = difficulty.HasValue ? "d=" + difficulty.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "x";
            var agent = nicehash ? "NiceHash" : "fixture";
            Connection.Context.UserAgent = agent;
            object parameters = method == "login" ? JObject.FromObject(new { login = address, pass = password, agent }) :
                new[] { address + ".worker", password, agent };
            return DispatchRequest(new JsonRpcRequest(method, parameters, id));
        }

        internal (ProgpowProductionVerifierTests.Cache Cache, JArray Parameters) PrepareProgpowProductionSubmission()
        {
            Connection.SendMessageOverride = null;
            wireRead = ReadWireMessages();
            var context = Connection.ContextAs<ProgpowWorkerContext>();
            context.VarDiff = null;
            var cache = new ProgpowProductionVerifierTests.Cache("Progpow");
            var source = ProgpowProductionVerifierTests.CreateSource("Progpow", clock, cache);
            var job = new ProgpowWorkerJob("verified", context.ExtraNonce1);
            ((ProgpowJob) source.ForWorker(context, preserveId: true)).PrepareWorkerJob(job, out var header);
            context.AddJob(job, 4);
            return (cache, new JArray("miner.worker", "verified", "0X000000000000000A", "0X" + header.ToUpperInvariant(),
                "0X" + ProgpowProductionVerifierTests.Cache.Mix.ToUpperInvariant()));
        }

        internal Task SubmitProgpow(object parameters, int id) => DispatchRequest(new JsonRpcRequest(ProgpowStratumMethods.SubmitShare, parameters, id));
        internal Task SubmitXelis(string jobId, int id) => DispatchRequest(new JsonRpcRequest(XelisStratumMethods.SubmitShare,
            new[] { "fixture", jobId, "0000000000000001" }, id));
        internal Task SubmitXelisParameters(object parameters, int id) =>
            DispatchRequest(new JsonRpcRequest(XelisStratumMethods.SubmitShare, parameters, id));
        internal async Task DispatchDisconnectingXelisRequest(string method)
        {
            requestHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(
                new JsonRpcRequest(method, new[] { "fixture", "worker", "x" }, 1)) + "\n"));
            await requestHandled.Task.WaitAsync(Timeout);
        }

        internal void AssertError(int id, StratumError code, string message)
        {
            var response = Assert.Single(Messages.Where(m => m["id"]?.Type == JTokenType.Integer && m["id"].Value<int>() == id));
            Assert.Equal((int) code, response["error"]["code"].Value<int>());
            Assert.Equal(message, response["error"]["message"].Value<string>());
        }

        private async Task DispatchRequest(JsonRpcRequest request)
        {
            requestHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(request) + "\n"));
            await requestHandled.Task.WaitAsync(Timeout);
            await Flush();
        }

        internal void AssertOneSuccess(int id)
        {
            var response = Assert.Single(Messages.Where(m => m["id"]?.Type == JTokenType.Integer && m["id"].Value<int>() == id));
            Assert.True(response["error"] == null || response["error"].Type == JTokenType.Null);
            Assert.NotNull(response["result"]);
            Assert.NotEqual(false, response["result"].Type == JTokenType.Boolean ? response["result"].Value<bool>() : true);
        }

        internal void SetNicehashHint(double difficulty)
        {
            var coin = Pool.Config.Template;
            var algorithm = coin.Name == "Monero" && coin.GetAlgorithmName() == "RandomX" ? "randomxmonero" : coin.GetAlgorithmName();
            cache.Set("nicehash_algos", new Dictionary<string, Miningcore.Nicehash.API.NicehashMiningAlgorithm>(StringComparer.OrdinalIgnoreCase)
                { [algorithm] = new() { Algorithm = algorithm, MinimalPoolDifficulty = difficulty } });
            cluster.Nicehash = new NicehashClusterConfig { EnableAutoDiff = true };
        }

        internal void AssertFinalWireAssignment()
        {
            if(family == "Beam")
            {
                var lastJob = Messages.Last(m => m["method"]?.Value<string>() == "job");
                Assert.Equal(55, BeamUtils.UnpackedDifficulty(lastJob["difficulty"].Value<long>()));
                Assert.False(string.IsNullOrEmpty(lastJob["input"].Value<string>()));
                return;
            }
            if(family is "Conceal" or "Cryptonote")
            {
                var jobs = Messages.Select(m => m["method"]?.Value<string>() == "job" ? m["params"] : m["result"]?["job"])
                    .Where(j => j is JObject).ToArray();
                Assert.NotEmpty(jobs);
                // Fixed 4-byte little-endian target for difficulty 55, calculated
                // independently from (2^256-1)/55 with protocol quantization.
                Assert.Equal("4a90a704", jobs[^1]["target"].Value<string>());
                Assert.False(string.IsNullOrEmpty(jobs[^1]["blob"].Value<string>()));
                return;
            }
            var method = family is "Equihash" or "Progpow" ? "mining.set_target" : "mining.set_difficulty";
            var lastAssignment = Messages.FindLastIndex(m => m["method"]?.Value<string>() == method);
            Assert.True(lastAssignment >= 0, $"{family} did not announce its assignment");
            var assigned = Messages[lastAssignment]["params"][0];
            if(family == "Equihash")
                Assert.Equal(EquihashUtils.EncodeTarget(55, ((EquihashCoinTemplate) Coin(family)).GetNetwork(NBitcoin.ChainName.Mainnet)),
                    assigned.Value<string>());
            else if(family == "Progpow")
                Assert.Equal(ProgpowUtils.RavencoinEncodeTarget(55), assigned.Value<string>());
            else if(family == "Ergo")
            {
                Assert.Equal(1, assigned.Value<double>()); // Normal Ergo miners receive the effective target in the job.
                var lastJob = Messages.Last(m => m["method"]?.Value<string>() == "mining.notify");
                var expected = BitcoinConstants.Diff1 * 1191 / 65536; // floor(65536 / 55), protocol precision.
                Assert.Equal(expected.ToString(), lastJob["params"][6].Value<string>());
            }
            else
                Assert.Equal(55, assigned.Value<double>());
            // Some protocols apply set_difficulty to the next job, while keeping
            // current work. The assertion here covers the last announced value;
            // bounded in-flight proof acceptance remains tracked separately.
            Assert.Contains(Messages, m => m["method"]?.Value<string>() == "mining.notify");
        }
        public async ValueTask DisposeAsync()
        {
            Time.Block = false;
            Time.Release.Set();
            stop.Cancel();
            client.Dispose();
            try
            {
                if(dispatch != null)
                    await dispatch.WaitAsync(Timeout);
                if(wireRead != null)
                    await wireRead.WaitAsync(Timeout);
                if(listenerLifetime != null)
                    await listenerLifetime.WaitAsync(Timeout);
            }
            finally
            {
                try { await daemon.DisposeAsync(); }
                finally
                {
                    Time.Release.Dispose();
                    stop.Dispose();
                    scope?.Dispose();
                    cache.Dispose();
                    logs.Dispose();
                }
            }
        }
    }

    private static CoinTemplate Coin(string family)
    {
        var name = family switch { "Progpow" => "ravencoin", "Cryptonote" => "monero", "Equihash" => "zcash", _ => family.ToLowerInvariant() };
        var original = ModuleInitializer.CoinTemplates[name];
        var coin = (CoinTemplate) typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(original, null);
        // Native authorization fixtures use the known-valid prefix-18 address.
        foreach(var property in coin.GetType().GetProperties().Where(p => p.Name.StartsWith("AddressPrefix") && p.CanWrite))
            property.SetValue(coin, Convert.ChangeType(18, property.PropertyType));
        return coin;
    }
    private static object ReadyJob(string family, PoolConfig config, ClusterConfig cluster)
    {
        if(family == "Progpow") return JobNotificationSnapshotTests.CreateProgpowJob("progpow");
        if(family is "Equihash" or "Xelis" or "Warthog" or "Ergo" or "Satoshicash")
        {
            var job = JobNotificationSnapshotTests.CreateArrayJob(family.ToLowerInvariant()).Job;
            SetProperty(job, "JobId", "ready");
            return job;
        }
        if(family == "Ethereum") return new EthereumJob("ready", new EthereumBlockTemplate
        { Target = new string('f', 64), Header = "0x" + new string('1', 64), Seed = "0x" + new string('2', 64), Height = 101 }, LogManager.GetCurrentClassLogger(), null);
        if(family == "Alephium")
        {
            var job = new AlephiumJob();
            job.Init(new AlephiumBlockTemplate { JobId = "ready", TargetBlob = new string('f', 64), HeaderBlob = "aabb", Height = 101 });
            return job;
        }
        if(family == "Beam")
        {
            var job = new BeamJob();
            SetProperty(job, "JobId", "ready");
            SetProperty(job, "BlockTemplate", new Miningcore.Blockchain.Beam.DaemonResponses.BeamBlockTemplate { Height = 101, Input = "aabb" });
            return job;
        }
        if(NeedsNative(family)) return NativeJob(family, config, cluster);
        var type = typeof(PoolBase).Assembly.GetType($"Miningcore.Blockchain.{family}.{family}Job", true);
        var result = RuntimeHelpers.GetUninitializedObject(type);
        SetProperty(result, "JobId", "ready");
        SetField(result, "jobParams", family == "Kaspa" ? new object[] { "ready", "aabb", new ulong[4], 123L } :
            new object[] { "ready", "prev", "prefix", "suffix", Array.Empty<string>(), "version", "bits", "time", false });
        return result;
    }

    private static object NativeJob(string family, PoolConfig config, ClusterConfig cluster)
    {
        var offset = NativeBlob.IndexOf("020800000000012e7f76", StringComparison.Ordinal) / 2 + 2;
        if(family == "Conceal")
            return new ConcealJob(new Miningcore.Blockchain.Conceal.DaemonResponses.GetBlockTemplateResponse
                { Blob = NativeBlob, Difficulty = 1000, Height = 101, ReservedOffset = offset }, new byte[3], "ready",
                (ConcealCoinTemplate) config.Template, config, cluster, "prev");
        if(family == "Cryptonote")
            return new CryptonoteJob(new Miningcore.Blockchain.Cryptonote.DaemonResponses.GetBlockTemplateResponse
                { Blob = NativeBlob, Difficulty = 1000, Height = 101, ReservedOffset = offset }, new byte[3], "ready",
                (CryptonoteCoinTemplate) config.Template, config, cluster, "prev", "fixture");
        var job = Substitute.For<ZanoJob>(new Miningcore.Blockchain.Zano.DaemonResponses.GetBlockTemplateResponse
            { Blob = NativeBlob, Difficulty = 1000, Height = 101, SeedHash = new string('0', 64) }, new byte[3],
            (ZanoCoinTemplate) config.Template, config, cluster, "prev", Substitute.For<IProgpowCache>());
        job.PrepareWorkerJob(Arg.Any<double>()).Returns(c => new ZanoWorkerJob("aabb", c.Arg<double>())
            { DifficultyTemplate = job, ExtraNonce = 1, Height = "0x00000065", SeedHash = new string('0', 64), Target = new string('f', 64) });
        return job;
    }

    // Known-valid 330-byte CryptoNote blob, shared with the native conversion KAT.
    private const string NativeBlob = "0106e5b3afd505583cf50bcc743d04d831d2b119dc94ad88679e359076ee3f18d258ee138b3b421c0300a401d90101ff9d0106d6d6a88702023c62e43372a58cb588147e20be53a27083f5c522f33c722b082ab7518c48cda280b4c4c32102609ec96e2499ee267d70efefc49f26e330526d3ef455314b7b5ba268a6045f8c80c0fc82aa0202fe5cc0fa56c4277d1a47827edce4725571529d57f33c73ada481ef84c323f30a8090cad2c60e02d88bf5e72a611c8b8464ce29e3b1adbfe1ae163886d9150fe511171cada98fcb80e08d84ddcb0102441915aaf9fbaf70ff454c701a6ae2bd59bb94dc0b888bf7e5d06274ee9238ca80c0caf384a302024078526e2132def44bde2806242652f5944e632f7d94290dd6ee5dda1929f5ee2b016e29f25f07ec2a8df59f0e118a6c9a4b769b745dc0c729071f6e0399d2585745020800000000012e7f7600";

    private static MethodInfo FindMethod(Type type, string name) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing fixture method {type.Name}.{name}");
    private static object Invoke(object target, string name, params object[] args) => FindMethod(target.GetType(), name).Invoke(target, args);
    private static void SetProperty(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(property != null, $"Missing required fixture property {target.GetType().Name}.{name}");
        property.SetValue(target, value);
    }
    private static void SetField(object target, string name, object value)
    {
        for(var type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if(field == null) continue;
            field.SetValue(target, value);
            return;
        }
        throw new InvalidOperationException($"Missing required fixture field {target.GetType().Name}.{name}");
    }

    private sealed class RestStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0}") });
    }
    private sealed class DaemonStub : IAsyncDisposable
    {
        internal int Calls;
        internal string XelisAlgorithm = "xel/v1";
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task serve;
        internal int Port => ((IPEndPoint) listener.LocalEndpoint).Port;
        internal DaemonStub() { listener.Start(); serve = Serve(); }
        private async Task Serve()
        {
            try
            {
                while(!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    var stream = client.GetStream();
                    using var reader = new System.IO.StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var length = 0;
                    while(await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                        if(line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..]);
                    var data = new char[length];
                    await reader.ReadBlockAsync(data, stop.Token);
                    Interlocked.Increment(ref Calls);
                    var id = JObject.Parse(new string(data))["id"];
                    var response = Encoding.UTF8.GetBytes(new JObject { ["id"] = id, ["result"] = new JObject
                        { ["isvalid"] = true, ["is_valid"] = true, ["type"] = "regular", ["address"] = "fixture",
                          ["difficulty"] = "100", ["algorithm"] = XelisAlgorithm, ["topoheight"] = 1,
                          ["template"] = new string('0', 256), ["miner_work"] = new string('a', 224) }, ["error"] = null }.ToString(Formatting.None));
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, stop.Token);
                    await stream.WriteAsync(response, stop.Token);
                }
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await serve.WaitAsync(Timeout); stop.Dispose(); }
    }
}
