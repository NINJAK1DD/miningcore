using AutoMapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Miningcore;

internal static class AutoMapperFactory
{
    private static MapperConfiguration CreateConfiguration(ILoggerFactory loggerFactory)
    {
        loggerFactory ??= NullLoggerFactory.Instance;

        return new MapperConfiguration(cfg => cfg.AddProfile(new AutoMapperProfile()),
            loggerFactory);
    }

    public static IMapper CreateMapper(ILoggerFactory loggerFactory = null) =>
        CreateConfiguration(loggerFactory).CreateMapper();
}
