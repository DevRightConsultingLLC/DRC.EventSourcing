using DRC.EventSourcing.Infrastructure;

namespace DRC.EventSourcing.PostgreSQL;

/// <summary>
/// Creates the per-store PostgreSQL tables if they do not already exist.
/// </summary>
public sealed class PostgreSQLSchemaInitializer<TStore>
    : BaseSchemaInitializer<TStore>, IEventStoreSchemaInitializer<TStore>
    where TStore : PostgreSQLEventStoreOptions
{
    private readonly TStore _options;

    public PostgreSQLSchemaInitializer(PostgreSQLConnectionFactory<TStore> connectionFactory, TStore options)
        : base(connectionFactory, options)
    {
        _options = options;
    }

    protected override string GenerateCreateTablesSql()
    {
        // Create schema if it doesn't exist
        var schemaSql = $@"
CREATE SCHEMA IF NOT EXISTS ""{_options.Schema}"";
";

        var eventsSql = $@"
CREATE TABLE IF NOT EXISTS {((IEventStoreOptions)_options).EventsTableName} (
    GlobalPosition  BIGSERIAL PRIMARY KEY,
    StreamId        VARCHAR(200) NOT NULL,
    StreamDomain    VARCHAR(200) NOT NULL,
    StreamVersion   INTEGER NOT NULL,
    StreamNamespace VARCHAR(200) NOT NULL,
    EventType       VARCHAR(200) NOT NULL,
    Data            BYTEA NOT NULL,
    Metadata        BYTEA NULL,
    CreatedUtc      TIMESTAMP NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS IX_{_options.StoreName}_Events_StreamDomain_StreamId_StreamVersion
    ON {((IEventStoreOptions)_options).EventsTableName} (StreamDomain, StreamId, StreamVersion);

CREATE INDEX IF NOT EXISTS IX_{_options.StoreName}_Events_StreamId
    ON {((IEventStoreOptions)_options).EventsTableName} (StreamId);

CREATE INDEX IF NOT EXISTS IX_{_options.StoreName}_Events_StreamNamespace
    ON {((IEventStoreOptions)_options).EventsTableName} (StreamNamespace);

CREATE INDEX IF NOT EXISTS IX_{_options.StoreName}_Events_StreamDomain
    ON {((IEventStoreOptions)_options).EventsTableName} (StreamDomain);
";

        var snapshotsSql = $@"
CREATE TABLE IF NOT EXISTS {((IEventStoreOptions)_options).SnapshotsTableName} (
    StreamId       VARCHAR(200) PRIMARY KEY,
    StreamVersion  INTEGER NOT NULL,
    Data           BYTEA NOT NULL,
    CreatedUtc     TIMESTAMP NOT NULL
);
";

        var archiveSegmentsSql = $@"
CREATE TABLE IF NOT EXISTS {((IEventStoreOptions)_options).ArchiveSegmentsTableName} (
    SegmentId       BIGSERIAL PRIMARY KEY,
    MinPosition     BIGINT NOT NULL,
    MaxPosition     BIGINT NOT NULL,
    FileName        VARCHAR(500) NOT NULL,
    Status          SMALLINT NOT NULL,
    StreamNamespace VARCHAR(200) NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS IX_{_options.StoreName}_ArchiveSegments_Range
    ON {((IEventStoreOptions)_options).ArchiveSegmentsTableName} (MinPosition, MaxPosition);
";

        var streamsSql = $@"
CREATE TABLE IF NOT EXISTS {((IEventStoreOptions)_options).StreamsTableName} (
    domain               VARCHAR(64) NOT NULL,
    stream_id            VARCHAR(200) NOT NULL,
    last_version         INTEGER NOT NULL,
    last_position        BIGINT NOT NULL,
    archived_at          TIMESTAMP NULL,
    ArchiveCutoffVersion INTEGER NULL,
    RetentionMode        SMALLINT NOT NULL DEFAULT 0,
    IsDeleted            BOOLEAN NOT NULL DEFAULT false,
    CONSTRAINT PK_{_options.StoreName}_Streams PRIMARY KEY (domain, stream_id)
);

CREATE INDEX IF NOT EXISTS IX_{_options.StoreName}_Streams_Retention_IsDeleted_Cutoff
    ON {((IEventStoreOptions)_options).StreamsTableName} (RetentionMode, IsDeleted, ArchiveCutoffVersion);
";

        return schemaSql + eventsSql + snapshotsSql + archiveSegmentsSql + streamsSql;
    }
}

