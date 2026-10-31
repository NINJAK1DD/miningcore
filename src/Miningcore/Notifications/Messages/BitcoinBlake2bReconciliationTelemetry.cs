namespace Miningcore.Notifications.Messages;

// A scan visit is not proof of recovery or a financial settlement. All values
// describe one bounded batch; global backlog counts belong to the operator audit.
internal record BitcoinBlake2bReconciliationTelemetry(string PoolId, int Rows,
    long Cursor, long MinimumHeight, ulong OldestBatchHeight, int NearHorizonRows, bool SweepCompleted);
