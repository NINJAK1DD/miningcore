using Miningcore.Persistence.Model;

namespace Miningcore.Payments;

internal sealed class PayoutReconciliationTracker
{
    public PayoutReconciliationTracker(IEnumerable<Balance> balances)
    {
        entries = balances.Select(x => new TrackedEntry(x)).ToArray();
    }

    private readonly object gate = new();
    private readonly TrackedEntry[] entries;
    private readonly List<Action> notifications = new();

    public bool HasInFlight
    {
        get
        {
            lock(gate)
                return entries.Any(x => x.State == PayoutState.Attempting);
        }
    }

    public void MarkAttempting(IEnumerable<Balance> balances) =>
        Update(balances, entry =>
        {
            if(entry.State == PayoutState.None)
                entry.State = PayoutState.Attempting;
        });

    public void MarkNotInFlight(IEnumerable<Balance> balances) =>
        Update(balances, entry =>
        {
            if(entry.State == PayoutState.Attempting &&
                string.IsNullOrWhiteSpace(entry.TransactionId))
                entry.State = PayoutState.None;
        });

    public void MarkAccepted(IEnumerable<Balance> balances, string transactionId) =>
        Update(balances, entry =>
        {
            entry.State = PayoutState.Accepted;
            entry.TransactionId = transactionId;
            entry.Detail = null;
        });

    public void MarkSubmitted(IEnumerable<Balance> balances, string transactionId)
    {
        lock(gate)
        {
            var submittedEntries = (balances ?? Array.Empty<Balance>())
                .Select(Find)
                .Where(x => x != null)
                .Distinct()
                .ToArray();

            if(submittedEntries.Length == 0)
                return;

            var duplicate = entries.Any(x => !submittedEntries.Contains(x) &&
                !string.IsNullOrWhiteSpace(x.TransactionId) &&
                string.Equals(x.TransactionId, transactionId, StringComparison.Ordinal));

            if(duplicate)
            {
                foreach(var entry in submittedEntries)
                {
                    entry.State = PayoutState.Attempting;
                    entry.TransactionId = transactionId;
                }

                throw new PayoutOutcomeUncertainException(
                    $"Separate wallet submissions returned duplicate transaction id {transactionId}");
            }

            foreach(var entry in submittedEntries)
            {
                entry.State = PayoutState.Attempting;
                entry.TransactionId = transactionId;
            }
        }
    }

    public void MarkFailed(IEnumerable<Balance> balances, string detail) =>
        Update(balances, entry =>
        {
            if(entry.State != PayoutState.Accepted)
            {
                entry.State = PayoutState.Failed;
                entry.Detail = detail;
            }
        });

    public void EnqueueNotification(Action notification)
    {
        lock(gate)
            notifications.Add(notification);
    }

    public void FlushNotifications(Action<Exception> onError)
    {
        Action[] pending;

        lock(gate)
        {
            pending = notifications.ToArray();
            notifications.Clear();
        }

        foreach(var notification in pending)
        {
            try
            {
                notification();
            }
            catch(Exception ex)
            {
                onError(ex);
            }
        }
    }

    public PayoutOutcomeUncertainException AttachReconciliation(
        PayoutOutcomeUncertainException exception)
    {
        lock(gate)
        {
            Apply(exception.Reconciliation);

            foreach(var entry in entries.Where(x => x.State == PayoutState.Attempting))
            {
                entry.State = PayoutState.Uncertain;
                entry.Detail ??= exception.Message;
            }

            var reconciliation = new PayoutReconciliation
            {
                Accepted = Select(PayoutState.Accepted),
                Failed = Select(PayoutState.Failed),
                Uncertain = Select(PayoutState.Uncertain),
                NotAttempted = entries
                    .Where(x => x.State == PayoutState.None ||
                        x.State == PayoutState.NotAttempted)
                    .Select(x => x.ToReconciliationEntry(
                        "Wallet submission was not started before payout processing stopped"))
                    .ToArray(),
            };

            return new PayoutOutcomeUncertainException(exception.Message,
                exception.InnerException, reconciliation);
        }
    }

    private PayoutReconciliationEntry[] Select(PayoutState state) => entries
        .Where(x => x.State == state)
        .Select(x => x.ToReconciliationEntry())
        .ToArray();

    private void Apply(PayoutReconciliation reconciliation)
    {
        if(reconciliation == null)
            return;

        Apply(reconciliation.Accepted, PayoutState.Accepted);
        Apply(reconciliation.Failed, PayoutState.Failed);
        Apply(reconciliation.Uncertain, PayoutState.Uncertain);
        Apply(reconciliation.NotAttempted, PayoutState.NotAttempted);
    }

    private void Apply(IEnumerable<PayoutReconciliationEntry> source, PayoutState state)
    {
        foreach(var sourceEntry in source ?? Array.Empty<PayoutReconciliationEntry>())
        {
            var entry = Find(sourceEntry.Address);

            if(entry == null)
                continue;

            entry.State = state;
            entry.Amount = sourceEntry.Amount;
            entry.SubmittedAmount = sourceEntry.SubmittedAmount;
            entry.TransactionId = sourceEntry.TransactionId;
            entry.Detail = sourceEntry.Detail;
        }
    }

    private void Update(IEnumerable<Balance> balances, Action<TrackedEntry> update)
    {
        lock(gate)
        {
            foreach(var balance in balances ?? Array.Empty<Balance>())
            {
                var entry = Find(balance);

                if(entry != null)
                    update(entry);
            }
        }
    }

    private TrackedEntry Find(Balance balance) =>
        entries.FirstOrDefault(x => ReferenceEquals(x.Balance, balance)) ??
        Find(balance.Address);

    private TrackedEntry Find(string address) => entries.FirstOrDefault(x =>
        string.Equals(x.Balance.Address, address, StringComparison.Ordinal));

    private enum PayoutState
    {
        None,
        Attempting,
        Accepted,
        Failed,
        Uncertain,
        NotAttempted,
    }

    private sealed class TrackedEntry
    {
        public TrackedEntry(Balance balance)
        {
            Balance = balance;
            Amount = balance.Amount;
        }

        public Balance Balance { get; }
        public decimal Amount { get; set; }
        public decimal? SubmittedAmount { get; set; }
        public string TransactionId { get; set; }
        public string Detail { get; set; }
        public PayoutState State { get; set; }

        public PayoutReconciliationEntry ToReconciliationEntry(string defaultDetail = null) =>
            new()
            {
                Address = Balance.Address,
                Amount = Amount,
                SubmittedAmount = SubmittedAmount,
                TransactionId = TransactionId,
                Detail = Detail ?? defaultDetail,
            };
    }
}
