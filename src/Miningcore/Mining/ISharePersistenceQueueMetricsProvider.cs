namespace Miningcore.Mining;

public readonly record struct SharePersistenceQueueMetricsSnapshot(
    int Depth, int HighWatermark, int Capacity, long OverflowCount);

public interface ISharePersistenceQueueMetricsProvider
{
    SharePersistenceQueueMetricsSnapshot GetPersistenceQueueMetrics();
    SharePersistenceQueueMetricsSnapshot GetEmergencyJournalQueueMetrics();
}
