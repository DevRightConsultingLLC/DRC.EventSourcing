using Microsoft.Data.Sqlite;
using DRC.EventSourcing.Infrastructure;
using System.Data;

namespace DRC.EventSourcing.Sqlite;

public sealed class SqliteSchemaInitializer<TStore> 
    : BaseSchemaInitializer<TStore>, IEventStoreSchemaInitializer<TStore>
    where TStore : SqliteEventStoreOptions
{
    private readonly TStore _options;

    public SqliteSchemaInitializer(SqliteConnectionFactory<TStore> factory, TStore options)
        : base(factory, options)
    {
        _options = options;
    }

    protected override string GenerateCreateTablesSql()
    {
        return $@"
CREATE TABLE IF NOT EXISTS {((IEventStoreOptions)_options).EventsTableName} (
    GlobalPosition   INTEGER PRIMARY KEY AUTOINCREMENT,
    StreamId         TEXT NOT NULL,
    StreamDomain     TEXT NOT NULL,
    StreamVersion    INTEGER NOT NULL,
    StreamNamespace  TEXT NOT NULL,
    EventType        TEXT NOT NULL,
    Data             BLOB NOT NULL,
    Metadata         BLOB NULL,
    CreatedUtc       TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_{_options.StoreName}_Events_StreamDomain_StreamId_StreamVersion
    ON {((IEventStoreOptions)_options).EventsTableName} (StreamDomain, StreamId, StreamVersion);
CREATE INDEX IF NOT EXISTS IX_{_options.StoreName}_Events_StreamId
    ON {((IEventStoreOptions)_options).EventsTableName} (StreamId);
CREATE INDEX IF NOT EXISTS IX_{_options.StoreName}_Events_StreamNamespace
    ON {((IEventStoreOptions)_options).EventsTableName} (StreamNamespace);
CREATE INDEX IF NOT EXISTS IX_{_options.StoreName}_Events_StreamDomain
    ON {((IEventStoreOptions)_options).EventsTableName} (StreamDomain);

CREATE TABLE IF NOT EXISTS {((IEventStoreOptions)_options).StreamsTableName} (
    domain               TEXT    NOT NULL,
    stream_id            TEXT    NOT NULL,
    last_version         INTEGER NOT NULL,
    last_position        INTEGER NOT NULL,
    archived_at          TEXT    NULL,
    ArchiveCutoffVersion INTEGER NULL,
    RetentionMode        INTEGER NOT NULL,
    IsDeleted            INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (domain, stream_id)
);
-- Composite index to support archive/delete selection:
--   • Archive:  RetentionMode = ColdArchivable, IsDeleted = 0, ArchiveCutoffVersion < @cutoff
--   • Delete:   RetentionMode = HardDeletable, IsDeleted = 1
CREATE INDEX IF NOT EXISTS IX_{_options.StoreName}_Streams_Retention_IsDeleted_Cutoff
    ON {((IEventStoreOptions)_options).StreamsTableName} (RetentionMode, IsDeleted, ArchiveCutoffVersion);

CREATE TABLE IF NOT EXISTS {((IEventStoreOptions)_options).SnapshotsTableName} (
    StreamId       TEXT NOT NULL PRIMARY KEY,
    StreamVersion  INTEGER NOT NULL,
    Data           BLOB NOT NULL,
    CreatedUtc     TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS {((IEventStoreOptions)_options).ArchiveSegmentsTableName} (
    SegmentId       INTEGER PRIMARY KEY AUTOINCREMENT,
    MinPosition     INTEGER NOT NULL,
    MaxPosition     INTEGER NOT NULL,
    FileName        TEXT NOT NULL,
    Status          INTEGER NOT NULL,
    StreamNamespace TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_{_options.StoreName}_ArchiveSegments_Range
    ON {((IEventStoreOptions)_options).ArchiveSegmentsTableName} (MinPosition, MaxPosition);
";
    }

    // Greenfield: schema is created correctly up front, so we don't need
    // to dynamically add columns. We just implement the override as a no-op.
    protected override Task EnsureSchemaColumnsAsync(IDbConnection conn, TStore options, CancellationToken ct)
    {
        // No additional schema adjustments required for SQLite in this greenfield setup.
        return Task.CompletedTask;
    }
}
