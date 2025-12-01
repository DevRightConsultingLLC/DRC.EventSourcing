using Dapper;
using MySqlConnector;

namespace DRC.EventSourcing.MySql;

/// <summary>
/// Reads active archive segments from MySQL for a given store.
/// </summary>
public sealed class MySqlArchiveSegmentStore<TStore> : IArchiveSegmentStore 
    where TStore : MySqlEventStoreOptions
{
    private readonly MySqlConnectionFactory<TStore> _connectionFactory;
    private readonly MySqlEventStoreOptions _options;

    public MySqlArchiveSegmentStore(
        MySqlConnectionFactory<TStore> connectionFactory, 
        MySqlEventStoreOptions options)
    {
        _connectionFactory = connectionFactory;
        _options = options;
    }

    public async Task<IReadOnlyList<ArchiveSegment>> GetActiveSegmentsAsync(
        CancellationToken ct = default)
    {
        await using var conn = (MySqlConnection)_connectionFactory.CreateConnection();
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

