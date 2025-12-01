using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using DRC.EventSourcing.Infrastructure;
using MySqlConnector;

namespace DRC.EventSourcing.MySql;

/// <summary>
/// Coordinates archiving events from the MySQL hot store into
/// NDJSON files on disk and marking their ranges as archive segments.
/// </summary>
public sealed class MySqlArchiveCoordinator<TStore> : BaseArchiveCoordinator<TStore>
    where TStore : MySqlEventStoreOptions
{
    private readonly TStore _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public MySqlArchiveCoordinator(
        MySqlConnectionFactory<TStore> connectionFactory,
        TStore options)
        : base(connectionFactory, options)
    {
        _options = options;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    protected override async IAsyncEnumerable<StreamHeader> QueryStreamHeadsAsync(
        IDbConnection conn,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var mysqlConn = (MySqlConnection)conn;
        if (mysqlConn.State != ConnectionState.Open)
            await mysqlConn.OpenAsync(ct);

        var sql = $@"
SELECT
    domain AS Domain,
    stream_id AS StreamId,
    last_version AS LastVersion,
    last_position AS LastPosition,
    RetentionMode AS RetentionMode,
    ArchiveCutoffVersion AS ArchiveCutoffVersion,
    IsDeleted AS IsDeleted,
    archived_at AS ArchivedAt
FROM {((IEventStoreOptions)_options).StreamsTableName}
WHERE
    (
        RetentionMode IN (@ColdArchivable, @FullHistory)
        AND ArchiveCutoffVersion IS NOT NULL
        AND IsDeleted = 0
    )
    OR
    (
        RetentionMode = @HardDeletable
        AND IsDeleted = 1
    )";

        var rows = await conn.QueryAsync<StreamHeadRow>(
            new CommandDefinition(
                sql,
                new
                {
                    ColdArchivable = (int)RetentionMode.ColdArchivable,
                    FullHistory = (int)RetentionMode.FullHistory,
                    HardDeletable = (int)RetentionMode.HardDeletable
                },
                cancellationToken: ct));

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            yield return new StreamHeader(
                row.Domain,
                row.StreamId,
                row.LastVersion,
                row.LastPosition,
                (RetentionMode)row.RetentionMode,
                Convert.ToBoolean(row.IsDeleted),
                row.ArchivedAt,
                row.ArchiveCutoffVersion);
        }
    }

    protected override async Task ArchiveStreamAsync(
        IDbConnection conn,
        StreamHeader head,
        CancellationToken ct)
    {
        if (head.ArchiveCutoffVersion is null || head.ArchiveCutoffVersion <= 0)
            return;

        var mysqlConn = (MySqlConnection)conn;
        if (mysqlConn.State != ConnectionState.Open)
            await mysqlConn.OpenAsync(ct);

        var cutoffVersion = head.ArchiveCutoffVersion.Value;

        var selectSql = $@"
SELECT
    GlobalPosition,
    StreamId,
    StreamVersion,
    StreamNamespace,
    EventType,
    Data,
    Metadata,
    CreatedUtc
FROM {((IEventStoreOptions)_options).EventsTableName}
WHERE StreamDomain = @Domain
  AND StreamId = @StreamId
  AND StreamVersion <= @CutoffVersion
ORDER BY GlobalPosition";

        var events = (await conn.QueryAsync<SqlEventRow>(
            new CommandDefinition(
                selectSql,
                new
                {
                    Domain = head.Domain,
                    StreamId = head.StreamId,
                    CutoffVersion = cutoffVersion
                },
                cancellationToken: ct))).ToList();

        if (events.Count == 0)
            return;

        var minPos = events.First().GlobalPosition;
        var maxPos = events.Last().GlobalPosition;
        var segmentNamespace = events.First().StreamNamespace;

        await using var tx = await mysqlConn.BeginTransactionAsync(ct);

        try
        {
            var checkSql = $@"
SELECT COUNT(1) FROM {((IEventStoreOptions)_options).ArchiveSegmentsTableName}
WHERE MinPosition <= @MaxPos AND MaxPosition >= @MinPos";

            var overlapCount = await conn.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    checkSql,
                    new { MinPos = minPos, MaxPos = maxPos },
                    tx,
                    cancellationToken: ct));

            if (overlapCount > 0)
            {
                await tx.RollbackAsync(ct);
                return;
            }

            var archiveDirectory = GetArchiveDirectory();
            var baseName = $"events-{minPos:D16}-{maxPos:D16}";
            var lines = events.Select(e =>
            {
                var dto = new ArchivedEventJson
                {
                    GlobalPosition = e.GlobalPosition,
                    StreamId = e.StreamId,
                    StreamVersion = e.StreamVersion,
                    StreamNamespace = e.StreamNamespace,
                    EventType = e.EventType,
                    CreatedUtc = e.CreatedUtc.ToString("O"),
                    Data = e.Data is null ? null : Convert.ToBase64String(e.Data),
                    Metadata = e.Metadata is null ? null : Convert.ToBase64String(e.Metadata)
                };

                return JsonSerializer.Serialize(dto, _jsonOptions);
            });

            var finalPath = await WriteNdjsonAsync(lines, archiveDirectory, baseName, ct);
            var fileName = Path.GetFileName(finalPath);

            await InsertArchiveSegmentRecordAsync(conn, tx, minPos, maxPos, fileName, segmentNamespace, ct);

            var deleteSql = $@"
DELETE FROM {((IEventStoreOptions)_options).EventsTableName}
WHERE StreamDomain = @Domain
  AND StreamId = @StreamId
  AND GlobalPosition BETWEEN @MinPos AND @MaxPos";

            await conn.ExecuteAsync(
                new CommandDefinition(
                    deleteSql,
                    new
                    {
                        Domain = head.Domain,
                        StreamId = head.StreamId,
                        MinPos = minPos,
                        MaxPos = maxPos
                    },
                    tx,
                    cancellationToken: ct));

            await tx.CommitAsync(ct);
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { /* ignore */ }
            throw;
        }
    }

    protected override async Task ArchiveWithoutDeletionAsync(
        IDbConnection conn,
        StreamHeader head,
        CancellationToken ct)
    {
        if (head.ArchiveCutoffVersion is null || head.ArchiveCutoffVersion <= 0)
            return;

        var mysqlConn = (MySqlConnection)conn;
        if (mysqlConn.State != ConnectionState.Open)
            await mysqlConn.OpenAsync(ct);

        var cutoffVersion = head.ArchiveCutoffVersion.Value;

        var selectSql = $@"
SELECT
    GlobalPosition,
    StreamId,
    StreamVersion,
    StreamNamespace,
    EventType,
    Data,
    Metadata,
    CreatedUtc
FROM {((IEventStoreOptions)_options).EventsTableName}
WHERE StreamDomain = @Domain
  AND StreamId = @StreamId
  AND StreamVersion <= @CutoffVersion
ORDER BY GlobalPosition";

        var events = (await conn.QueryAsync<SqlEventRow>(
            new CommandDefinition(
                selectSql,
                new
                {
                    Domain = head.Domain,
                    StreamId = head.StreamId,
                    CutoffVersion = cutoffVersion
                },
                cancellationToken: ct))).ToList();

        if (events.Count == 0)
            return;

        var minPos = events.First().GlobalPosition;
        var maxPos = events.Last().GlobalPosition;
        var segmentNamespace = events.First().StreamNamespace;

        await using var tx = await mysqlConn.BeginTransactionAsync(ct);

        try
        {
            var checkSql = $@"
SELECT COUNT(1) FROM {((IEventStoreOptions)_options).ArchiveSegmentsTableName}
WHERE MinPosition <= @MaxPos AND MaxPosition >= @MinPos";

            var overlapCount = await conn.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    checkSql,
                    new { MinPos = minPos, MaxPos = maxPos },
                    tx,
                    cancellationToken: ct));

            if (overlapCount > 0)
            {
                await tx.RollbackAsync(ct);
                return;
            }

            var archiveDirectory = GetArchiveDirectory();
            var baseName = $"events-{minPos:D16}-{maxPos:D16}";
            var lines = events.Select(e =>
            {
                var dto = new ArchivedEventJson
                {
                    GlobalPosition = e.GlobalPosition,
                    StreamId = e.StreamId,
                    StreamVersion = e.StreamVersion,
                    StreamNamespace = e.StreamNamespace,
                    EventType = e.EventType,
                    CreatedUtc = e.CreatedUtc.ToString("O"),
                    Data = e.Data is null ? null : Convert.ToBase64String(e.Data),
                    Metadata = e.Metadata is null ? null : Convert.ToBase64String(e.Metadata)
                };

                return JsonSerializer.Serialize(dto, _jsonOptions);
            });

            var finalPath = await WriteNdjsonAsync(lines, archiveDirectory, baseName, ct);
            var fileName = Path.GetFileName(finalPath);

            await InsertArchiveSegmentRecordAsync(conn, tx, minPos, maxPos, fileName, segmentNamespace, ct);

            // NOTE: Key difference - we DO NOT delete events from hot store

            await tx.CommitAsync(ct);
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { /* ignore */ }
            throw;
        }
    }

    protected override async Task DeleteStreamAsync(
        IDbConnection conn,
        StreamHeader head,
        CancellationToken ct)
    {
        var mysqlConn = (MySqlConnection)conn;
        if (mysqlConn.State != ConnectionState.Open)
            await mysqlConn.OpenAsync(ct);

        await using var tx = await mysqlConn.BeginTransactionAsync(ct);

        var deleteEventsSql = $@"
DELETE FROM {((IEventStoreOptions)_options).EventsTableName}
WHERE StreamDomain = @Domain
  AND StreamId = @StreamId";

        await conn.ExecuteAsync(
            new CommandDefinition(
                deleteEventsSql,
                new
                {
                    Domain = head.Domain,
                    StreamId = head.StreamId
                },
                tx,
                cancellationToken: ct));

        var deleteStreamSql = $@"
DELETE FROM {((IEventStoreOptions)_options).StreamsTableName}
WHERE domain = @Domain
  AND stream_id = @StreamId";

        await conn.ExecuteAsync(
            new CommandDefinition(
                deleteStreamSql,
                new
                {
                    Domain = head.Domain,
                    StreamId = head.StreamId
                },
                tx,
                cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    protected override async Task InsertArchiveSegmentRecordAsync(
        IDbConnection conn,
        IDbTransaction tx,
        long minPos,
        long maxPos,
        string filename,
        string? segmentNamespace,
        CancellationToken ct)
    {
        var insertSql = $@"
INSERT INTO {((IEventStoreOptions)_options).ArchiveSegmentsTableName}
(MinPosition, MaxPosition, FileName, Status, StreamNamespace)
VALUES (@MinPosition, @MaxPosition, @FileName, 1, @StreamNamespace)";

        await conn.ExecuteAsync(
            new CommandDefinition(
                insertSql,
                new
                {
                    MinPosition = minPos,
                    MaxPosition = maxPos,
                    FileName = filename,
                    StreamNamespace = segmentNamespace
                },
                tx,
                cancellationToken: ct));
    }

    private record ArchivedEventJson
    {
        public long GlobalPosition { get; init; }
        public string StreamId { get; init; } = default!;
        public int StreamVersion { get; init; }
        public string StreamNamespace { get; init; } = default!;
        public string EventType { get; init; } = default!;
        public string CreatedUtc { get; init; } = default!;
        public string? Data { get; init; }
        public string? Metadata { get; init; }
    }

    private record SqlEventRow
    {
        public long GlobalPosition { get; init; }
        public string StreamId { get; init; } = default!;
        public int StreamVersion { get; init; }
        public string StreamNamespace { get; init; } = default!;
        public string EventType { get; init; } = default!;
        public byte[] Data { get; init; } = default!;
        public byte[]? Metadata { get; init; }
        public DateTime CreatedUtc { get; init; }
    }

    private record StreamHeadRow
    {
        public string Domain { get; init; } = default!;
        public string StreamId { get; init; } = default!;
        public int LastVersion { get; init; }
        public long LastPosition { get; init; }
        public int RetentionMode { get; init; }
        public int? ArchiveCutoffVersion { get; init; }
        public sbyte IsDeleted { get; init; }
        public DateTime? ArchivedAt { get; init; }
    }
}

