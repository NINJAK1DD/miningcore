using AutoMapper;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Model.Projections;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using MinerStats = Miningcore.Persistence.Model.Projections.MinerStats;

namespace Miningcore;

public class AutoMapperProfile : Profile
{
    public const string AutofacContextItemName = "ctx";

    public AutoMapperProfile()
    {
        // Fix for Automapper 11 which chokes on recursive objects such as JToken
        CreateMap<JToken, JToken>().ConvertUsing(x=> x);

        //////////////////////
        // outgoing mappings

        CreateMap<Blockchain.Share, Persistence.Model.Share>()
            .ForMember(dest => dest.AccountingId, opt => opt.Ignore())
            .ForMember(dest => dest.AccountingRole, opt => opt.Ignore())
            .ForMember(dest => dest.RewardBasisSatoshis, opt => opt.Ignore());

        CreateMap<Blockchain.Share, Block>()
            .ForMember(dest => dest.Reward, opt => opt.MapFrom(src => src.BlockReward))
            .ForMember(dest => dest.Hash, opt => opt.MapFrom(src => src.BlockHash))
            .ForMember(dest => dest.Type, opt => opt.MapFrom(src => src.BlockType))
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.Status, opt => opt.Ignore())
            .ForMember(dest => dest.ConfirmationProgress, opt => opt.Ignore())
            .ForMember(dest => dest.Effort, opt => opt.Ignore())
            .ForMember(dest => dest.MinerEffort, opt => opt.Ignore())
            .ForMember(dest => dest.NotifyBlockFoundOnUpdate, opt => opt.Ignore())
            .ForMember(dest => dest.NotifyBlockConfirmationProgressOnUpdate, opt => opt.Ignore())
            .ForMember(dest => dest.NotifyBlockUnlockedOnUpdate, opt => opt.Ignore());

        CreateMap<BlockStatus, string>().ConvertUsing(e => e.ToString().ToLower());

        CreateMap<Mining.PoolStats, PoolStats>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.PoolId, opt => opt.Ignore())
            .ForMember(dest => dest.NetworkHashrate, opt => opt.Ignore())
            .ForMember(dest => dest.NetworkDifficulty, opt => opt.Ignore())
            .ForMember(dest => dest.LastNetworkBlockTime, opt => opt.Ignore())
            .ForMember(dest => dest.BlockHeight, opt => opt.Ignore())
            .ForMember(dest => dest.ConnectedPeers, opt => opt.Ignore())
            .ForMember(dest => dest.Created, opt => opt.Ignore());

        CreateMap<BlockchainStats, PoolStats>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.PoolId, opt => opt.Ignore())
            .ForMember(dest => dest.ConnectedMiners, opt => opt.Ignore())
            .ForMember(dest => dest.PoolHashrate, opt => opt.Ignore())
            .ForMember(dest => dest.SharesPerSecond, opt => opt.Ignore())
            .ForMember(dest => dest.Created, opt => opt.Ignore());

        // API
        CreateMap<CoinTemplate, Api.Responses.ApiCoinConfig>()
            .ForMember(dest => dest.Type, opt => opt.MapFrom(src => src.Symbol))
            .ForMember(dest => dest.Family, opt => opt.MapFrom(src => src.Family.ToString().ToLower()))
            .ForMember(dest => dest.Symbol, opt => opt.MapFrom(src => src.Symbol))
            .ForMember(dest => dest.Website, opt => opt.MapFrom(src => src.Website))
            .ForMember(dest => dest.Market, opt => opt.MapFrom(src => src.Market))
            .ForMember(dest => dest.Twitter, opt => opt.MapFrom(src => src.Twitter))
            .ForMember(dest => dest.Discord, opt => opt.MapFrom(src => src.Discord))
            .ForMember(dest => dest.Telegram, opt => opt.MapFrom(src => src.Telegram))
            .ForMember(dest => dest.Github, opt => opt.MapFrom(src => src.Github))
            .ForMember(dest => dest.Algorithm, opt => opt.MapFrom(src => src.GetAlgorithmName()));

        // Public endpoint DTOs form a one-way boundary from runtime
        // configuration. Private TLS credentials and trusted PROXY-protocol peer
        // addresses have no destination members and therefore cannot be exposed
        // by this API projection or affect same-type internal mappings.
        CreateMap<VarDiffConfig, Api.Responses.ApiVarDiffConfig>();
        CreateMap<TcpProxyProtocolConfig,
            Api.Responses.ApiTcpProxyProtocolConfig>();
        CreateMap<PoolEndpoint, Api.Responses.ApiPoolEndpoint>();
        CreateMap<PoolShareBasedBanningConfig,
            Api.Responses.ApiPoolShareBasedBanningConfig>();

        CreateMap<PoolConfig, Api.Responses.PoolInfo>()
            .ForMember(dest => dest.Coin, opt => opt.MapFrom(src => src.Template))
            .ForMember(dest => dest.Ports, opt => opt.Ignore())
            .ForMember(dest => dest.ShareBasedBanning, opt => opt.MapFrom(src => src.Banning))
            .ForMember(dest => dest.PoolFeePercent, opt => opt.Ignore())
            .ForMember(dest => dest.AddressInfoLink, opt => opt.Ignore())
            .ForMember(dest => dest.PoolStats, opt => opt.Ignore())
            .ForMember(dest => dest.NetworkStats, opt => opt.Ignore())
            .ForMember(dest => dest.TopMiners, opt => opt.Ignore())
            .ForMember(dest => dest.TotalPaid, opt => opt.Ignore())
            .ForMember(dest => dest.TotalBlocks, opt => opt.Ignore())
            .ForMember(dest => dest.TotalConfirmedBlocks, opt => opt.Ignore())
            .ForMember(dest => dest.TotalPendingBlocks, opt => opt.Ignore())
            .ForMember(dest => dest.BlockReward, opt => opt.Ignore())
            .ForMember(dest => dest.LastPoolBlockTime, opt => opt.Ignore())
            .ForMember(dest => dest.PoolEffort, opt => opt.Ignore());

        CreateMap<PoolStats, Api.Responses.AggregatedPoolStats>()
            .ForMember(dest => dest.ValidSharesPerSecond,
                opt => opt.MapFrom(src => src.SharesPerSecond));

        CreateMap<Block, Api.Responses.Block>()
            .ForMember(dest => dest.InfoLink, opt => opt.Ignore())
            .ForMember(dest => dest.DirectRecipientOutputs, opt => opt.MapFrom(src =>
                string.IsNullOrEmpty(src.DirectRecipientOutputs)
                    ? Array.Empty<BitcoinDirectCoinbaseOutput>()
                    : JsonConvert.DeserializeObject<BitcoinDirectCoinbaseOutput[]>(
                        src.DirectRecipientOutputs)));

        CreateMap<MinerSettings, Api.Responses.MinerSettings>();

        CreateMap<Payment, Api.Responses.Payment>()
            .ForMember(dest => dest.AddressInfoLink, opt => opt.Ignore())
            .ForMember(dest => dest.TransactionInfoLink, opt => opt.Ignore());

        CreateMap<BalanceChange, Api.Responses.BalanceChange>();
        CreateMap<PoolPaymentProcessingConfig,
                Api.Responses.ApiPoolPaymentProcessingConfig>()
            .ForMember(dest => dest.Extra, opt => opt.Ignore());

        CreateMap<MinerStats, Api.Responses.MinerStats>()
            .ForMember(dest => dest.LastPayment, opt => opt.Ignore())
            .ForMember(dest => dest.LastPaymentLink, opt => opt.Ignore())
            .ForMember(dest => dest.PerformanceSamples, opt => opt.Ignore())
            .ForMember(dest => dest.TotalConfirmedBlocks, opt => opt.MapFrom(src => src.TotalConfirmedBlocks))
            .ForMember(dest => dest.TotalPendingBlocks, opt => opt.MapFrom(src => src.TotalPendingBlocks));

        CreateMap<WorkerPerformanceStats, Api.Responses.WorkerPerformanceStats>();
        CreateMap<WorkerPerformanceStatsContainer, Api.Responses.WorkerPerformanceStatsContainer>();
        CreateMap<MinerWorkerPerformanceStats, Api.Responses.MinerPerformanceStats>();

        // PostgreSQL
        CreateMap<Persistence.Model.Share, Persistence.Postgres.Entities.Share>();
        CreateMap<Block, Persistence.Postgres.Entities.Block>();
        CreateMap<Balance, Persistence.Postgres.Entities.Balance>();
        CreateMap<Payment, Persistence.Postgres.Entities.Payment>();
        CreateMap<MinerSettings, Persistence.Postgres.Entities.MinerSettings>();
        CreateMap<PoolStats, Persistence.Postgres.Entities.PoolStats>();

        CreateMap<MinerWorkerPerformanceStats, Persistence.Postgres.Entities.MinerWorkerPerformanceStats>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.Partition, opt => opt.Ignore());

        //////////////////////
        // incoming mappings

        // API
        CreateMap<Api.Responses.MinerSettings, MinerSettings>()
            .ForMember(dest => dest.PoolId, opt => opt.Ignore())
            .ForMember(dest => dest.Address, opt => opt.Ignore())
            .ForMember(dest => dest.Created, opt => opt.Ignore())
            .ForMember(dest => dest.Updated, opt => opt.Ignore());

        // PostgreSQL
        CreateMap<Persistence.Postgres.Entities.Share, Persistence.Model.Share>();
        CreateMap<Persistence.Postgres.Entities.Block, Block>()
            .ForMember(dest => dest.NotifyBlockFoundOnUpdate, opt => opt.Ignore())
            .ForMember(dest => dest.NotifyBlockConfirmationProgressOnUpdate, opt => opt.Ignore())
            .ForMember(dest => dest.NotifyBlockUnlockedOnUpdate, opt => opt.Ignore());
        CreateMap<Persistence.Postgres.Entities.Balance, Balance>();
        CreateMap<Persistence.Postgres.Entities.Payment, Payment>();
        CreateMap<Persistence.Postgres.Entities.BalanceChange, BalanceChange>();
        CreateMap<Persistence.Postgres.Entities.PoolStats, PoolStats>();
        CreateMap<Persistence.Postgres.Entities.MinerSettings, MinerSettings>();
        CreateMap<Persistence.Postgres.Entities.MinerWorkerPerformanceStats, MinerWorkerPerformanceStats>();
        CreateMap<Persistence.Postgres.Entities.MinerWorkerPerformanceStats, Api.Responses.MinerPerformanceStats>();

        CreateMap<PoolStats, Mining.PoolStats>()
            .ForMember(dest => dest.LastPoolBlockTime, opt => opt.Ignore());

        CreateMap<PoolStats, BlockchainStats>()
            .ForMember(dest => dest.RewardType, opt => opt.Ignore())
            .ForMember(dest => dest.NetworkType, opt => opt.Ignore())
            .ForMember(dest => dest.NextNetworkTarget, opt => opt.Ignore())
            .ForMember(dest => dest.NextNetworkBits, opt => opt.Ignore())
            .ForMember(dest => dest.NodeVersion, opt => opt.Ignore());
    }
}
