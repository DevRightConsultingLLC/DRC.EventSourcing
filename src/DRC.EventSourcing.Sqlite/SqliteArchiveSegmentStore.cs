using Dapper;
using Microsoft.Data.Sqlite;

namespace DRC.EventSourcing.Sqlite;

/// <summary>
/// Reads active archive segments from SQLite for a given store.
/// </summary>
public sealed class SqliteArchiveSegmentStore<TStore> : IArchiveSegmentStore where TStore : SqliteEventStoreOptions
{
    private readonly SqliteConnectionFactory<TStore> _connectionFactory;
    private readonly SqliteEventStoreOptions _options;

    public SqliteArchiveSegmentStore(SqliteConnectionFactory<TStore> connectionFactory, SqliteEventStoreOptions options)
    {
        _connectionFactory = connectionFactory;
        _options = options;
    }

    public async Task<IReadOnlyList<ArchiveSegment>> GetActiveSegmentsAsync(
        CancellationToken ct = default)
    {
        await using var conn = (SqliteConnection)_connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        var cmd = new CommandDefinition(
            $@"SELECT MinPosition, MaxPosition, FileName
               FROM {((IEventStoreOptions)_options).ArchiveSegmentsTableName}
               WHERE Status = 1
               ORDER BY MinPosition",
            cancellationToken: ct);

        var rows = await conn.QueryAsync<SegmentRow>(cmd);

        return rows
            .Select(r => new ArchiveSegment(
                new GlobalPosition(r.MinPosition),
                new GlobalPosition(r.MaxPosition),
                r.FileName))
            .ToArray();
    }

    private sealed class SegmentRow
    {
        public long MinPosition { get; set; }
        public long MaxPosition { get; set; }
        public string FileName { get; set; } = default!;
    }
}
