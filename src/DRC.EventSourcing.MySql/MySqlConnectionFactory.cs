using System.Data;
using DRC.EventSourcing.Infrastructure;
using MySqlConnector;

namespace DRC.EventSourcing.MySql;

/// <summary>
/// Factory for creating MySQL database connections.
/// </summary>
public sealed class MySqlConnectionFactory<TStore> : BaseConnectionFactory<TStore>
    where TStore : MySqlEventStoreOptions
{
    public MySqlConnectionFactory(TStore options) : base(options)
    {
    }

    protected override IDbConnection CreateConnectionCore()
    {
        return new MySqlConnection(Options.ConnectionString);
    }
    
}

