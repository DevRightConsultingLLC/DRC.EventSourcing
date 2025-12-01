using Dapper;
using System.Data;
using DRC.EventSourcing.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DRC.EventSourcing.Sqlite;

/// <summary>
/// SQLite-specific implementation of the event store.
/// </summary>
/// <typeparam name="TStore">The SQLite options type</typeparam>
/// <remarks>
/// <para>SQLite-specific features and considerations:</para>
/// <list type="bullet">
///   <item>Uses AUTOINCREMENT for GlobalPosition (monotonically increasing)</item>
///   <item>DateTime values stored as ISO 8601 strings in UTC</item>
///   <item>UPSERT operations via INSERT...ON CONFLICT</item>
///   <item>Serialized transaction mode ensures thread safety</item>
///   <item>WAL mode recommended for better concurrent read performance</item>
/// </list>
/// <para>Performance characteristics:</para>
/// <list type="bullet">
///   <item>Append: 1-3ms for small batches</item>
///   <item>Read: Sub-millisecond for indexed queries</item>
///   <item>Suitable for: Single-server applications, embedded scenarios, development/testing</item>
/// </list>
/// </remarks>
public sealed class SqliteEventStore<TStore> : BaseEventStore<TStore> where TStore : SqliteEventStoreOptions
{
    private readonly SqliteConnectionFactory<TStore> _factory;
    private readonly TStore _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteEventStore{TStore}"/> class.
    /// </summary>
    /// <param name="factory">Factory for creating SQLite connections</param>
    /// <param name="options">SQLite-specific configuration options</param>
    /// <param name="policyProvider">Domain retention policy provider</param>
    /// <param name="logger">Logger instance for diagnostic logging</param>
    /// <param name="metrics">Optional metrics collector for observability</param>
    public SqliteEventStore(
        SqliteConnectionFactory<TStore> factory, 
        TStore options,
        IDomainRetentionPolicyProvider policyProvider,
        ILogger<SqliteEventStore<TStore>> logger,
        EventStoreMetrics? metrics = null) 
        : base(factory, options, policyProvider, logger, metrics)
    {
        _factory = factory;
        _options = options;
    }

    protected override async Task<(int lastVersion, int status, long lastPosition)?> ReadStreamHeadAsync(IDbConnection conn, IDbTransaction tx, string domain, string streamId, CancellationToken ct)
    {
        try
        {
            var tableName = ((IEventStoreOptions)Options).StreamsTableName;
            // Map RetentionMode and IsDeleted to a legacy "status" field for BaseEventStore compatibility
            // status = 0 means active (RetentionMode can be anything, IsDeleted = 0)
            // status != 0 means closed/deleted (IsDeleted = 1)
            var cmd = new CommandDefinition(
                $"SELECT last_version AS lastVersion, CASE WHEN IsDeleted = 1 THEN 1 ELSE 0 END AS status, last_position AS lastPosition FROM {tableName} WHERE domain = @Domain AND stream_id = @StreamId", 
                new { Domain = domain, StreamId = streamId }, 
                tx, 
                cancellationToken: ct);
            var head = await conn.QueryFirstOrDefaultAsync<(int lastVersion, int status, long lastPosition)>(cmd);
            if (head == default) return null;
            return head;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to read stream head for domain '{domain}' and streamId '{streamId}'", ex);
        }
    }

    protected override async Task<long?> InsertEventAndReturnGlobalPositionAsync(IDbConnection conn, IDbTransaction tx, EventData e, string domain, string streamId, int version, DateTime createdUtc, CancellationToken ct)
    {
        var tableName = ((IEventStoreOptions)Options).EventsTableName;
        var insertCmd = new CommandDefinition(
            $"INSERT INTO {tableName} (StreamId, StreamDomain, StreamVersion, StreamNamespace, EventType, Data, Metadata, CreatedUtc) VALUES (@StreamId, @Domain, @Version, @Namespace, @EventType, @Data, @Metadata, @CreatedUtc); SELECT last_insert_rowid();",
            new
            {
                StreamId = streamId,
                Domain = domain,
                Version = version,
                Namespace = e.Namespace,
                EventType = e.EventType,
                Data = e.Data,
                Metadata = e.Metadata,
                CreatedUtc = createdUtc.ToString("O")
            },
            tx,
            cancellationToken: ct);

        return await conn.ExecuteScalarAsync<long?>(insertCmd);
    }

    protected override async Task UpsertStreamHeadAsync(IDbConnection conn, IDbTransaction tx, string domain,
        string streamId, RetentionMode retentionMode, int lastVersion, long lastPosition, CancellationToken ct)
    {
        var tableName = ((IEventStoreOptions)Options).StreamsTableName;
        var upsertCmd = new CommandDefinition(
            $@"INSERT INTO {tableName} (domain, stream_id, last_version, last_position, archived_at, ArchiveCutoffVersion, RetentionMode, IsDeleted)
                  VALUES (@Domain, @StreamId, @LastVersion, @LastPosition, NULL, NULL, @RetentionMode, 0)
                  ON CONFLICT(domain, stream_id) DO UPDATE SET
                    last_version = excluded.last_version,
                    last_position = excluded.last_position;",
            new { Domain = domain, StreamId = streamId, LastVersion = lastVersion, LastPosition = lastPosition, RetentionMode = (int)retentionMode },
            tx,
            cancellationToken: ct);

        await conn.ExecuteAsync(upsertCmd);
    }

    protected override async Task<int?> ProbeLatestVersionAsync(IDbConnection conn, string domain, string streamId, CancellationToken ct)
    {
        var tableName = ((IEventStoreOptions)Options).EventsTableName;
        var probeVersion = await conn.ExecuteScalarAsync<int?>(new CommandDefinition($"SELECT MAX(StreamVersion) FROM {tableName} WHERE StreamId = @StreamId AND StreamDomain = @Domain", new { StreamId = streamId, Domain = domain }, cancellationToken: ct));
        return probeVersion;
    }

    protected override async Task<IReadOnlyList<EventEnvelope>> ReadStreamRowsAsync(IDbConnection conn, string domain, string streamId, string? nameSpace, StreamVersion fromVersionInclusive, int maxCount, CancellationToken ct)
    {
        var tableName = ((IEventStoreOptions)Options).EventsTableName;
        var nsClause = nameSpace is null ? string.Empty : "AND StreamNamespace = @Namespace";

        var cmd = new CommandDefinition(
            $@"SELECT GlobalPosition, StreamId, StreamDomain, StreamVersion, StreamNamespace, EventType, Data, Metadata, CreatedUtc
               FROM {tableName}
               WHERE StreamId = @StreamId AND StreamDomain = @Domain AND StreamVersion >= @FromVersion
               {nsClause}
               ORDER BY StreamVersion
               LIMIT @MaxCount",
            new
            {
                StreamId = streamId,
                Domain = domain,
                Namespace = nameSpace,
                FromVersion = fromVersionInclusive.Value,
                MaxCount = maxCount
            },
            cancellationToken: ct);

        var rows = await conn.QueryAsync<SqlEventRow>(cmd);
        return rows.Select(r => r.ToEnvelope()).ToArray();
    }

    protected override async Task<IReadOnlyList<EventEnvelope>> ReadAllForwardsRowsAsync(IDbConnection conn, string? domain, string? nameSpace, long positionExclusive, int batchSize, CancellationToken ct)
    {
        var tableName = ((IEventStoreOptions)Options).EventsTableName;
        var whereDomain = domain is null ? string.Empty : "AND (StreamDomain = @Domain)";
        var whereNs = nameSpace is null ? string.Empty : "AND StreamNamespace = @Namespace";

        var cmd = new CommandDefinition(
            $@"SELECT GlobalPosition, StreamId, StreamDomain, StreamVersion, StreamNamespace, EventType, Data, Metadata, CreatedUtc
                   FROM {tableName}
                   WHERE GlobalPosition > @Position
                   {whereDomain}
                   {whereNs}
                   ORDER BY GlobalPosition
                   LIMIT @BatchSize",
            new { Position = positionExclusive, BatchSize = batchSize, Domain = domain, Namespace = nameSpace },
            cancellationToken: ct);

        var rows = (await conn.QueryAsync<SqlEventRow>(cmd)).ToList();
        return rows.Select(r => r.ToEnvelope()).ToList();
    }

    protected override async Task<long?> SelectMinGlobalPositionAsync(IDbConnection conn, CancellationToken ct)
    {
        var tableName = ((IEventStoreOptions)Options).EventsTableName;
        var value = await conn.ExecuteScalarAsync<long?>(new CommandDefinition($"SELECT MIN(GlobalPosition) FROM {tableName}", cancellationToken: ct));
        return value;
    }

    protected override async Task<int?> SelectMaxStreamVersionAsync(IDbConnection conn, string domain, string streamId, CancellationToken ct)
    {
        var tableName = ((IEventStoreOptions)Options).EventsTableName;
        var value = await conn.ExecuteScalarAsync<int?>(new CommandDefinition($"SELECT MAX(StreamVersion) FROM {tableName} WHERE StreamId = @StreamId AND StreamDomain = @Domain", new { StreamId = streamId, Domain = domain }, cancellationToken: ct));
        return value;
    }

    protected override async Task<StreamHeader?> ReadStreamHeaderAsync(IDbConnection conn, string domain, string streamId, CancellationToken ct)
    {
        var tableName = ((IEventStoreOptions)Options).StreamsTableName;
        var cmd = new CommandDefinition(
            $@"SELECT domain, stream_id, last_version, last_position, archived_at, ArchiveCutoffVersion, RetentionMode, IsDeleted 
               FROM {tableName} 
               WHERE domain = @Domain AND stream_id = @StreamId",
            new { Domain = domain, StreamId = streamId },
            cancellationToken: ct);

        var row = await conn.QueryFirstOrDefaultAsync<StreamHeaderRow>(cmd);
        
        if (row == null)
            return null;

        return new StreamHeader(
            row.domain,
            row.stream_id,
            row.last_version,
            row.last_position,
            (RetentionMode)row.RetentionMode,
            row.IsDeleted != 0,
            string.IsNullOrEmpty(row.archived_at) ? null : DateTime.Parse(row.archived_at),
            row.ArchiveCutoffVersion);
    }

    private sealed class StreamHeaderRow
    {
        public string domain { get; set; } = default!;
        public string stream_id { get; set; } = default!;
        public int last_version { get; set; }
        public long last_position { get; set; }
        public string? archived_at { get; set; }
        public int? ArchiveCutoffVersion { get; set; }
        public int RetentionMode { get; set; }
        public int IsDeleted { get; set; }
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
}