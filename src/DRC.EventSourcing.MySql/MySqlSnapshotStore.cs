using Dapper;
using DRC.EventSourcing.Infrastructure;
using System.Data;

namespace DRC.EventSourcing.MySql;

/// <summary>
/// MySQL-specific implementation of the snapshot store.
/// </summary>
public sealed class MySqlSnapshotStore<TStore> : BaseSnapshotStore<TStore>, ISnapshotStore<TStore> 
    where TStore : MySqlEventStoreOptions
{
    private readonly TStore _options;

    public MySqlSnapshotStore(
        MySqlConnectionFactory<TStore> connectionFactory, 
        TStore options)
        : base(connectionFactory, options)
    {
        _options = options;
    }

    protected override async Task<Snapshot?> ProviderGetLatestAsync(string streamId, CancellationToken ct)
    {
        using var conn = ConnectionFactory.CreateConnection();
        
        var cmd = new CommandDefinition(
            $@"SELECT StreamId, StreamVersion, Data, CreatedUtc
               FROM {((IEventStoreOptions)_options).SnapshotsTableName}
               WHERE StreamId = @StreamId",
            new { StreamId = streamId },
            cancellationToken: ct);

        var row = await conn.QueryFirstOrDefaultAsync<dynamic>(cmd);
        if (row is null) return null;

        return new Snapshot(
            (string)row.StreamId,
            new StreamVersion((int)row.StreamVersion),
            (byte[])row.Data,
            (DateTime)row.CreatedUtc
        );
    }

    protected override async Task ProviderSaveAsync(Snapshot snapshot, CancellationToken ct)
    {
        using var conn = ConnectionFactory.CreateConnection();
        
        var cmd = new CommandDefinition(
            $@"INSERT INTO {((IEventStoreOptions)_options).SnapshotsTableName} (StreamId, StreamVersion, Data, CreatedUtc)
               VALUES (@StreamId, @StreamVersion, @Data, @CreatedUtc)
               ON DUPLICATE KEY UPDATE
                   StreamVersion = VALUES(StreamVersion),
                   Data = VALUES(Data),
                   CreatedUtc = VALUES(CreatedUtc)",
            new
            {
                snapshot.StreamId,
                StreamVersion = snapshot.StreamVersion.Value,
                snapshot.Data,
                snapshot.CreatedUtc
            },
            cancellationToken: ct);

        await conn.ExecuteAsync(cmd);
    }
}

