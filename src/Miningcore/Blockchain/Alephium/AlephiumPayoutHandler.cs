using System;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Alephium.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using Block = Miningcore.Persistence.Model.Block;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Alephium;

[CoinFamily(CoinFamily.Alephium)]
public class AlephiumPayoutHandler : PayoutHandlerBase,
    IPayoutHandler
{
    public AlephiumPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus) :
        base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);

        this.ctx = ctx;
    }

    protected readonly IComponentContext ctx;
    protected AlephiumClient alephiumClient;
    private string network;
    private AlephiumPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;

    protected override string LogCategory => "Alephium Payout Handler";
    
    #region IPayoutHandler
    
    public virtual async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<AlephiumPaymentProcessingConfigExtra>();
        
        logger = LogUtil.GetPoolScopedLogger(typeof(AlephiumPayoutHandler), pc);

        alephiumClient = AlephiumClientFactory.CreateClient(pc, cc, null);
        
        var infosChainParams = await Guard(() => alephiumClient.GetInfosChainParamsAsync(ct),
            ex=> ReportAndRethrowApiError("Failed to get key params", ex));
        
        switch(infosChainParams?.NetworkId)
        {
            case 0:
                network = "mainnet";
                break;
            case 1:
            case 7:
                network = "testnet";
                break;
            case 4:
                network = "devnet";
                break;
            default:
                throw new PaymentException($"Unsupport network type '{infosChainParams?.NetworkId}'");
        }
    }
    
    public virtual async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        if(blocks.Length == 0)
            return blocks;

        var coin = poolConfig.Template.As<AlephiumCoinTemplate>();
        var pageSize = 100;
        var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
        var result = new List<Block>();
        
        for(var i = 0; i < pageCount; i++)
        {
            // get a page full of blocks
            var page = blocks
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();
            
            for(var j = 0; j < page.Length; j++)
            {
                var block = page[j];

                Settlement blockRewardTransaction;
                FixedAssetOutput blockReward;
                BlockEntry blockInfo;
                // First output is always the mainchain reward FixedOutputs[0] and [1] and [2] would be uncles (if exist)
                int blockRewardTransactionIndex = 0;

                var isBlockInMainChain = await Guard(() => alephiumClient.GetBlockflowIsBlockInMainChainAsync((string) block.Hash, ct),
                    ex=> logger.Debug(ex));

                // Starting with Rhone-Upgrade - https://docs.alephium.org/integration/mining/#rhone-upgrade - "Ghost" uncles are now a thing on ALPH
                // When a Block is not found in main chain, we must check now if it could be a "ghost" uncle
                if(!isBlockInMainChain)
                {
                    block.Type = AlephiumConstants.BlockTypeUncle;

                    // get uncle block info
                    blockInfo = await Guard(() => alephiumClient.UncleHashAsync((string) block.Hash, ct),
                        ex=> logger.Debug(ex));

                    // Dang, not even a "ghost" uncle, we definitely lost that battle :'(
                    if(blockInfo == null)
                    {
                        result.Add(block);

                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;

                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] classified as orphaned because it's not the chain and not even a 'ghost' uncle");

                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                        
                        continue;
                    }
                    else
                    {
                        logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] is a possible ghost uncle. It contains {blockInfo.Transactions.Count} transaction(s)");

                        // we only need the transaction(s) related to the block reward
                        blockRewardTransaction = blockInfo.Transactions
                            .Where(x => x.Unsigned.Inputs.Count < 1)
                            .LastOrDefault();

                        // Get the index of our uncle (by HASH value) - either 0 or 1 (2 max uncles)
                        var ghostUncleIndex = blockInfo.GhostUncles
                            .ToList()
                            .FindIndex(u => u.BlockHash == block.Hash);

                        // Advance index pointer +1 due to mainchain reward
                        blockRewardTransactionIndex = ghostUncleIndex + 1;
                    }
                }
                else
                {
                    block.Type = AlephiumConstants.BlockTypeBlock;

                    // get block info
                    blockInfo = await Guard(() => alephiumClient.HashAsync((string) block.Hash, ct),
                        ex=> logger.Debug(ex));

                    logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] contains {blockInfo.Transactions.Count} transaction(s)");

                    // we only need the transaction(s) related to the block reward
                    blockRewardTransaction = blockInfo.Transactions
                        .Where(x => x.Unsigned.Inputs.Count < 1)
                        .LastOrDefault();
                }

                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] contains {(blockRewardTransaction == null ? 0 : blockRewardTransaction.Unsigned.FixedOutputs.Count)} transaction(s) related to the block reward");

                // Money time
                if(blockRewardTransaction != null)
                {
                    // get wallet miner's addresses
                    var walletMinersAddresses = await Guard(() => alephiumClient.GetMinersAddressesAsync(ct),
                        ex=> logger.Debug(ex));

                    // We only need the transaction related to our block type
                    blockReward = blockRewardTransaction.Unsigned.FixedOutputs.ElementAtOrDefault(blockRewardTransactionIndex);
                    // We only need the transaction which rewards one of our wallet miner's addresses
                    if(!walletMinersAddresses.Addresses.Contains(blockReward.Address))
                        blockReward = null;

                    logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] contains {(blockReward == null ? 0 : 1)} transaction related to our wallet miner's addresses");

                    if(blockReward != null)
                    {
                        // update progress
                        // Two block confirmations methods are available:
                        // 1) ALPH default lock mechanism: All of the mined coins are locked for N minutes (up to +8 hours on mainnet, very short on testnet)
                        // 2) Mining pool operator provides a custom block rewards lock time, this method must be ONLY USE ON TESTNET in order to mimic MAINNET
                        if(extraPoolPaymentProcessingConfig?.BlockRewardsLockTime == null)
                        {
                            logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] uses the default block reward lock mechanism for minimum confirmations calculation");

                            decimal transactionsLockTime = (decimal) blockReward.LockTime;

                            block.ConfirmationProgress = Math.Min(1.0d, (double) ((AlephiumUtils.UnixTimeStampForApi(clock.Now) - blockInfo.Timestamp) / (transactionsLockTime - blockInfo.Timestamp)));
                        }
                        else
                        {
                            logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] uses a custom [{network}] block rewards lock time: [{extraPoolPaymentProcessingConfig?.BlockRewardsLockTime}] minute(s)");

                            block.ConfirmationProgress = Math.Min(1.0d, (double) ((AlephiumUtils.UnixTimeStampForApi(clock.Now) - blockInfo.Timestamp) / ((decimal) extraPoolPaymentProcessingConfig?.BlockRewardsLockTime * 60 * 1000)));
                        }

                        result.Add(block);

                        messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);

                        // matured and spendable?
                        if(block.ConfirmationProgress >= 1)
                        {
                            block.Status = BlockStatus.Confirmed;
                            block.ConfirmationProgress = 1;

                            // reset block reward
                            block.Reward = 0;

                            block.Reward = AlephiumUtils.ConvertNumberFromApi(blockReward.AttoAlphAmount) / AlephiumConstants.SmallestUnit;

                            logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} [{block.Hash}] worth {FormatAmount(block.Reward)}");
                            messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                        }

                        continue;
                    }
                }

                // If we end here that only means that we definitely lost that battle :'(
                result.Add(block);

                block.Status = BlockStatus.Orphaned;
                block.Reward = 0;

                logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] classified as orphaned because it's not the chain");

                messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
            }
        }

        return result.ToArray();
    }
    
    public virtual async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        try
        {
            await TrackPayoutAsync(balances, () => PayoutTrackedAsync(balances, ct));
        }
        finally
        {
            // Relocking is wallet-security cleanup, not part of the financial result. Use a
            // fresh bounded token so shutdown cancellation cannot skip it or replace that result.
            await RelockPayoutWalletSafelyAsync(LockWallet);
        }
    }

    protected virtual async Task PayoutTrackedAsync(Balance[] balances, CancellationToken ct)
    {
        var infosChainParams = await Guard(() => alephiumClient.GetInfosChainParamsAsync(ct));

        var info = await Guard(() => alephiumClient.GetInfosInterCliquePeerInfoAsync(ct));
        
        if(infosChainParams?.NetworkId != 7)
        {
            if(info?.Count < 1)
            {
                logger.Warn(() => $"[{LogCategory}] Payout aborted. Not enough peer(s)");
                return;
            }
        }

        // build args
        var amounts = balances
            .Where(x => x.Amount > 0)
            .ToDictionary(x => x.Address, x => x.Amount);

        if(amounts.Count == 0)
            return;

        var balancesTotal = amounts.Sum(x => x.Value);
        
        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses");
        
        // ALPH only allows multiple recipients per transaction if they belong to the same group
        // So we must validate the addresses and consolidate them together in their respective group
        var groupingAmounts = new List<KeyValuePair<string, decimal>>[]
        {
            new List<KeyValuePair<string, decimal>>(),
            new List<KeyValuePair<string, decimal>>(),
            new List<KeyValuePair<string, decimal>>(),
            new List<KeyValuePair<string, decimal>>(),
        };
        
        logger.Info(() => $"[{LogCategory}] Validating addresses...");
        foreach(var pair in amounts)
        {
            logger.Debug(() => $"[{LogCategory}] Address {pair.Key} with amount [{FormatAmount(pair.Value)}]");
            var validity = await Guard(() => alephiumClient.GetAddressesAddressGroupAsync(pair.Key, ct));

            if(validity == null || !(validity?.Group1 >= 0))
                logger.Warn(()=> $"[{LogCategory}] Address {pair.Key} is not valid!");
            else
            {
                logger.Debug(() => $"[{LogCategory}] Address {pair.Key} belongs to group [{validity.Group1}]");
                groupingAmounts[validity.Group1].Add(pair);
            }
        }
        
        retry:
            // get wallet status
            var status = await alephiumClient.NameAsync(extraPoolPaymentProcessingConfig.WalletName, ct);

            // unlock wallet
            if(status.Locked)
                await UnlockWallet(ct);

            // get wallet addresses
            var walletAddresses = await alephiumClient.NameAddressesAsync(extraPoolPaymentProcessingConfig.WalletName, ct);
            if (walletAddresses?.Addresses1.Count < 4)
            {
                logger.Warn(() => $"[{LogCategory}] Pool payment wallet name: {extraPoolPaymentProcessingConfig.WalletName} must have 4 miner's addresses. Please fix it");
                return;
            }

            // get balance
            var walletBalances = await alephiumClient.NameBalancesAsync(extraPoolPaymentProcessingConfig.WalletName, ct);
            var walletBalanceTotal = AlephiumUtils.ConvertNumberFromApi(walletBalances.TotalBalance) / AlephiumConstants.SmallestUnit;
            var walletBalanceLocked = walletBalances.Balances1
                .Sum(x => (AlephiumUtils.ConvertNumberFromApi(x.LockedBalance) / AlephiumConstants.SmallestUnit));
            var walletBalanceAvailable = walletBalanceTotal - walletBalanceLocked;

            logger.Info(() => $"[{LogCategory}] Current wallet balance - Total: [{FormatAmount(walletBalanceTotal)}] - Locked: [{FormatAmount(walletBalanceLocked)}] - Available: [{FormatAmount(walletBalanceAvailable)}]");

            // bail if balance does not satisfy payments
            if(walletBalanceAvailable < balancesTotal)
            {
                logger.Warn(() => $"[{LogCategory}] Wallet balance currently short of {FormatAmount(balancesTotal - walletBalanceAvailable)}. Will try again");
                return;
            }

            // check if any pool address has enough funds to cover the transaction
            var anyPoolAddress = walletBalances.Balances1
                .Where(x => ((AlephiumUtils.ConvertNumberFromApi(x.Balance) / AlephiumConstants.SmallestUnit) - (AlephiumUtils.ConvertNumberFromApi(x.LockedBalance) / AlephiumConstants.SmallestUnit)) >= balancesTotal)
                .FirstOrDefault();
            // No pool wallet address can cover that transaction
            if(string.IsNullOrEmpty(anyPoolAddress?.Address))
            {
                logger.Warn(() => $"[{LogCategory}] No pool wallet address can cover that transaction");

                // we need to move funds between two addresses which hold the majority of coins, in order to allow payouts to resume
                var wealthyPoolAddress = walletBalances.Balances1
                    .OrderByDescending(x => (AlephiumUtils.ConvertNumberFromApi(x.Balance) / AlephiumConstants.SmallestUnit) - (AlephiumUtils.ConvertNumberFromApi(x.LockedBalance) / AlephiumConstants.SmallestUnit))
                    .Take(2)
                    .ToArray();

                // all available funds have already been moved to one address
                if(((AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[1].Balance) / AlephiumConstants.SmallestUnit) - (AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[1].LockedBalance) / AlephiumConstants.SmallestUnit)) <= 0)
                {
                    logger.Info(() => $"[{LogCategory}] All available funds have already been moved to pool wallet address {wealthyPoolAddress[0].Address} [{FormatAmount(AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[0].Balance) / AlephiumConstants.SmallestUnit)}]");
                    return;
                }

                logger.Info(() => $"[{LogCategory}] We will now move the funds from pool wallet address {wealthyPoolAddress[1].Address} - Total: [{FormatAmount(AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[1].Balance) / AlephiumConstants.SmallestUnit)}] - Locked: [{FormatAmount(AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[1].LockedBalance) / AlephiumConstants.SmallestUnit)}] - Available: [{FormatAmount((AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[1].Balance) / AlephiumConstants.SmallestUnit) - (AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[1].LockedBalance) / AlephiumConstants.SmallestUnit))}] - to pool wallet address {wealthyPoolAddress[0].Address} - Total: [{FormatAmount(AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[0].Balance) / AlephiumConstants.SmallestUnit)}] - Locked: [{FormatAmount(AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[0].LockedBalance) / AlephiumConstants.SmallestUnit)}] - Available: [{FormatAmount((AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[0].Balance) / AlephiumConstants.SmallestUnit) - (AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[0].LockedBalance) / AlephiumConstants.SmallestUnit))}]");

                var bodyChangeWealthyActiveAddress = new ChangeActiveAddress
                {
                    Address = wealthyPoolAddress[1].Address,
                };

                await Guard(() => alephiumClient.NameChangeActiveAddressAsync(extraPoolPaymentProcessingConfig.WalletName, bodyChangeWealthyActiveAddress, ct), ex =>
                {
                    logger.Warn(() => $"[{LogCategory}] Change active address failed");
                });

                logger.Debug(() => $"[{LogCategory}] Pool wallet address {wealthyPoolAddress[1].Address} is now the active address");

                // get address UTXOs
                var wealthyPoolAddressUtxos = await alephiumClient.GetAddressesAddressUtxosAsync(wealthyPoolAddress[1].Address, ct);
                if(!string.IsNullOrEmpty(wealthyPoolAddressUtxos?.Warning))
                {
                    logger.Warn(() => $"[{LogCategory}] Pool wallet address: {wealthyPoolAddress[1].Address} maybe can't be used anymore: {wealthyPoolAddressUtxos.Warning}. Please fix it");
                    return;
                }

                logger.Debug(() => $"[{LogCategory}] Pool wallet address {wealthyPoolAddress[1].Address} has currently {wealthyPoolAddressUtxos.Utxos.Count} (locked+unlocked) UTXO(s)");
                logger.Debug(() => $"[{LogCategory}] Current Epoch Unix Timestamp {AlephiumUtils.UnixTimeStampForApi(clock.Now)} ms");

                // create UTXOs batch
                var inputWealthyUtxos = wealthyPoolAddressUtxos.Utxos
                    .Where(x => x.LockTime <= AlephiumUtils.UnixTimeStampForApi(clock.Now))
                    .Select(x => new OutputRef
                    {
                        Hint = x.Ref.Hint,
                        Key = x.Ref.Key,
                    })
                    .ToArray();

                logger.Debug(() => $"[{LogCategory}] Pool wallet address {wealthyPoolAddress[1].Address} has currently {inputWealthyUtxos.Length} (unlocked) UTXO(s)");

                Sweep destinationSweep;
                TransferResults txSweep;

                // calculate gas amount for transaction
                // ALPH Gas computation - https://wiki.alephium.org/integration/exchange#gas-computation
                var inputWealthyGas = AlephiumConstants.GasPerInput * inputWealthyUtxos.Length;
                var outputWealthyGas = AlephiumConstants.GasPerOutput;
                var wealthyTxGas = inputWealthyGas + outputWealthyGas + AlephiumConstants.TxBaseGas + AlephiumConstants.P2pkUnlockGas + AlephiumConstants.GasPerOutput;
                var wealthyEstimatedGasAmount = Math.Max(AlephiumConstants.MinGasPerTx, wealthyTxGas);
                if(wealthyEstimatedGasAmount > AlephiumConstants.MaxGasPerTx)
                {
                    // Rare case-scenario when we actually need to let Swagger do its magic with address(es) holding huge amount of UTXOs (probably lot of "dust" amounts)
                    logger.Warn(() => $"[{LogCategory}] Estimated necessary gas amount [{wealthyEstimatedGasAmount}] exceeds the maximum possible per transaction [{AlephiumConstants.MaxGasPerTx}]. We need to let Swagger operate its magic, we will only provide the destination address");
                    
                    destinationSweep = new Sweep
                    {
                        ToAddress = wealthyPoolAddress[0].Address,
                    };
                    
                    txSweep = await Guard(() => alephiumClient.NameSweepAllAddressesAsync(extraPoolPaymentProcessingConfig.WalletName, destinationSweep, ct), ex =>
                    {
                        RethrowSubmissionFailure("Alephium wallet sweep submission",
                            "Failed to Sweep all wealthy active addresses", ex);
                    });
                }
                else
                {
                    logger.Debug(() => $"[{LogCategory}] Estimated necessary gas amount: {wealthyEstimatedGasAmount}");

                    destinationSweep = new Sweep
                    {
                        ToAddress = wealthyPoolAddress[0].Address,
                        GasAmount = wealthyEstimatedGasAmount,
                    };

                    txSweep = await Guard(() => alephiumClient.NameSweepActiveAddressAsync(extraPoolPaymentProcessingConfig.WalletName, destinationSweep, ct), ex =>
                    {
                        RethrowSubmissionFailure("Alephium wallet sweep submission",
                            "Failed to Sweep wealthy active address", ex);
                    });
                }

                var sweepResults = ParseSweepResults(txSweep);
                foreach(var result in sweepResults)
                {
                    logger.Info(() => $"[{LogCategory}] Sweep transaction id: {result.TxId}, FromGroup: {result.FromGroup}, ToGroup: {result.ToGroup}");
                }

                goto retry;
            }

            // select the pool address which has enough funds to cover the transaction
            var inputAddress = walletAddresses.Addresses1
                .Where(x => x.Address == anyPoolAddress.Address)
                .FirstOrDefault();
            if(string.IsNullOrEmpty(inputAddress?.Address))
            {
                logger.Warn(() => $"[{LogCategory}] Pool wallet address {anyPoolAddress.Address} does not exist anymore. Please fix it");
                return;
            }

            logger.Info(() => $"[{LogCategory}] Pool wallet address {inputAddress.Address} has enough funds - Total: [{FormatAmount((AlephiumUtils.ConvertNumberFromApi(anyPoolAddress.Balance) / AlephiumConstants.SmallestUnit))}] - Locked: [{FormatAmount((AlephiumUtils.ConvertNumberFromApi(anyPoolAddress.LockedBalance) / AlephiumConstants.SmallestUnit))}] - Avalaible: [{FormatAmount((AlephiumUtils.ConvertNumberFromApi(anyPoolAddress.Balance) / AlephiumConstants.SmallestUnit) - (AlephiumUtils.ConvertNumberFromApi(anyPoolAddress.LockedBalance) / AlephiumConstants.SmallestUnit))}]");

            logger.Info(() => $"[{LogCategory}] Change active address");
            var bodyChangeActiveAddress = new ChangeActiveAddress
            {
                Address = inputAddress.Address,
            };

            await Guard(() => alephiumClient.NameChangeActiveAddressAsync(extraPoolPaymentProcessingConfig.WalletName, bodyChangeActiveAddress, ct), ex =>
            {
               logger.Warn(() => $"[{LogCategory}] Change active address failed");
            });

            logger.Debug(() => $"[{LogCategory}] Pool wallet address {inputAddress.Address} is now the active address");

            decimal groupTotalBalance;

            Terminus[] batchDestinations;
            OutputRef[] inputUtxos;

            int inputGas;
            int outputGas;
            int txGas;
            int estimatedGasAmount;
            int initialTotalAddresses;
            int numberOfAddressesToRemove;

            BuildSettlement destinationsTransaction;
            Sign signTxBuild;
            SubmitSettlement submitTxSign;

            // Processing groups of addresses
            for (var j = 0; j < groupingAmounts.Length; j++)
            {
                initialTotalAddresses = groupingAmounts[j].Count;
                // Only groups containing addresses are processing
                if(initialTotalAddresses > 0)
                {
                    var groupBalances = CreateGroupBalances(groupingAmounts[j]);
                    groupTotalBalance = groupingAmounts[j].Sum(x => x.Value);
                    logger.Info(() => $"[{LogCategory}] Processing group [{j}] containing {initialTotalAddresses} address(es), total amount [{FormatAmount(groupTotalBalance)}]");

                    logger.Info(() => $"[{LogCategory}] 1/3) Build the transaction");

                    // get address UTXOs
                    var (utxoLookupSucceeded, inputAddressUtxos) =
                        await TryPayoutPreparationAsync(groupBalances,
                            "Alephium address UTXO lookup", ct, () =>
                                alephiumClient.GetAddressesAddressUtxosAsync(
                                    inputAddress.Address, ct));
                    if(!utxoLookupSucceeded)
                        continue;

                    if(inputAddressUtxos?.Utxos == null)
                    {
                        RecordPayoutPreparationFailure(groupBalances,
                            "Alephium address UTXO lookup returned no UTXO collection");
                        continue;
                    }

                    if(!string.IsNullOrEmpty(inputAddressUtxos?.Warning))
                    {
                        RecordPayoutPreparationFailure(groupBalances,
                            $"Alephium address UTXO lookup returned a warning: " +
                            inputAddressUtxos.Warning);
                        continue;
                    }

                    logger.Debug(() => $"[{LogCategory}] Pool wallet address {inputAddress.Address} has currently {inputAddressUtxos.Utxos.Count} (locked+unlocked) UTXO(s)");
                    logger.Debug(() => $"[{LogCategory}] Current Epoch Unix Timestamp {AlephiumUtils.UnixTimeStampForApi(clock.Now)} ms");

                    // create UTXOs batch
                    inputUtxos = inputAddressUtxos.Utxos
                        .Where(x => x.LockTime <= AlephiumUtils.UnixTimeStampForApi(clock.Now))
                        .Select(x => new OutputRef
                        {
                            Hint = x.Ref.Hint,
                            Key = x.Ref.Key,
                        })
                        .ToArray();

                    logger.Debug(() => $"[{LogCategory}] Pool wallet address {inputAddress.Address} has currently {inputUtxos.Length} (unlocked) UTXO(s)");

                    // calculate gas amount for transaction
                    // ALPH Gas computation - https://wiki.alephium.org/integration/exchange#gas-computation
                    inputGas = AlephiumConstants.GasPerInput * inputUtxos.Length;
                    outputGas = AlephiumConstants.GasPerOutput * groupingAmounts[j].Count;
                    txGas = inputGas + outputGas + AlephiumConstants.TxBaseGas + AlephiumConstants.P2pkUnlockGas + AlephiumConstants.GasPerOutput;
                    estimatedGasAmount = Math.Max(AlephiumConstants.MinGasPerTx, txGas);
                    if(estimatedGasAmount > AlephiumConstants.MaxGasPerTx)
                    {
                        logger.Warn(() => $"[{LogCategory}] Estimated necessary gas amount [{estimatedGasAmount}] exceeds the maximum possible per transaction [{AlephiumConstants.MaxGasPerTx}]. We need to remove addresses.");

                        numberOfAddressesToRemove = (int)Math.Ceiling((decimal)(estimatedGasAmount - AlephiumConstants.MaxGasPerTx) / AlephiumConstants.GasPerOutput);
                        // No can do sir
                        if(numberOfAddressesToRemove >= initialTotalAddresses)
                        {
                            RecordPayoutPreparationFailure(groupBalances,
                                $"Alephium transaction requires removing " +
                                $"{numberOfAddressesToRemove} address(es), but the group " +
                                $"contains only {initialTotalAddresses}");
                            continue;
                        }

                        logger.Debug(() => $"[{LogCategory}] {numberOfAddressesToRemove} address(es) are removed");
                        // trim addresses
                        while(groupingAmounts[j].Count > (initialTotalAddresses - numberOfAddressesToRemove))
                            groupingAmounts[j].RemoveAt(groupingAmounts[j].Count - 1);

                        groupTotalBalance = groupingAmounts[j].Sum(x => x.Value);
                        logger.Info(() => $"[{LogCategory}] Group {j} containing now {groupingAmounts[j].Count} address(es), total amount [{FormatAmount(groupTotalBalance)}]");

                        estimatedGasAmount = AlephiumConstants.MaxGasPerTx;
                        groupBalances = CreateGroupBalances(groupingAmounts[j]);
                    }

                    logger.Debug(() => $"[{LogCategory}] Estimated necessary gas amount: {estimatedGasAmount} [{FormatAmount(((estimatedGasAmount * AlephiumConstants.DefaultGasPrice) / AlephiumConstants.SmallestUnit))}]");
                    if(extraPoolPaymentProcessingConfig?.KeepTransactionFees == true)
                        logger.Debug(() => $"[{LogCategory}] Pool does not pay the transaction fee, so each address will have its payout deducted with [{FormatAmount(((estimatedGasAmount * AlephiumConstants.DefaultGasPrice) / AlephiumConstants.SmallestUnit) / groupingAmounts[j].Count)}]");

                    // create destination batch
                    batchDestinations = groupingAmounts[j].Select(x => new Terminus
                    {
                        Address = x.Key,
                        AttoAlphAmount = AlephiumUtils.ConvertNumberForApi(((extraPoolPaymentProcessingConfig?.KeepTransactionFees == false) ? x.Value * AlephiumConstants.SmallestUnit : ((x.Value * AlephiumConstants.SmallestUnit) > ((estimatedGasAmount * AlephiumConstants.DefaultGasPrice) / groupingAmounts[j].Count) ? (x.Value * AlephiumConstants.SmallestUnit) - ((estimatedGasAmount * AlephiumConstants.DefaultGasPrice) / groupingAmounts[j].Count) : x.Value * AlephiumConstants.SmallestUnit))),
                    }).ToArray();

                    destinationsTransaction = new BuildSettlement
                    {
                        FromPublicKey = inputAddress.PublicKey,
                        Destinations = batchDestinations,
                        Utxos = inputUtxos,
                        GasAmount = estimatedGasAmount,
                    };

                    var (buildSucceeded, txBuild) = await TryPayoutPreparationAsync(
                        groupBalances, "Alephium transaction build", ct, () =>
                            alephiumClient.PostTransactionsBuildAsync(
                                destinationsTransaction, ct));
                    if(!buildSucceeded)
                        continue;

                    if(string.IsNullOrEmpty(txBuild?.TxId))
                    {
                        RecordPayoutPreparationFailure(groupBalances,
                            "Alephium transaction build returned no transaction id");
                        continue;
                    }

                    logger.Info(() => $"[{LogCategory}] Unsigned transaction {txBuild.UnsignedTx} with txId {txBuild.TxId}");

                    logger.Info(() => $"[{LogCategory}] 2/3) Sign the transaction");
                    signTxBuild = new Sign
                    {
                        Data = txBuild.TxId,
                    };

                    var (signSucceeded, txSign) = await TryPayoutPreparationAsync(
                        groupBalances, "Alephium transaction signing", ct, () =>
                            alephiumClient.NameSignAsync(
                                extraPoolPaymentProcessingConfig.WalletName,
                                signTxBuild, ct));
                    if(!signSucceeded)
                        continue;

                    if(string.IsNullOrEmpty(txSign?.Signature))
                    {
                        RecordPayoutPreparationFailure(groupBalances,
                            "Alephium transaction signing returned no signature");
                        continue;
                    }

                    logger.Info(() => $"[{LogCategory}] Unsigned transaction signature {txSign.Signature}");

                    logger.Info(() => $"[{LogCategory}] 3/3) Submit signed transaction to the network");
                    submitTxSign = new SubmitSettlement
                    {
                        UnsignedTx = txBuild.UnsignedTx,
                        Signature = txSign.Signature,
                    };

                    await SubmitPayoutGroupAsync(groupBalances, submitTxSign,
                        (estimatedGasAmount * AlephiumConstants.DefaultGasPrice) /
                        AlephiumConstants.SmallestUnit, ct);
                }
            }
        
    }

    private Balance[] CreateGroupBalances(
        IEnumerable<KeyValuePair<string, decimal>> amounts) => amounts
        .Select(x => new Balance
        {
            PoolId = poolConfig.Id,
            Address = x.Key,
            Amount = x.Value,
        })
        .ToArray();

    protected async Task<(bool Success, T Result)> TryPayoutPreparationAsync<T>(
        Balance[] balances, string operation, CancellationToken ct,
        Func<Task<T>> action)
    {
        try
        {
            return (true, await action());
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch(Exception ex)
        {
            RecordPayoutPreparationFailure(balances,
                $"{operation} failed before wallet submission: {GetApiError(ex)}", ex);
            return (false, default);
        }
    }

    private void RecordPayoutPreparationFailure(Balance[] balances, string detail,
        Exception exception = null)
    {
        logger.Warn(() => exception == null
            ? $"[{LogCategory}] {detail}"
            : $"[{LogCategory}] {detail}: {exception}");
        NotifyPayoutFailure(poolConfig.Id, balances, detail, exception);
    }

    internal static TransferResult[] ParseSweepResults(TransferResults sweep)
    {
        if(sweep?.Results == null || sweep.Results.Count == 0)
            throw new PayoutOutcomeUncertainException(
                "Alephium wallet sweep returned success without transaction identities");

        var results = sweep.Results.ToArray();
        for(var i = 0; i < results.Length; i++)
        {
            var result = results[i];
            if(result == null)
                throw new PayoutOutcomeUncertainException(
                    $"Alephium wallet sweep result {i + 1} was null");

            WalletSubmissionOutcome.RequireTransactionId(result.TxId,
                $"Alephium wallet sweep result {i + 1}");
        }

        return results;
    }

    protected async Task SubmitPayoutGroupAsync(Balance[] balances,
        SubmitSettlement request, decimal transactionFee, CancellationToken ct)
    {
        TrackPayoutSubmission(ct, balances);

        SubmitTxResult txSubmit;

        try
        {
            txSubmit = await Guard(() => SubmitTransactionAsync(request, ct), ex =>
            {
                RethrowSubmissionFailure("Alephium transaction submission",
                    "Submit signed transaction failed", ex);
            });
        }
        catch(PayoutOutcomeUncertainException)
        {
            throw;
        }
        catch(AlephiumApiException ex)
        {
            var detail = "Alephium transaction submission was conclusively rejected: " +
                GetApiError(ex);
            NotifyPayoutFailure(poolConfig.Id, balances, detail, ex);
            return;
        }

        var txId = WalletSubmissionOutcome.RequireTransactionId(txSubmit?.TxId,
            "Alephium transaction submission");

        logger.Info(() => $"[{LogCategory}] Payment transaction id: {txId}");

        await PersistPaymentsAsync(balances, txId);

        NotifyPayoutSuccess(poolConfig.Id, balances, new[] { txId }, transactionFee);
    }

    protected virtual Task<SubmitTxResult> SubmitTransactionAsync(
        SubmitSettlement request, CancellationToken ct) =>
        alephiumClient.PostTransactionsSubmitAsync(request, ct);
    
    public override double AdjustShareDifficulty(double difficulty)
    {
        return difficulty * AlephiumConstants.Pow2xDiff1TargetNumZero;
    }

    public double AdjustBlockEffort(double effort)
    {
        return effort * AlephiumConstants.Pow2xDiff1TargetNumZero;
    }
    
    #endregion // IPayoutHandler

    private class PaymentException : Exception
    {
        public PaymentException(string msg) : base(msg)
        {
        }
    }

    private void ReportAndRethrowApiError(string action, Exception ex, bool rethrow = true)
    {
        var error = GetApiError(ex);

        logger.Warn(() => $"{action}: {error}");

        if(rethrow)
            throw ex;
    }

    private static string GetApiError(Exception ex) =>
        ex is AlephiumApiException apiException
            ? apiException.Response ?? apiException.Message
            : ex.Message;

    private void RethrowSubmissionFailure(string operation, string action, Exception ex)
    {
        // Transport failures remain financially ambiguous. A structured REST rejection is
        // conclusive and must escape Guard<T> as its original exception instead of being
        // converted to a null result and misclassified as a malformed success.
        WalletSubmissionOutcome.RethrowIfUnknown(ex, operation);
        ReportAndRethrowApiError(action, ex);
    }

    private async Task UnlockWallet(CancellationToken ct)
    {
        logger.Info(() => $"[{LogCategory}] Unlocking wallet: {extraPoolPaymentProcessingConfig.WalletName}");

        var walletPassword = extraPoolPaymentProcessingConfig.WalletPassword ?? string.Empty;

        await Guard(() => alephiumClient.NameUnlockAsync(extraPoolPaymentProcessingConfig.WalletName, new WalletUnlock {Password = walletPassword}, ct), ex =>
        {
            if (ex is AlephiumApiException apiException)
            {
                var error = apiException.Response;

                if (error != null && !error.ToLower().Contains("already unlocked"))
                    throw new PaymentException($"Failed to unlock wallet: {error}");
            }

            else
                throw ex;
        });

        logger.Info(() => $"[{LogCategory}] Wallet: {extraPoolPaymentProcessingConfig.WalletName} unlocked");
    }

    protected virtual async Task LockWallet(CancellationToken ct)
    {
        logger.Info(() => $"[{LogCategory}] Locking wallet: {extraPoolPaymentProcessingConfig.WalletName}");

        await Guard(() => alephiumClient.NameLockAsync(extraPoolPaymentProcessingConfig.WalletName, ct),
            ex => ReportAndRethrowApiError("Failed to lock wallet", ex));

        logger.Info(() => $"[{LogCategory}] Wallet: {extraPoolPaymentProcessingConfig.WalletName} is locked");
    }
}
