using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Autofac;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Util;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain;

public abstract class JobManagerBase<TJob>
{
    protected JobManagerBase(IComponentContext ctx, IMessageBus messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(messageBus);

        this.ctx = ctx;
        this.messageBus = messageBus;
    }

    protected readonly IComponentContext ctx;
    protected readonly IMessageBus messageBus;
    protected ClusterConfig clusterConfig;

    protected TJob currentJob;
    internal double CandidateDifficultyLimit => currentJob == null ? double.MaxValue : CandidateShareCredit.Limit(currentJob);
    private int jobId;
    protected readonly object jobLock = new();
    protected ILogger logger;
    protected PoolConfig poolConfig;
    protected bool hasInitialBlockTemplate = false;
    protected readonly Subject<Unit> blockFoundSubject = new();

    protected abstract void ConfigureDaemons();

    protected async Task StartDaemonAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        while(!await AreDaemonsHealthyAsync(ct))
        {
            logger.Info(() => "Waiting for daemons to come online ...");

            await timer.WaitForNextTickAsync(ct);
        }

        logger.Info(() => "All daemons online");

        while(!await AreDaemonsConnectedAsync(ct))
        {
            logger.Info(() => "Waiting for daemon to connect to peers ...");

            await timer.WaitForNextTickAsync(ct);
        }
    }

    protected string NextJobId(string format = null)
    {
        int previous, value;
        do
        {
            previous = Volatile.Read(ref jobId);
            // IDs identify bounded recent job registries, not lifetime history.
            // Stay positive and keep native formats without exhausting a long-lived pool.
            value = previous == int.MaxValue ? 1 : previous + 1;
        } while(Interlocked.CompareExchange(ref jobId, value, previous) != previous);

        if(format != null)
            return value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

        return value.ToStringHex8();
    }

    internal string NextWorkerJobId(string format = null) => NextJobId(format);

    protected IObservable<string> BtStreamSubscribe(ZmqPubSubEndpointConfig config)
    {
        return messageBus.Listen<BtStreamMessage>()
            .Where(x => x.Topic == config.Topic)
            .SafeDo(x => messageBus.SendMessage(new TelemetryEvent(poolConfig.Id, TelemetryCategory.BtStream, x.Received - x.Sent)), logger)
            .Select(x => x.Payload);
    }

    protected void OnBlockFound()
    {
        blockFoundSubject.OnNext(Unit.Default);
    }

    protected abstract Task<bool> AreDaemonsHealthyAsync(CancellationToken ct);
    protected abstract Task<bool> AreDaemonsConnectedAsync(CancellationToken ct);
    protected abstract Task EnsureDaemonsSynchedAsync(CancellationToken ct);
    protected abstract Task PostStartInitAsync(CancellationToken ct);
    public abstract TJob GetJobForStratum();

    #region API-Surface

    public virtual void Configure(PoolConfig pc, ClusterConfig cc)
    {
        Contract.RequiresNonNull(pc);
        Contract.RequiresNonNull(cc);

        logger = LogUtil.GetPoolScopedLogger(typeof(JobManagerBase<TJob>), pc);
        poolConfig = pc;
        clusterConfig = cc;

        ConfigureDaemons();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);

        logger.Info(() => "Starting Job Manager ...");

        try
        {
            await StartDaemonAsync(ct);
            await EnsureDaemonsSynchedAsync(ct);
            await PostStartInitAsync(ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch(TrustedPoolStartupException)
        {
            throw;
        }
        catch(Exception ex)
        {
            Rpc.RpcConsumerDiagnostics.Write(logger, LogLevel.Error,
                "JobManagerBase.StartAsync", failure: ex);
            // Host startup also logs exceptions. Keep the original available to
            // in-process inspection, but not as InnerException/ToString output.
            throw new Rpc.RpcConsumerStartupException(poolConfig.Id, ex);
        }

        logger.Info(() => "Job Manager Online");
    }

    #endregion // API-Surface
}
