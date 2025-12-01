using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using DRC.EventSourcing.Infrastructure;

namespace DRC.EventSourcing.SqlServer;

/// <summary>
/// Coordinates archiving events from the SQL Server hot store into
/// NDJSON files on disk and marking their ranges as archive segments.
/// </summary>
public sealed class SqlServerArchiveCoordinator<TStore> : BaseArchiveCoordinator<TStore>
    where TStore : SqlServerEventStoreOptions
{
    private readonly TStore _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public SqlServerArchiveCoordinator(
        SqlServerConnectionFactory<TStore> connectionFactory,
        TStore options)
        : base(connectionFactory, options)
    {
        _options = options;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// Retrieves streams from the Streams table that qualify for archiving or deletion.
    /// A stream is returned when:
    ///   • RetentionMode = ColdArchivable AND ArchiveCutoffVersion is not null AND IsDeleted = 0
    ///   • OR RetentionMode = FullHistory AND ArchiveCutoffVersion is not null AND IsDeleted = 0
    ///   • OR RetentionMode = HardDeletable AND IsDeleted = 1.
    /// </summary>
    protected override async IAsyncEnumerable<StreamHeader> QueryStreamHeadsAsync(
        IDbConnection conn,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (conn.State != ConnectionState.Open)
            conn.Open();

        // stream_id is UNIQUEIDENTIFIER in SQL Server; cast to NVARCHAR for StreamId string
        var sql = $@"
SELECT
    domain                               AS Domain,
    CAST(stream_id AS nvarchar(36))      AS StreamId,
    last_version                         AS LastVersion,
    last_position                        AS LastPosition,
    RetentionMode                        AS RetentionMode,
    ArchiveCutoffVersion                 AS ArchiveCutoffVersion,
    IsDeleted                            AS IsDeleted,
    archived_at                          AS ArchivedAt
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
    );";

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
                row.IsDeleted,
                row.ArchivedAt,
                row.ArchiveCutoffVersion);
        }
    }

    /// <summary>
    /// Archives events for a single stream up to its ArchiveCutoffVersion:
    ///   • Reads events from the hot Events table
    ///   • Writes them to an NDJSON file in the archive directory
    ///   • Records the segment in ArchiveSegments
    ///   • Deletes the archived events from the hot store
    /// </summary>
    protected override async Task ArchiveStreamAsync(
        IDbConnection conn,
        StreamHeader head,
        CancellationToken ct)
    {
        if (head.ArchiveCutoffVersion is null || head.ArchiveCutoffVersion <= 0)
            return;

        if (conn.State != ConnectionState.Open)
            conn.Open();

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
ORDER BY GlobalPosition;";

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

        using var tx = conn.BeginTransaction();

        try
        {
                // Check for existing overlapping segments BEFORE writing file to prevent orphans
                var checkSql = $@"
SELECT COUNT(1) FROM {((IEventStoreOptions)Options).ArchiveSegmentsTableName}
WHERE MinPosition <= @MaxPos AND MaxPosition >= @MinPos;";

            var overlapCount = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    checkSql,
                    new { MinPos = minPos, MaxPos = maxPos },
                    tx,
                    cancellationToken: ct));

            if (overlapCount > 0)
            {
                tx.Rollback();
                return; // Skip - segment already archived, no file written
            }

            // Now safe to write the file - we hold a transaction lock preventing concurrent archives
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

            // Write NDJSON file (temp + atomic rename handled by base)
            var finalPath = await WriteNdjsonAsync(lines, archiveDirectory, baseName, ct);
            var fileName = Path.GetFileName(finalPath);

            await InsertArchiveSegmentRecordAsync(conn, tx, minPos, maxPos, fileName, segmentNamespace, ct);

            // Delete archived events from the hot store
            var deleteSql = $@"
DELETE FROM {((IEventStoreOptions)_options).EventsTableName}
WHERE StreamDomain = @Domain
  AND StreamId = @StreamId
  AND GlobalPosition BETWEEN @MinPos AND @MaxPos;";

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

            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Archives events for FullHistory streams (same as ColdArchivable but keeps hot events).
    /// • Reads events up to ArchiveCutoffVersion from the hot Events table
    /// • Writes them to an NDJSON file in the archive directory
    /// • Records the segment in ArchiveSegments
    /// • DOES NOT delete events from the hot store (key difference from ColdArchivable)
    /// </summary>
    protected override async Task ArchiveWithoutDeletionAsync(
        IDbConnection conn,
        StreamHeader head,
        CancellationToken ct)
    {
        if (head.ArchiveCutoffVersion is null || head.ArchiveCutoffVersion <= 0)
            return;

            if (conn.State != ConnectionState.Open)
                conn.Open();

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
ORDER BY GlobalPosition;";

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

            using var tx = conn.BeginTransaction();

            try
            {
                // Check for existing overlapping segments BEFORE writing file to prevent orphans
                var checkSql = $@"
SELECT COUNT(1) FROM {((IEventStoreOptions)_options).ArchiveSegmentsTableName}
WHERE MinPosition <= @MaxPos AND MaxPosition >= @MinPos;";

                var overlapCount = await conn.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        checkSql,
                        new { MinPos = minPos, MaxPos = maxPos },
                        tx,
                        cancellationToken: ct));

                if (overlapCount > 0)
                {
                    tx.Rollback();
                    return; // Skip - segment already archived, no file written
                }

                // Now safe to write the file - we hold a transaction lock preventing concurrent archives
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

                // Write NDJSON file (temp + atomic rename handled by base)
                var finalPath = await WriteNdjsonAsync(lines, archiveDirectory, baseName, ct);
                var fileName = Path.GetFileName(finalPath);

                await InsertArchiveSegmentRecordAsync(conn, tx, minPos, maxPos, fileName, segmentNamespace, ct);

                // NOTE: Key difference from ArchiveStreamAsync - we DO NOT delete events from hot store
                // This allows FullHistory streams to maintain both hot and cold copies

                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

    /// <summary>
    /// Hard delete for streams with RetentionMode.HardDeletable and IsDeleted = true:
    ///   • Delete all events for the stream
    ///   • Delete the stream header
    /// </summary>
    protected override async Task DeleteStreamAsync(
        IDbConnection conn,
        StreamHeader head,
        CancellationToken ct)
    {
        if (conn.State != ConnectionState.Open)
            conn.Open();

        using var tx = conn.BeginTransaction();

        var deleteEventsSql = $@"
DELETE FROM {((IEventStoreOptions)_options).EventsTableName}
WHERE StreamDomain = @Domain
  AND StreamId = @StreamId;";

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
  AND stream_id = @StreamId;";

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

        tx.Commit();
    }

    /// <summary>
    /// Inserts an archive segment record in the "{StoreName}_ArchiveSegments" table.
    /// Typically called from within ArchiveStream* methods while holding a transaction.
    /// </summary>
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
VALUES
    (@MinPosition, @MaxPosition, @FileName, 1, @StreamNamespace);";

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

    #region Local DTOs

    private sealed class StreamHeadRow
    {
        public string Domain { get; set; } = default!;
        public string StreamId { get; set; } = default!;
        public int LastVersion { get; set; }
        public long LastPosition { get; set; }
        public int RetentionMode { get; set; }
        public int? ArchiveCutoffVersion { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime? ArchivedAt { get; set; }
    }

    private sealed class SqlEventRow
    {
        public long GlobalPosition { get; set; }
        public string StreamId { get; set; } = default!;
        public int StreamVersion { get; set; }
        public string? StreamNamespace { get; set; }
        public string EventType { get; set; } = default!;
        public byte[]? Data { get; set; }
        public byte[]? Metadata { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    private sealed class ArchivedEventJson
    {
        public long GlobalPosition { get; set; }
        public string StreamId { get; set; } = default!;
        public int StreamVersion { get; set; }
        public string? StreamNamespace { get; set; }
        public string EventType { get; set; } = default!;
        public string CreatedUtc { get; set; } = default!;
        public string? Data { get; set; }
        public string? Metadata { get; set; }
    }

    #endregion
}
