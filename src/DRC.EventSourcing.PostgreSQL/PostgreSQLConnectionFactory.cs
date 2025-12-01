using System.Data;
using DRC.EventSourcing.Infrastructure;
using Npgsql;

namespace DRC.EventSourcing.PostgreSQL;

/// <summary>
/// Factory for creating PostgreSQL database connections.
/// </summary>
public sealed class PostgreSQLConnectionFactory<TStore> : BaseConnectionFactory<TStore>
    where TStore : PostgreSQLEventStoreOptions
{
    public PostgreSQLConnectionFactory(TStore options) : base(options)
    {
    }

    protected override IDbConnection CreateConnectionCore()
    {
        return new NpgsqlConnection(Options.ConnectionString);
    }
}

