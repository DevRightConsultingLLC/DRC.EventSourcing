using Dapper;
using System.Data;
using DRC.EventSourcing.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DRC.EventSourcing.SqlServer;

/// <summary>
/// SQL Server-specific implementation of the event store.
/// </summary>
/// <typeparam name="TStore">The SQL Server options type</typeparam>
/// <remarks>
/// <para>SQL Server-specific features and optimizations:</para>
/// <list type="bullet">
///   <item>Uses IDENTITY columns for GlobalPosition (guaranteed monotonic increase)</item>
///   <item>MERGE statements for atomic upsert operations</item>
///   <item>UPDLOCK and ROWLOCK hints for optimistic concurrency with minimal blocking</item>
///   <item>Schema support for logical separation of event stores</item>
///   <item>Native DateTime2 support for high-precision timestamps</item>
/// </list>
/// <para>Performance characteristics:</para>
/// <list type="bullet">
///   <item>Append: 2-5ms for small batches (network latency dependent)</item>
///   <item>Read: Sub-millisecond to milliseconds depending on index usage</item>
///   <item>Suitable for: Multi-server applications, high-volume scenarios, enterprise deployments</item>
/// </list>
/// <para>Recommended indexes for optimal performance:</para>
/// <list type="bullet">
///   <item>Events: (StreamDomain, StreamId, StreamVersion)</item>
///   <item>Events: (GlobalPosition) for forward reads</item>
///   <item>Streams: (domain, stream_id) unique clustered</item>
/// </list>
/// </remarks>
public sealed class SqlServerEventStore<TStore> : BaseEventStore<TStore> where TStore: SqlServerEventStoreOptions
{
    private readonly SqlServerConnectionFactory<TStore> _connectionFactory;
    private readonly TStore _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlServerEventStore{TStore}"/> class.
    /// </summary>
    /// <param name="connectionFactory">Factory for creating SQL Server connections</param>
    /// <param name="options">SQL Server-specific configuration options</param>
    /// <param name="policyProvider">Domain retention policy provider</param>
    /// <param name="logger">Logger instance for diagnostic logging</param>
    /// <param name="metrics">Optional metrics collector for observability</param>
    public SqlServerEventStore(
        SqlServerConnectionFactory<TStore> connectionFactory, 
        TStore options,
        IDomainRetentionPolicyProvider policyProvider,
        ILogger<SqlServerEventStore<TStore>> logger,
        EventStoreMetrics? metrics = null) 
        : base(connectionFactory, options, policyProvider, logger, metrics)
    {
        _connectionFactory = connectionFactory;
        _options = options;
    }

    protected override async Task<(int lastVersion, int status, long lastPosition)?> ReadStreamHeadAsync(IDbConnection conn, IDbTransaction tx, string domain, string streamId, CancellationToken ct)
    {
        try
        {
            // Map RetentionMode and IsDeleted to a legacy "status" field for BaseEventStore compatibility
            // status = 0 means active (RetentionMode can be anything, IsDeleted = 0)
            // status != 0 means closed/deleted (IsDeleted = 1)
            var headCmd = new CommandDefinition(
                $@"SELECT TOP (1) 
                    last_version AS lastVersion, 
                    CASE WHEN IsDeleted = 1 THEN 1 ELSE 0 END AS status, 
                    last_position AS lastPosition 
                   FROM {((IEventStoreOptions)_options).StreamsTableName} WITH (UPDLOCK, ROWLOCK) 
                   WHERE domain = @Domain AND stream_id = @StreamId",
                new { Domain = domain, StreamId = streamId }, tx, cancellationToken: ct);
            var head = await conn.QueryFirstOrDefaultAsync(headCmd);

            if (head is null) return null;

            int lastVersion = (int)head.lastVersion;
            int status = (int)head.status;
            long lastPosition = (long)head.lastPosition;
            return (lastVersion, status, lastPosition);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to read stream head for domain '{domain}' and streamId '{streamId}'", ex);
        }
    }

    protected override async Task<long?> InsertEventAndReturnGlobalPositionAsync(IDbConnection conn, IDbTransaction tx, EventData e, string domain, string streamId, int version, DateTime createdUtc, CancellationToken ct)
    {
        var insertCmd = new CommandDefinition(
            $@"INSERT INTO {((IEventStoreOptions)_options).EventsTableName} (StreamId, StreamDomain, StreamVersion, StreamNamespace, EventType, Data, Metadata, CreatedUtc)
                       OUTPUT inserted.GlobalPosition
                       VALUES (@StreamId, @Domain, @Version, @Namespace, @EventType, @Data, @Metadata, @CreatedUtc)",
            new
            {
                StreamId = streamId,
                Domain = domain,
                Version = version,
                Namespace = e.Namespace,
                EventType = e.EventType,
                Data = e.Data,
                Metadata = e.Metadata,
                CreatedUtc = createdUtc
            },
            tx,
            cancellationToken: ct);

        var gp = await conn.ExecuteScalarAsync<long?>(insertCmd);
        return gp;
    }

    protected override async Task UpsertStreamHeadAsync(IDbConnection conn, IDbTransaction tx, string domain, string streamId, RetentionMode retentionMode, int lastVersion, long lastPosition, CancellationToken ct)
    {
        var mergeCmd = new CommandDefinition($@"
MERGE {((IEventStoreOptions)_options).StreamsTableName} AS target
USING (SELECT @Domain AS domain, @StreamId AS stream_id, @LastVersion AS last_version, @LastPosition AS last_position, @RetentionMode AS retention_mode) AS src
    ON (target.domain = src.domain AND target.stream_id = src.stream_id)
WHEN MATCHED THEN
    UPDATE SET
        last_version = src.last_version,
        last_position = src.last_position
WHEN NOT MATCHED THEN
    INSERT (domain, stream_id, last_version, last_position, archived_at, ArchiveCutoffVersion, RetentionMode, IsDeleted)
    VALUES (src.domain, src.stream_id, src.last_version, src.last_position, NULL, NULL, src.retention_mode, 0);
", new { Domain = domain, StreamId = streamId, LastVersion = lastVersion, LastPosition = lastPosition, RetentionMode = (int)retentionMode }, tx, cancellationToken: ct);

        await conn.ExecuteAsync(mergeCmd);
    }

    protected override async Task<int?> ProbeLatestVersionAsync(IDbConnection conn, string domain, string streamId, CancellationToken ct)
    {
        var probeVersion = await conn.ExecuteScalarAsync<int?>(new CommandDefinition($"SELECT MAX(StreamVersion) FROM {((IEventStoreOptions)_options).EventsTableName} WHERE StreamId = @StreamId AND StreamDomain = @Domain", new { StreamId = streamId, Domain = domain }, cancellationToken: ct));
        return probeVersion;
    }

    protected override async Task<IReadOnlyList<EventEnvelope>> ReadStreamRowsAsync(IDbConnection conn, string domain, string streamId, string? nameSpace, StreamVersion fromVersionInclusive, int maxCount, CancellationToken ct)
    {
        var nsClause = nameSpace is null ? string.Empty : "AND StreamNamespace = @Namespace";

        var cmd = new CommandDefinition(
            $@"SELECT TOP (@MaxCount)
                     GlobalPosition,
                     StreamId,
                     StreamDomain,
                     StreamVersion,
                     StreamNamespace,
                     EventType,
                     Data,
                     Metadata,
                     CreatedUtc
              FROM {((IEventStoreOptions)_options).EventsTableName}
              WHERE StreamId = @StreamId AND StreamDomain = @Domain AND StreamVersion >= @FromVersion
              {nsClause}
              ORDER BY StreamVersion",
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
        var whereDomain = domain is null ? string.Empty : "AND (StreamDomain = @Domain)";
        var whereNs = nameSpace is null ? string.Empty : "AND StreamNamespace = @Namespace";

        var cmd = new CommandDefinition(
            $@"SELECT TOP (@BatchSize)
                        GlobalPosition,
                        StreamId,
                        StreamDomain,
                        StreamVersion,
                        StreamNamespace,
                        EventType,
                        Data,
                        Metadata,
                        CreatedUtc
                  FROM {((IEventStoreOptions)_options).EventsTableName}
                  WHERE GlobalPosition > @Position
                  {whereDomain}
                  {whereNs}
                  ORDER BY GlobalPosition",
            new
            {
                BatchSize = batchSize,
                Position = positionExclusive,
                Domain = domain,
                Namespace = nameSpace
            },
            cancellationToken: ct);

        var rows = (await conn.QueryAsync<SqlEventRow>(cmd)).ToList();
        return rows.Select(r => r.ToEnvelope()).ToList();
    }

    protected override async Task<long?> SelectMinGlobalPositionAsync(IDbConnection conn, CancellationToken ct)
    {
        var value = await conn.ExecuteScalarAsync<long?>(new CommandDefinition($"SELECT MIN(GlobalPosition) FROM {((IEventStoreOptions)_options).EventsTableName}", cancellationToken: ct));
        return value;
    }

    protected override async Task<int?> SelectMaxStreamVersionAsync(IDbConnection conn, string domain, string streamId, CancellationToken ct)
    {
        var value = await conn.ExecuteScalarAsync<int?>(new CommandDefinition($"SELECT MAX(StreamVersion) FROM {((IEventStoreOptions)_options).EventsTableName} WHERE StreamId = @StreamId AND StreamDomain = @Domain", new { StreamId = streamId, Domain = domain }, cancellationToken: ct));
        return value;
    }

    protected override async Task<StreamHeader?> ReadStreamHeaderAsync(IDbConnection conn, string domain, string streamId, CancellationToken ct)
    {
        var cmd = new CommandDefinition(
            $@"SELECT domain, stream_id, last_version, last_position, archived_at, ArchiveCutoffVersion, RetentionMode, IsDeleted 
               FROM {((IEventStoreOptions)_options).StreamsTableName} 
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
            row.IsDeleted,
            row.archived_at,
            row.ArchiveCutoffVersion);
    }

    private sealed class StreamHeaderRow
    {
        public string domain { get; set; } = default!;
        public string stream_id { get; set; } = default!;
        public int last_version { get; set; }
        public long last_position { get; set; }
        public DateTime? archived_at { get; set; }
        public int? ArchiveCutoffVersion { get; set; }
        public short RetentionMode { get; set; }
        public bool IsDeleted { get; set; }
    }

    private sealed class SqlEventRow
    {
        public long GlobalPosition { get; set; }
        public string StreamId { get; set; } = default!;
        public string StreamDomain { get; set; } = default!;
        public int StreamVersion { get; set; }
        public string StreamNamespace { get; set; } = default!;
        public string EventType { get; set; } = default!;
        public byte[]? Data { get; set; }
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
                CreatedUtc
            );
    }
}
