using System.Data;

namespace Asap.Web.Data;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}
