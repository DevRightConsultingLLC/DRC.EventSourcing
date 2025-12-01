using Dapper;
using DRC.EventSourcing.Infrastructure;

namespace DRC.EventSourcing.Sqlite;

/// <summary>
/// SQLite implementation of ISnapshotStore using per-store table names.
/// </summary>
public sealed class SqliteSnapshotStore<TStore> : BaseSnapshotStore<TStore>, ISnapshotStore<TStore> where TStore : SqliteEventStoreOptions
{
    private readonly SqliteConnectionFactory<TStore> _connectionFactory;
    private readonly TStore _options;

    public SqliteSnapshotStore(SqliteConnectionFactory<TStore> connectionFactory, TStore options)
        : base(connectionFactory, options)
    {
        _connectionFactory = connectionFactory;
        _options = options;
    }

    protected override Task<Snapshot?> ProviderGetLatestAsync(string streamId, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            await using var conn = (Microsoft.Data.Sqlite.SqliteConnection)_connectionFactory.CreateConnection();
            await conn.OpenAsync(ct);

            var cmd = new CommandDefinition($@"SELECT StreamId, StreamVersion, Data, CreatedUtc
               FROM {((IEventStoreOptions)_options).SnapshotsTableName}
               WHERE StreamId = @StreamId", new { StreamId = streamId }, cancellationToken: ct);
            var row = await conn.QuerySingleOrDefaultAsync<SnapshotRow>(cmd);
            if (row is null) return null;
            return new Snapshot(row.StreamId, new StreamVersion(row.StreamVersion), row.Data, row.CreatedUtc);
        }, ct);
    }

    protected override Task ProviderSaveAsync(Snapshot snapshot, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            await using var conn = (Microsoft.Data.Sqlite.SqliteConnection)_connectionFactory.CreateConnection();
            await conn.OpenAsync(ct);

            var cmd = new CommandDefinition($@"INSERT INTO {((IEventStoreOptions)_options).SnapshotsTableName}
                    (StreamId, StreamVersion, Data, CreatedUtc)
               VALUES (@StreamId, @StreamVersion, @Data, @CreatedUtc)
               ON CONFLICT(StreamId) DO UPDATE SET
                    StreamVersion = excluded.StreamVersion,
                    Data          = excluded.Data,
                    CreatedUtc    = excluded.CreatedUtc;",
                new
                {
                    StreamId = snapshot.StreamId,
                    StreamVersion = snapshot.StreamVersion.Value,
                    Data = snapshot.Data,
                    CreatedUtc = snapshot.CreatedUtc
                },
                cancellationToken: ct);
            await conn.ExecuteAsync(cmd);
        }, ct);
    }

    private sealed class SnapshotRow
    {
        public string StreamId { get; set; } = default!;
        public int StreamVersion { get; set; }
        public byte[] Data { get; set; } = default!;
        public DateTime CreatedUtc { get; set; }
    }
}
