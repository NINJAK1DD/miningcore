using System.Data;

namespace Miningcore.Persistence;

public interface IConnectionFactory
{
    Task<IDbConnection> OpenConnectionAsync();
}

internal interface ICancellableConnectionFactory
{
    Task<IDbConnection> OpenConnectionAsync(CancellationToken ct);
}
