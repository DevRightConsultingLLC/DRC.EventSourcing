using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using DRC.EventSourcing.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DRC.EventSourcing.Sqlite
{
    /// <summary>
    /// SQLite archive coordinator that:
    ///   - Enumerates candidate streams from the Streams table
    ///   - Archives events to NDJSON files and records ArchiveSegments
    ///   - Deletes hot events (and, for hard-deletable streams, entire streams)
    /// </summary>
    public sealed class SqliteArchiveCoordinator<TStore> : BaseArchiveCoordinator<TStore>
        where TStore : SqliteEventStoreOptions
    {
        private readonly JsonSerializerOptions _jsonOptions;

        public SqliteArchiveCoordinator(
            SqliteConnectionFactory<TStore> connectionFactory,
            TStore options)
            : base(connectionFactory, options)
        {
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }

        /// <summary>
        /// Retrieves streams from the Streams table that qualify for archiving or deletion.
        /// A stream is returned when:
        ///   • RetentionMode = ColdArchivable and ArchiveCutoffVersion is not null and IsDeleted = 0
        ///   • OR RetentionMode = FullHistory and ArchiveCutoffVersion is not null and IsDeleted = 0
        ///   • OR RetentionMode = HardDeletable and IsDeleted = 1.
        /// </summary>
        protected override async IAsyncEnumerable<StreamHeader> QueryStreamHeadsAsync(
            IDbConnection conn,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var sqliteConn = (SqliteConnection)conn;

            if (sqliteConn.State != ConnectionState.Open)
                await sqliteConn.OpenAsync(ct);

            var sql = $@"
SELECT
    domain               AS Domain,
    stream_id            AS StreamId,
    last_version         AS LastVersion,
    last_position        AS LastPosition,
    RetentionMode        AS RetentionMode,
    ArchiveCutoffVersion AS ArchiveCutoffVersion,
    IsDeleted            AS IsDeleted,
    archived_at          AS ArchivedAt
FROM {((IEventStoreOptions)Options).StreamsTableName}
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

            var rows = await sqliteConn.QueryAsync<StreamHeadRow>(
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
                    string.IsNullOrEmpty(row.ArchivedAt) ? null : DateTime.Parse(row.ArchivedAt),
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

            var sqliteConn = (SqliteConnection)conn;

            if (sqliteConn.State != ConnectionState.Open)
                await sqliteConn.OpenAsync(ct);

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
FROM {((IEventStoreOptions)Options).EventsTableName}
WHERE StreamDomain = @Domain
  AND StreamId = @StreamId
  AND StreamVersion <= @CutoffVersion
ORDER BY GlobalPosition;";

            var events = (await sqliteConn.QueryAsync<SqlEventRow>(
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

            await using var tx = await sqliteConn.BeginTransactionAsync(ct);

            try
            {
                // Check for existing overlapping segments BEFORE writing file to prevent orphans
                var checkSql = $@"
SELECT COUNT(1) FROM {((IEventStoreOptions)Options).ArchiveSegmentsTableName}
WHERE MinPosition <= @MaxPos AND MaxPosition >= @MinPos;";

                var overlapCount = await sqliteConn.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        checkSql,
                        new { MinPos = minPos, MaxPos = maxPos },
                        tx,
                        cancellationToken: ct));

                if (overlapCount > 0)
                {
                    await tx.RollbackAsync(ct);
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
                var fileName = System.IO.Path.GetFileName(finalPath);

                await InsertArchiveSegmentRecordAsync(sqliteConn, tx, minPos, maxPos, fileName, segmentNamespace, ct);

                // Delete archived events from hot store
                var deleteSql = $@"
DELETE FROM {((IEventStoreOptions)Options).EventsTableName}
WHERE StreamDomain = @Domain
  AND StreamId = @StreamId
  AND GlobalPosition BETWEEN @MinPos AND @MaxPos;";

                await sqliteConn.ExecuteAsync(
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
                try { await tx.RollbackAsync(ct); } catch { }
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

            var sqliteConn = (SqliteConnection)conn;

            if (sqliteConn.State != ConnectionState.Open)
                await sqliteConn.OpenAsync(ct);

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
FROM {((IEventStoreOptions)Options).EventsTableName}
WHERE StreamDomain = @Domain
  AND StreamId = @StreamId
  AND StreamVersion <= @CutoffVersion
ORDER BY GlobalPosition;";

            var events = (await sqliteConn.QueryAsync<SqlEventRow>(
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

            await using var tx = await sqliteConn.BeginTransactionAsync(ct);

            try
            {
                // Check for existing overlapping segments BEFORE writing file to prevent orphans
                var checkSql = $@"
SELECT COUNT(1) FROM {((IEventStoreOptions)Options).ArchiveSegmentsTableName}
WHERE MinPosition <= @MaxPos AND MaxPosition >= @MinPos;";

                var overlapCount = await sqliteConn.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        checkSql,
                        new { MinPos = minPos, MaxPos = maxPos },
                        tx,
                        cancellationToken: ct));

                if (overlapCount > 0)
                {
                    await tx.RollbackAsync(ct);
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

                await InsertArchiveSegmentRecordAsync(sqliteConn, tx, minPos, maxPos, fileName, segmentNamespace, ct);

                // NOTE: Key difference from ArchiveStreamAsync - we DO NOT delete events from hot store
                // This allows FullHistory streams to maintain both hot and cold copies

                await tx.CommitAsync(ct);
            }
            catch
            {
                try { await tx.RollbackAsync(ct); } catch { }
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
            var sqliteConn = (SqliteConnection)conn;

            if (sqliteConn.State != ConnectionState.Open)
                await sqliteConn.OpenAsync(ct);

            await using var tx = await sqliteConn.BeginTransactionAsync(ct);

            var deleteEventsSql = $@"
DELETE FROM {((IEventStoreOptions)Options).EventsTableName}
WHERE StreamDomain = @Domain
  AND StreamId = @StreamId;";

            await sqliteConn.ExecuteAsync(
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
DELETE FROM {((IEventStoreOptions)Options).StreamsTableName}
WHERE domain = @Domain
  AND stream_id = @StreamId;";

            await sqliteConn.ExecuteAsync(
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

        /// <summary>
        /// Inserts an archive segment record in the ArchiveSegments table.
        /// Called from ArchiveStreamAsync / BackupStreamAsync while holding a transaction.
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
            var sqliteConn = (SqliteConnection)conn;

            var insertSql = $@"
INSERT INTO {((IEventStoreOptions)Options).ArchiveSegmentsTableName}
    (MinPosition, MaxPosition, FileName, Status, StreamNamespace)
VALUES
    (@MinPosition, @MaxPosition, @FileName, 1, @StreamNamespace);";

            await sqliteConn.ExecuteAsync(
                new CommandDefinition(
                    insertSql,
                    new
                    {
                        MinPosition = minPos,
                        MaxPosition = maxPos,
                        FileName = filename,
                        StreamNamespace = segmentNamespace
                    },
                    (SqliteTransaction)tx,
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
            public string? ArchivedAt { get; set; }
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
}
