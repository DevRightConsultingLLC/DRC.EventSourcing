using System.Text.RegularExpressions;

namespace DRC.EventSourcing.PostgreSQL;

/// <summary>
/// Configuration options for a PostgreSQL-backed event store.
/// </summary>
public class PostgreSQLEventStoreOptions : IEventStoreOptions
{
    private string _schema = "public";

    /// <summary>
    /// Gets or sets the PostgreSQL connection string for this event store.
    /// </summary>
    public string ConnectionString { get; set; } = default!;

    /// <summary>
    /// Gets or sets the logical name for this event store instance.
    /// Used as a prefix for all database objects (tables, indexes, etc.).
    /// </summary>
    public string StoreName { get; set; } = default!;

    /// <summary>
    /// Gets or sets the PostgreSQL schema name where event store tables will be created.
    /// Defaults to "public".
    /// </summary>
    public string Schema
    {
        get => _schema;
        set
        {
            ValidateIdentifier(value, nameof(Schema));
            _schema = value;
        }
    }

    /// <summary>
    /// Gets or sets the directory path for cold storage archives (NDJSON files).
    /// If null or empty, archiving features will be disabled.
    /// </summary>
    public string? ArchiveDirectory { get; set; }

    /// <summary>
    /// Gets the fully qualified table name for events: schema.storename_Events
    /// </summary>
    public string EventsTableName => $"\"{Schema}\".\"{StoreName}_Events\"";

    /// <summary>
    /// Gets the fully qualified table name for streams: schema.storename_Streams
    /// </summary>
    public string StreamsTableName => $"\"{Schema}\".\"{StoreName}_Streams\"";

    /// <summary>
    /// Gets the fully qualified table name for snapshots: schema.storename_Snapshots
    /// </summary>
    public string SnapshotsTableName => $"\"{Schema}\".\"{StoreName}_Snapshots\"";

    /// <summary>
    /// Gets the fully qualified table name for archive segments: schema.storename_ArchiveSegments
    /// </summary>
    public string ArchiveSegmentsTableName => $"\"{Schema}\".\"{StoreName}_ArchiveSegments\"";

    private static void ValidateIdentifier(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be null or whitespace.", paramName);

        // PostgreSQL identifier validation (alphanumeric, underscores, max 63 chars)
        if (!Regex.IsMatch(value, @"^[a-zA-Z_][a-zA-Z0-9_]{0,62}$"))
            throw new ArgumentException(
                $"'{value}' is not a valid PostgreSQL identifier. Must start with letter or underscore, " +
                "contain only alphanumeric characters and underscores, and be max 63 characters.",
                paramName);
    }
}

