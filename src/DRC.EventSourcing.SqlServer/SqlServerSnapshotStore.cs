using Dapper;
using Microsoft.Data.SqlClient;
using DRC.EventSourcing.Infrastructure;

namespace DRC.EventSourcing.SqlServer;

public sealed class SqlServerSnapshotStore<TStore> : BaseSnapshotStore<TStore>, ISnapshotStore<TStore> where TStore: SqlServerEventStoreOptions
{
    private readonly SqlServerConnectionFactory<TStore> _connectionFactory;

    public SqlServerSnapshotStore(SqlServerConnectionFactory<TStore> connectionFactory, TStore options)
        : base(connectionFactory, options)
    {
        _connectionFactory = connectionFactory;
    }

    // ProviderGetLatestAsync and ProviderSaveAsync implement DB access directly using Options

    protected override async Task<Snapshot?> ProviderGetLatestAsync(string streamId, CancellationToken ct)
    {
        await using var conn = (SqlConnection)_connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        var cmd = new CommandDefinition($@"SELECT StreamId, StreamVersion, Data, CreatedUtc
              FROM {((IEventStoreOptions)Options).SnapshotsTableName}
              WHERE StreamId = @StreamId", new { StreamId = streamId }, cancellationToken: ct);
        var row = await conn.QuerySingleOrDefaultAsync<SnapshotRow>(cmd);
        if (row is null) return null;
        return new Snapshot(row.StreamId, new StreamVersion(row.StreamVersion), row.Data, row.CreatedUtc);
    }

    protected override async Task ProviderSaveAsync(Snapshot snapshot, CancellationToken ct)
    {
        await using var conn = (SqlConnection)_connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        var mergeSql = $@"MERGE {((IEventStoreOptions)Options).SnapshotsTableName} AS target
USING (VALUES (@StreamId, @StreamVersion, @Data, @CreatedUtc))
      AS source (StreamId, StreamVersion, Data, CreatedUtc)
ON target.StreamId = source.StreamId
WHEN MATCHED THEN
    UPDATE SET StreamVersion = source.StreamVersion,
               Data          = source.Data,
               CreatedUtc    = source.CreatedUtc
WHEN NOT MATCHED THEN
    INSERT (StreamId, StreamVersion, Data, CreatedUtc)
    VALUES (source.StreamId, source.StreamVersion, source.Data, source.CreatedUtc);";

        var cmd = new CommandDefinition(mergeSql, new { StreamId = snapshot.StreamId, StreamVersion = snapshot.StreamVersion.Value, Data = snapshot.Data, CreatedUtc = snapshot.CreatedUtc }, cancellationToken: ct);
        await conn.ExecuteAsync(cmd);
    }

    private sealed class SnapshotRow
    {
        public string StreamId { get; set; } = default!;
        public int StreamVersion { get; set; }
        public byte[] Data { get; set; } = default!;
        public DateTime CreatedUtc { get; set; }
    }
}