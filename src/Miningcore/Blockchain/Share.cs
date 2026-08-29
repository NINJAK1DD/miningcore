using ProtoBuf;

namespace Miningcore.Blockchain;

public enum ShareAccountingRole
{
    None = 0,
    Single = 1,
    Parent = 2,
    Auxiliary = 3,
}

[ProtoContract]
public class Share
{
    private Task persistenceAdmission = Task.CompletedTask;

    /// <summary>
    /// Runtime-only completion used by the local Stratum admission path when a saturated
    /// persistence queue diverts this share to the bounded emergency journal writer.
    /// </summary>
    [ProtoIgnore]
    internal Task PersistenceAdmission => Volatile.Read(ref persistenceAdmission);

    internal void SetPersistenceAdmission(Task completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        Volatile.Write(ref persistenceAdmission, completion);
    }

    /// <summary>
    /// The pool originating this share from
    /// </summary>
    [ProtoMember(1)]
    public string PoolId { get; set; }

    /// <summary>
    /// Who mined it (wallet address)
    /// </summary>
    [ProtoMember(2)]
    public string Miner { get; set; }

    /// <summary>
    /// Who mined it
    /// </summary>
    [ProtoMember(3)]
    public string Worker { get; set; }

    /// <summary>
    /// Mining Software
    /// </summary>
    [ProtoMember(5)]
    public string UserAgent { get; set; }

    /// <summary>
    /// From where was it submitted
    /// </summary>
    [ProtoMember(6)]
    public string IpAddress { get; set; }

    /// <summary>
    /// Submission source (pool, external stratum etc)
    /// </summary>
    [ProtoMember(7)]
    public string Source { get; set; }

    /// <summary>
    /// Stratum difficulty assigned to the miner at the time the share was submitted/accepted (used for payout
    /// calculations)
    /// </summary>
    [ProtoMember(8)]
    public double Difficulty { get; set; }

    /// <summary>
    /// Miner-style achieved share difficulty
    /// </summary>
    [ProtoMember(17)]
    public double ShareDifficulty { get; set; }

    /// <summary>
    /// Raw achieved share difficulty, directly comparable to networkdifficulty
    /// </summary>
    [ProtoMember(18)]
    public double ActualDifficulty { get; set; }

    /// <summary>
    /// Logical mining session identifier
    /// </summary>
    [ProtoMember(19)]
    public string SessionId { get; set; }

    /// <summary>
    /// Persist this message as a block candidate without inserting it into the shares table.
    /// Used for independently durable merged-mining block records that must not create a second
    /// standalone share row.
    /// </summary>
    [ProtoMember(20)]
    public bool BlockOnly { get; set; }

    /// <summary>
    /// The block record was emitted independently as a block-only message. The original share is
    /// still persisted for statistics, but must not create a duplicate block row.
    /// </summary>
    [ProtoMember(21)]
    public bool BlockRecordEmitted { get; set; }

    /// <summary>
    /// Preserve the sender timestamp when this ordinary share crosses a relay. Merged mining
    /// publishes the statistical proof before its independent block-submission paths so the
    /// winning proof remains on the correct effort boundary even if a peer chain is slow.
    /// </summary>
    [ProtoMember(22)]
    public bool PreserveCreated { get; set; }

    /// <summary>
    /// Stable, lowercase UUID (without separators) for exactly-once financial accounting.
    /// A merged-mining proof uses the same identifier for both chain projections.
    /// </summary>
    [ProtoMember(23)]
    public string AccountingId { get; set; }

    [ProtoMember(24)]
    public ShareAccountingRole AccountingRole { get; set; }

    /// <summary>
    /// Chain-specific amount available to the pool before configured reward-recipient
    /// percentages, expressed in satoshis. Integer wire representation avoids floating-point
    /// changes between a submitting node, relay receiver and recovery import.
    /// </summary>
    [ProtoMember(25)]
    public long RewardBasisSatoshis { get; set; }

    /// <summary>
    /// Auxiliary-chain projection of the same accepted proof. Only a parent projection may
    /// contain this member; nesting beyond one level is rejected by the recorder and receiver.
    /// </summary>
    [ProtoMember(26)]
    public Share PairedShare { get; set; }

    /// <summary>
    /// Runtime-only guard used when a job manager has already published the ordinary statistical
    /// copy. This is deliberately not serialized on the relay wire.
    /// </summary>
    [ProtoIgnore]
    public bool StatisticalRecordEmitted { get; set; }

    /// <summary>
    /// Block this share refers to
    /// </summary>
    [ProtoMember(9)]
    public long BlockHeight { get; set; }

    /// <summary>
    /// Block reward after deducting pool fee and donations
    /// </summary>
    public decimal BlockReward { get; set; }

    /// <summary>
    /// Block reward after deducting pool fee and donations
    /// </summary>
    [ProtoMember(10)]
    public double BlockRewardDouble { get; set; }

    /// <summary>
    /// Block hash
    /// </summary>
    [ProtoMember(11)]
    public string BlockHash { get; set; }

    /// <summary>
    /// Block hash
    /// </summary>
    [ProtoMember(16)]
    public string BlockType { get; set; }

    /// <summary>
    /// If this share presumably resulted in a block
    /// </summary>
    [ProtoMember(12)]
    public bool IsBlockCandidate { get; set; }

    /// <summary>
    /// Arbitrary data to be interpreted by the payment processor specialized
    /// in this coin to verify this block candidate was accepted by the network
    /// </summary>
    [ProtoMember(13)]
    public string TransactionConfirmationData { get; set; }

    /// <summary>
    /// Network difficulty at the time the share was submitted (used for some payout schemes like PPLNS)
    /// </summary>
    [ProtoMember(14)]
    public double NetworkDifficulty { get; set; }

    /// <summary>
    /// When the share was found
    /// </summary>
    [ProtoMember(15)]
    public DateTime Created { get; set; }
}
