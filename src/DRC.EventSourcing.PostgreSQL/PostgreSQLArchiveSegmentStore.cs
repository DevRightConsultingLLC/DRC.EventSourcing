using Dapper;
using Npgsql;

namespace DRC.EventSourcing.PostgreSQL;

/// <summary>
/// Reads active archive segments from PostgreSQL for a given store.
/// </summary>
public sealed class PostgreSQLArchiveSegmentStore<TStore> : IArchiveSegmentStore 
    where TStore : PostgreSQLEventStoreOptions
{
    private readonly PostgreSQLConnectionFactory<TStore> _connectionFactory;
    private readonly PostgreSQLEventStoreOptions _options;

    public PostgreSQLArchiveSegmentStore(
        PostgreSQLConnectionFactory<TStore> connectionFactory, 
        PostgreSQLEventStoreOptions options)
    {
        _connectionFactory = connectionFactory;
        _options = options;
    }

    public async Task<IReadOnlyList<ArchiveSegment>> GetActiveSegmentsAsync(
        CancellationToken ct = default)
    {
        await using var conn = (NpgsqlConnection)_connectionFactory.CreateConnection();
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

