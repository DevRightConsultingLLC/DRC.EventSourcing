using DRC.EventSourcing.Infrastructure;

namespace DRC.EventSourcing.SqlServer;

/// <summary>
/// Creates the per-store SQL Server tables if they do not already exist.
/// </summary>
public sealed class SqlServerSchemaInitializer<TStore>
    : BaseSchemaInitializer<TStore>, IEventStoreSchemaInitializer<TStore>
    where TStore : SqlServerEventStoreOptions
{
    private readonly TStore _options;

    public SqlServerSchemaInitializer(SqlServerConnectionFactory<TStore> connectionFactory, TStore options)
        : base(connectionFactory, options)
    {
        _options = options;
    }

    protected override string GenerateCreateTablesSql()
    {
        var eventsSql = $@"
IF NOT EXISTS (
    SELECT 1
    FROM sys.tables t
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE t.name = '{_options.StoreName}_Events'
      AND s.name = '{_options.Schema}'
)
BEGIN
    CREATE TABLE {((IEventStoreOptions)_options).EventsTableName} (
        GlobalPosition  BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        StreamId        NVARCHAR(200) NOT NULL,
        StreamDomain    NVARCHAR(200) NOT NULL,
        StreamVersion   INT NOT NULL,
        StreamNamespace NVARCHAR(200) NOT NULL,
        EventType       NVARCHAR(200) NOT NULL,
        Data            VARBINARY(MAX) NOT NULL,
        Metadata        VARBINARY(MAX) NULL,
        CreatedUtc      DATETIME2 NOT NULL
    );

    CREATE UNIQUE INDEX IX_{_options.StoreName}_Events_StreamDomain_StreamId_StreamVersion
        ON {((IEventStoreOptions)_options).EventsTableName} (StreamDomain, StreamId, StreamVersion);

    CREATE INDEX IX_{_options.StoreName}_Events_StreamId
        ON {((IEventStoreOptions)_options).EventsTableName} (StreamId);

    CREATE INDEX IX_{_options.StoreName}_Events_StreamNamespace
        ON {((IEventStoreOptions)_options).EventsTableName} (StreamNamespace);

    CREATE INDEX IX_{_options.StoreName}_Events_StreamDomain
        ON {((IEventStoreOptions)_options).EventsTableName} (StreamDomain);
END;
";

        var snapshotsSql = $@"
IF NOT EXISTS (
    SELECT 1
    FROM sys.tables t
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE t.name = '{_options.StoreName}_Snapshots'
      AND s.name = '{_options.Schema}'
)
BEGIN
    CREATE TABLE {((IEventStoreOptions)_options).SnapshotsTableName} (
        StreamId       NVARCHAR(200) NOT NULL PRIMARY KEY,
        StreamVersion  INT NOT NULL,
        Data           VARBINARY(MAX) NOT NULL,
        CreatedUtc     DATETIME2 NOT NULL
    );
END;
";

        var archiveSegmentsSql = $@"
IF NOT EXISTS (
    SELECT 1
    FROM sys.tables t
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE t.name = '{_options.StoreName}_ArchiveSegments'
      AND s.name = '{_options.Schema}'
)
BEGIN
    CREATE TABLE {((IEventStoreOptions)_options).ArchiveSegmentsTableName} (
        SegmentId       BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        MinPosition     BIGINT NOT NULL,
        MaxPosition     BIGINT NOT NULL,
        FileName        NVARCHAR(500) NOT NULL,
        Status          TINYINT NOT NULL,
        StreamNamespace NVARCHAR(200) NULL
    );

    CREATE UNIQUE INDEX IX_{_options.StoreName}_ArchiveSegments_Range
        ON {((IEventStoreOptions)_options).ArchiveSegmentsTableName} (MinPosition, MaxPosition);
END;
";

        var streamsSql = $@"
IF NOT EXISTS (
    SELECT 1
    FROM sys.tables t
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE t.name = '{_options.StoreName}_Streams'
      AND s.name = '{_options.Schema}'
)
BEGIN
    CREATE TABLE {((IEventStoreOptions)_options).StreamsTableName} (
        domain               NVARCHAR(64) NOT NULL,
        stream_id            UNIQUEIDENTIFIER NOT NULL,
        last_version         INT NOT NULL,
        last_position        BIGINT NOT NULL,
        archived_at          DATETIME2 NULL,
        ArchiveCutoffVersion INT NULL,
        RetentionMode        SMALLINT NOT NULL,
        IsDeleted            BIT NOT NULL CONSTRAINT DF_{_options.StoreName}_Streams_IsDeleted DEFAULT (0),
        CONSTRAINT PK_{_options.StoreName}_Streams PRIMARY KEY (domain, stream_id)
    );
END;

-- If the Streams table exists, ensure new columns are present
IF EXISTS (
    SELECT 1
    FROM sys.tables t
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE t.name = '{_options.StoreName}_Streams'
      AND s.name = '{_options.Schema}'
)
BEGIN
    -- ArchiveCutoffVersion
    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns c
        JOIN sys.tables t ON c.object_id = t.object_id
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        WHERE t.name = '{_options.StoreName}_Streams'
          AND s.name = '{_options.Schema}'
          AND c.name = 'ArchiveCutoffVersion'
    )
    BEGIN
        ALTER TABLE {((IEventStoreOptions)_options).StreamsTableName}
            ADD ArchiveCutoffVersion INT NULL;
    END;

    -- RetentionMode
    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns c
        JOIN sys.tables t ON c.object_id = t.object_id
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        WHERE t.name = '{_options.StoreName}_Streams'
          AND s.name = '{_options.Schema}'
          AND c.name = 'RetentionMode'
    )
    BEGIN
        ALTER TABLE {((IEventStoreOptions)_options).StreamsTableName}
            ADD RetentionMode SMALLINT NOT NULL
                CONSTRAINT DF_{_options.StoreName}_Streams_RetentionMode DEFAULT (0);
    END;

    -- IsDeleted
    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns c
        JOIN sys.tables t ON c.object_id = t.object_id
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        WHERE t.name = '{_options.StoreName}_Streams'
          AND s.name = '{_options.Schema}'
          AND c.name = 'IsDeleted'
    )
    BEGIN
        ALTER TABLE {((IEventStoreOptions)_options).StreamsTableName}
            ADD IsDeleted BIT NOT NULL
                CONSTRAINT DF_{_options.StoreName}_Streams_IsDeleted_Compat DEFAULT (0);
    END;
END;

-- Create archive/delete-friendly index if it doesn't exist
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE t.name = '{_options.StoreName}_Streams'
      AND s.name = '{_options.Schema}'
      AND i.name = 'IX_{_options.StoreName}_Streams_Retention_IsDeleted_Cutoff'
)
BEGIN
    CREATE INDEX IX_{_options.StoreName}_Streams_Retention_IsDeleted_Cutoff
        ON {((IEventStoreOptions)_options).StreamsTableName} (RetentionMode, IsDeleted, ArchiveCutoffVersion);
END;
";

        return eventsSql + snapshotsSql + archiveSegmentsSql + streamsSql;
    }
}
