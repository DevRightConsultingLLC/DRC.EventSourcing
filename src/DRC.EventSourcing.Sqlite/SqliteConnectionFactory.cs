// DRC.EventSourcing.Sqlite/SqliteConnectionFactory.cs
using System.Data;
using Microsoft.Data.Sqlite;
using DRC.EventSourcing.Infrastructure;

namespace DRC.EventSourcing.Sqlite;

public sealed class SqliteConnectionFactory<TStore> : BaseConnectionFactory<TStore> where TStore : SqliteEventStoreOptions
{
    private readonly TStore _options;
    private readonly string _effectiveConnectionString;
    private readonly SqliteConnection? _keepAliveConnection;

    static SqliteConnectionFactory()
    {
        // Ensure SQLite provider is initialized for consumers that don't call Batteries.Init
        SQLitePCL.Batteries_V2.Init();
    }

    public SqliteConnectionFactory(TStore options) : base(options)
    {
        _options = options;

        var cs = options.ConnectionString?.Trim() ?? string.Empty;

        if (IsInMemoryRequested(cs))
        {
            // Create a stable name per store to avoid collisions and allow sharing across connections
            var storeName = string.IsNullOrWhiteSpace(options.StoreName) ? Guid.NewGuid().ToString("N") : options.StoreName;
            var safeName = Uri.EscapeDataString(storeName);

            // Build a file: URI that creates a named in-memory DB with shared cache
            _effectiveConnectionString = $"Data Source=file:{safeName}?mode=memory&cache=shared";

            // Keep a single connection open for the lifetimes of the factory so the in-memory DB remains alive
            _keepAliveConnection = new SqliteConnection(_effectiveConnectionString);
            _keepAliveConnection.Open();
        }
        else
        {
            _effectiveConnectionString = cs;
        }
    }

    protected override IDbConnection CreateConnectionCore() => new SqliteConnection(_effectiveConnectionString);

    protected override void InitializeProviderBatteries()
    {
        // Ensure SQLite provider is initialized for consumers that don't call Batteries.Init
        SQLitePCL.Batteries_V2.Init();
    }

    private static bool IsInMemoryRequested(string cs)
    {
        if (string.IsNullOrWhiteSpace(cs)) return false;

        // Common patterns:
        //  - ":memory:"
        //  - contains "mode=memory" (either in URI or key/value form)
        //  - contains ":memory:" as a Data Source
        if (cs.Equals(":memory:", StringComparison.OrdinalIgnoreCase)) return true;
        if (cs.IndexOf("mode=memory", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (cs.IndexOf(":memory:", StringComparison.OrdinalIgnoreCase) >= 0) return true;

        return false;
    }
}