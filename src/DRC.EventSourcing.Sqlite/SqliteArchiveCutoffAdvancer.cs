﻿using Dapper;
using DRC.EventSourcing.Infrastructure;
using System.Data;

namespace DRC.EventSourcing.Sqlite;

public sealed class SqliteArchiveCutoffAdvancer<TStore> : BaseArchiveCutoffAdvancer<TStore> where TStore : SqliteEventStoreOptions
{
    private readonly TStore _options;

    public SqliteArchiveCutoffAdvancer(SqliteConnectionFactory<TStore> factory, TStore options) : base(factory, options)
    {
        _options = options;
    }

    public override async Task<bool> TryAdvanceArchiveCutoff(string domain, string streamId, int newCutoffVersion, CancellationToken ct = default)
    {
        using var conn = ConnectionFactory.CreateConnection();
        if (conn.State == ConnectionState.Closed) conn.Open();

        var cmd = new Dapper.CommandDefinition($@"UPDATE {((IEventStoreOptions)_options).StreamsTableName}
SET ArchiveCutoffVersion = @NewCutoff
WHERE domain = @Domain AND stream_id = @StreamId AND (ArchiveCutoffVersion IS NULL OR ArchiveCutoffVersion < @NewCutoff);",
            new { NewCutoff = newCutoffVersion, Domain = domain, StreamId = streamId }, cancellationToken: ct);

        var res = await conn.ExecuteAsync(cmd);
        return res > 0;
    }
}
