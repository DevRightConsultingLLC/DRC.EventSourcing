using Dapper;
using System.Data;
using DRC.EventSourcing.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DRC.EventSourcing.PostgreSQL;

/// <summary>
/// PostgreSQL-specific implementation of the event store.
/// </summary>
public sealed class PostgreSQLEventStore<TStore> : BaseEventStore<TStore> where TStore: PostgreSQLEventStoreOptions
{
    private readonly TStore _options;

    public PostgreSQLEventStore(
        PostgreSQLConnectionFactory<TStore> connectionFactory, 
        TStore options,
        IDomainRetentionPolicyProvider policyProvider,
        ILogger<PostgreSQLEventStore<TStore>> logger,
        EventStoreMetrics? metrics = null) 
        : base(connectionFactory, options, policyProvider, logger, metrics)
    {
        _options = options;
    }

    protected override async Task<(int lastVersion, int status, long lastPosition)?> ReadStreamHeadAsync(
        IDbConnection conn, IDbTransaction tx, string domain, string streamId, CancellationToken ct)
    {
        try
        {
            var headCmd = new CommandDefinition(
                $@"SELECT last_version, 
                          CASE WHEN IsDeleted = true THEN 1 ELSE 0 END AS status, 
                          last_position 
                   FROM {((IEventStoreOptions)_options).StreamsTableName}
                   WHERE domain = @Domain AND stream_id = @StreamId
                   FOR UPDATE",
                new { Domain = domain, StreamId = streamId }, tx, cancellationToken: ct);
            
            var head = await conn.QueryFirstOrDefaultAsync(headCmd);
            if (head is null) return null;

            int lastVersion = (int)head.last_version;
            int status = (int)head.status;
            long lastPosition = (long)head.last_position;
            return (lastVersion, status, lastPosition);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to read stream head for domain '{domain}' and streamId '{streamId}'", ex);
        }
    }

    protected override async Task<long?> InsertEventAndReturnGlobalPositionAsync(
        IDbConnection conn, IDbTransaction tx, EventData e, string domain, string streamId, 
        int version, DateTime createdUtc, CancellationToken ct)
    {
        var insertCmd = new CommandDefinition(
            $@"INSERT INTO {((IEventStoreOptions)_options).EventsTableName} 
               (StreamId, StreamDomain, StreamVersion, StreamNamespace, EventType, Data, Metadata, CreatedUtc)
               VALUES (@StreamId, @Domain, @Version, @Namespace, @EventType, @Data, @Metadata, @CreatedUtc)
               RETURNING GlobalPosition",
            new
            {
                StreamId = streamId,
                Domain = domain,
                Version = version,
                Namespace = e.Namespace ?? string.Empty,
                EventType = e.EventType,
                Data = e.Data,
                Metadata = e.Metadata,
                CreatedUtc = createdUtc
            },
            tx,
            cancellationToken: ct);

        return await conn.ExecuteScalarAsync<long?>(insertCmd);
    }

    protected override async Task UpsertStreamHeadAsync(
        IDbConnection conn, IDbTransaction tx, string domain, string streamId, 
        RetentionMode retentionMode, int lastVersion, long lastPosition, CancellationToken ct)
    {
        var upsertCmd = new CommandDefinition($@"
INSERT INTO {((IEventStoreOptions)_options).StreamsTableName} 
    (domain, stream_id, last_version, last_position, archived_at, ArchiveCutoffVersion, RetentionMode, IsDeleted)
VALUES (@Domain, @StreamId, @LastVersion, @LastPosition, NULL, NULL, @RetentionMode, false)
ON CONFLICT (domain, stream_id)
DO UPDATE SET
    last_version = EXCLUDED.last_version,
    last_position = EXCLUDED.last_position", 
            new { 
                Domain = domain, 
                StreamId = streamId, 
                LastVersion = lastVersion, 
                LastPosition = lastPosition, 
                RetentionMode = (int)retentionMode 
            }, 
            tx, 
            cancellationToken: ct);

        await conn.ExecuteAsync(upsertCmd);
    }

    protected override async Task<int?> ProbeLatestVersionAsync(
        IDbConnection conn, string domain, string streamId, CancellationToken ct)
    {
        return await conn.ExecuteScalarAsync<int?>(
            new CommandDefinition(
                $"SELECT MAX(StreamVersion) FROM {((IEventStoreOptions)_options).EventsTableName} WHERE StreamId = @StreamId AND StreamDomain = @Domain", 
                new { StreamId = streamId, Domain = domain }, 
                cancellationToken: ct));
    }

    protected override async Task<StreamHeader?> ReadStreamHeaderAsync(
        IDbConnection conn, string domain, string streamId, CancellationToken ct)
    {
        var cmd = new CommandDefinition(
            $@"SELECT domain, 
                      stream_id, 
                      last_version, 
                      last_position, 
                      RetentionMode,
                      IsDeleted,
                      archived_at,
                      ArchiveCutoffVersion
               FROM {((IEventStoreOptions)_options).StreamsTableName}
               WHERE domain = @Domain AND stream_id = @StreamId",
            new { Domain = domain, StreamId = streamId },
            cancellationToken: ct);

        var row = await conn.QueryFirstOrDefaultAsync<dynamic>(cmd);
        if (row is null) return null;

        return new StreamHeader(
            (string)row.domain,
            (string)row.stream_id,
            (int)row.last_version,
            (long)row.last_position,
            (RetentionMode)(short)row.retentionmode,
            (bool)row.isdeleted,
            row.archived_at as DateTime?,
            row.archivecutoffversion as int?
        );
    }

    protected override async Task<IReadOnlyList<EventEnvelope>> ReadStreamRowsAsync(
        IDbConnection conn, string domain, string streamId, string? nameSpace, 
        StreamVersion fromVersionInclusive, int maxCount, CancellationToken ct)
    {
        var nsClause = nameSpace is null ? string.Empty : "AND StreamNamespace = @Namespace";

        var cmd = new CommandDefinition(
            $@"SELECT GlobalPosition,
                      StreamId,
                      StreamDomain,
                      StreamVersion,
                      StreamNamespace,
                      EventType,
                      Data,
                      Metadata,
                      CreatedUtc
               FROM {((IEventStoreOptions)_options).EventsTableName}
               WHERE StreamDomain = @Domain AND StreamId = @StreamId AND StreamVersion >= @FromVersion {nsClause}
               ORDER BY StreamVersion
               LIMIT @MaxCount",
            new { 
                Domain = domain, 
                StreamId = streamId, 
                FromVersion = fromVersionInclusive.Value, 
                Namespace = nameSpace, 
                MaxCount = maxCount 
            },
            cancellationToken: ct);

        var rows = await conn.QueryAsync<SqlEventRow>(cmd);
        return rows.Select(r => r.ToEnvelope()).ToArray();
    }

    protected override async Task<IReadOnlyList<EventEnvelope>> ReadAllForwardsRowsAsync(
        IDbConnection conn, string? domain, string? nameSpace, long positionExclusive, 
        int batchSize, CancellationToken ct)
    {
        var domainClause = domain is null ? string.Empty : "AND StreamDomain = @Domain";
        var nsClause = nameSpace is null ? string.Empty : "AND StreamNamespace = @Namespace";

        var sql = $@"
SELECT GlobalPosition,
       StreamId,
       StreamDomain,
       StreamVersion,
       StreamNamespace,
       EventType,
       Data,
       Metadata,
       CreatedUtc
FROM {((IEventStoreOptions)_options).EventsTableName}
WHERE GlobalPosition > @FromPos {domainClause} {nsClause}
ORDER BY GlobalPosition
LIMIT @BatchSize";

        var cmd = new CommandDefinition(
            sql,
            new { 
                Domain = domain, 
                Namespace = nameSpace, 
                FromPos = positionExclusive, 
                BatchSize = batchSize 
            },
            cancellationToken: ct);

        var rows = await conn.QueryAsync<SqlEventRow>(cmd);
        return rows.Select(r => r.ToEnvelope()).ToArray();
    }

    private sealed class SqlEventRow
    {
        public long GlobalPosition { get; set; }
        public string StreamId { get; set; } = default!;
        public string StreamDomain { get; set; } = default!;
        public int StreamVersion { get; set; }
        public string StreamNamespace { get; set; } = default!;
        public string EventType { get; set; } = default!;
        public byte[] Data { get; set; } = default!;
        public byte[]? Metadata { get; set; }
        public DateTime CreatedUtc { get; set; }

        public EventEnvelope ToEnvelope() =>
            new(
                StreamId,
                new StreamVersion(StreamVersion),
                new GlobalPosition(GlobalPosition),
                EventType,
                Data,
                Metadata,
                CreatedUtc);
    }

    protected override async Task<long?> SelectMinGlobalPositionAsync(
        IDbConnection conn, CancellationToken ct)
    {
        return await conn.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                $"SELECT MIN(GlobalPosition) FROM {((IEventStoreOptions)_options).EventsTableName}",
                cancellationToken: ct));
    }

    protected override async Task<int?> SelectMaxStreamVersionAsync(
        IDbConnection conn, string domain, string streamId, CancellationToken ct)
    {
        return await conn.ExecuteScalarAsync<int?>(
            new CommandDefinition(
                $"SELECT MAX(StreamVersion) FROM {((IEventStoreOptions)_options).EventsTableName} WHERE StreamDomain = @Domain AND StreamId = @StreamId",
                new { Domain = domain, StreamId = streamId },
                cancellationToken: ct));
    }
}

