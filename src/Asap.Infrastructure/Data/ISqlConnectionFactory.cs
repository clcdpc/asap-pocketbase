using System.Data;

namespace Asap.Infrastructure.Data;

public interface ISqlConnectionFactory
{
    Task<IDbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}
