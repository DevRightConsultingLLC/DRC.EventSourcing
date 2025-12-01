using System.Data;
using Microsoft.Data.SqlClient;
using DRC.EventSourcing.Infrastructure;

namespace DRC.EventSourcing.SqlServer;

/// <summary>
/// Simple connection factory for SQL Server.
/// </summary>
public sealed class SqlServerConnectionFactory<TStore> : BaseConnectionFactory<TStore> where TStore: SqlServerEventStoreOptions
{
    private readonly string _connectionString;

    public SqlServerConnectionFactory(TStore options) : base(options)
    {
        _connectionString = options.ConnectionString ?? string.Empty;
    }

    protected override IDbConnection CreateConnectionCore() => new SqlConnection(_connectionString);
}
