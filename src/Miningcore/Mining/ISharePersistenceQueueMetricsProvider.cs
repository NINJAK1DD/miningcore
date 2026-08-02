namespace Miningcore.Mining;

public interface ISharePersistenceQueueMetricsProvider
{
    int PersistenceQueueDepth { get; }
    int PersistenceQueueHighWatermark { get; }
    int PersistenceQueueCapacity { get; }
    int EmergencyJournalQueueDepth { get; }
    int EmergencyJournalQueueHighWatermark { get; }
    int EmergencyJournalQueueCapacity { get; }
}
