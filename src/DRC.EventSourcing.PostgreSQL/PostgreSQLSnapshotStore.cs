﻿using Dapper;
using DRC.EventSourcing.Infrastructure;
using System.Data;

namespace DRC.EventSourcing.PostgreSQL;

/// <summary>
/// PostgreSQL-specific implementation of the snapshot store.
/// </summary>
public sealed class PostgreSQLSnapshotStore<TStore> : BaseSnapshotStore<TStore>, ISnapshotStore<TStore> 
    where TStore : PostgreSQLEventStoreOptions
{
    private readonly TStore _options;

    public PostgreSQLSnapshotStore(
        PostgreSQLConnectionFactory<TStore> connectionFactory, 
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
            (string)row.streamid,
            new StreamVersion((int)row.streamversion),
            (byte[])row.data,
            (DateTime)row.createdutc
        );
    }

    protected override async Task ProviderSaveAsync(Snapshot snapshot, CancellationToken ct)
    {
        using var conn = ConnectionFactory.CreateConnection();
        
        var cmd = new CommandDefinition(
            $@"INSERT INTO {((IEventStoreOptions)_options).SnapshotsTableName} (StreamId, StreamVersion, Data, CreatedUtc)
               VALUES (@StreamId, @StreamVersion, @Data, @CreatedUtc)
               ON CONFLICT (StreamId)
               DO UPDATE SET
                   StreamVersion = EXCLUDED.StreamVersion,
                   Data = EXCLUDED.Data,
                   CreatedUtc = EXCLUDED.CreatedUtc",
            new
            {
                StreamId = snapshot.StreamId,
                StreamVersion = snapshot.StreamVersion.Value,
                Data = snapshot.Data,
                CreatedUtc = snapshot.CreatedUtc
            },
            cancellationToken: ct);

        await conn.ExecuteAsync(cmd);
    }
}

