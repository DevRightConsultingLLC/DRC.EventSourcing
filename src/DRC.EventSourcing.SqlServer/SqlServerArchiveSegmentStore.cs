using Dapper;
using Microsoft.Data.SqlClient;

namespace DRC.EventSourcing.SqlServer;

public sealed class SqlServerArchiveSegmentStore<TStore> : IArchiveSegmentStore where TStore: SqlServerEventStoreOptions
{
    private readonly SqlServerConnectionFactory<TStore> _connectionFactory;

    public SqlServerArchiveSegmentStore(SqlServerConnectionFactory<TStore> connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Returns all active archive segments (Status = 1), ordered by MinPosition.
    /// </summary>
    public async Task<IReadOnlyList<ArchiveSegment>> GetActiveSegmentsAsync(
        CancellationToken ct = default)
    {
        await using var conn = (SqlConnection)_connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        var cmd = new CommandDefinition(
            @"SELECT MinPosition, MaxPosition, FileName
              FROM ArchiveSegments
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