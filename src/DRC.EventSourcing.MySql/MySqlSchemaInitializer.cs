using DRC.EventSourcing.Infrastructure;

namespace DRC.EventSourcing.MySql;

/// <summary>
/// Creates the per-store MySQL tables if they do not already exist.
/// </summary>
public sealed class MySqlSchemaInitializer<TStore>
    : BaseSchemaInitializer<TStore>, IEventStoreSchemaInitializer<TStore>
    where TStore : MySqlEventStoreOptions
{
    private readonly TStore _options;

    public MySqlSchemaInitializer(MySqlConnectionFactory<TStore> connectionFactory, TStore options)
        : base(connectionFactory, options)
    {
        _options = options;
    }

    protected override string GenerateCreateTablesSql()
    {
        var eventsSql = $@"
CREATE TABLE IF NOT EXISTS {((IEventStoreOptions)_options).EventsTableName} (
    GlobalPosition  BIGINT AUTO_INCREMENT PRIMARY KEY,
    StreamId        VARCHAR(200) NOT NULL,
    StreamDomain    VARCHAR(200) NOT NULL,
    StreamVersion   INT NOT NULL,
    StreamNamespace VARCHAR(200) NOT NULL,
    EventType       VARCHAR(200) NOT NULL,
    Data            LONGBLOB NOT NULL,
    Metadata        LONGBLOB NULL,
    CreatedUtc      DATETIME(6) NOT NULL,
    UNIQUE INDEX IX_{_options.StoreName}_Events_StreamDomain_StreamId_StreamVersion (StreamDomain, StreamId, StreamVersion),
    INDEX IX_{_options.StoreName}_Events_StreamId (StreamId),
    INDEX IX_{_options.StoreName}_Events_StreamNamespace (StreamNamespace),
    INDEX IX_{_options.StoreName}_Events_StreamDomain (StreamDomain)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
";

        var snapshotsSql = $@"
CREATE TABLE IF NOT EXISTS {((IEventStoreOptions)_options).SnapshotsTableName} (
    StreamId       VARCHAR(200) PRIMARY KEY,
    StreamVersion  INT NOT NULL,
    Data           LONGBLOB NOT NULL,
    CreatedUtc     DATETIME(6) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
";

        var archiveSegmentsSql = $@"
CREATE TABLE IF NOT EXISTS {((IEventStoreOptions)_options).ArchiveSegmentsTableName} (
    SegmentId       BIGINT AUTO_INCREMENT PRIMARY KEY,
    MinPosition     BIGINT NOT NULL,
    MaxPosition     BIGINT NOT NULL,
    FileName        VARCHAR(500) NOT NULL,
    Status          SMALLINT NOT NULL,
    StreamNamespace VARCHAR(200) NULL,
    UNIQUE INDEX IX_{_options.StoreName}_ArchiveSegments_Range (MinPosition, MaxPosition)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
";

        var streamsSql = $@"
CREATE TABLE IF NOT EXISTS {((IEventStoreOptions)_options).StreamsTableName} (
    domain               VARCHAR(64) NOT NULL,
    stream_id            VARCHAR(200) NOT NULL,
    last_version         INT NOT NULL,
    last_position        BIGINT NOT NULL,
    archived_at          DATETIME(6) NULL,
    ArchiveCutoffVersion INT NULL,
    RetentionMode        SMALLINT NOT NULL DEFAULT 0,
    IsDeleted            TINYINT(1) NOT NULL DEFAULT 0,
    PRIMARY KEY (domain, stream_id),
    INDEX IX_{_options.StoreName}_Streams_Retention_IsDeleted_Cutoff (RetentionMode, IsDeleted, ArchiveCutoffVersion)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
";

        return eventsSql + snapshotsSql + archiveSegmentsSql + streamsSql;
    }
}

