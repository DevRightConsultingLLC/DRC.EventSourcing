using System.Text.RegularExpressions;

namespace DRC.EventSourcing.MySql;

/// <summary>
/// Configuration options for a MySQL/MariaDB-backed event store.
/// </summary>
public class MySqlEventStoreOptions : IEventStoreOptions
{
    /// <summary>
    /// Gets or sets the MySQL connection string for this event store.
    /// </summary>
    /// <remarks>
    /// Common MySQL connection string options:
    /// <list type="bullet">
    ///   <item>Server: MySQL server hostname or IP address</item>
    ///   <item>Port: Port number (default: 3306)</item>
    ///   <item>Database: Database name</item>
    ///   <item>User: MySQL username</item>
    ///   <item>Password: Password</item>
    ///   <item>Pooling: Enable connection pooling (default: true)</item>
    ///   <item>MinimumPoolSize: Minimum connections in pool</item>
    ///   <item>MaximumPoolSize: Maximum connections in pool</item>
    ///   <item>SslMode: SSL connection mode (None, Preferred, Required)</item>
    /// </list>
    /// Example: "Server=localhost;Port=3306;Database=eventstore;User=root;Password=password;Pooling=true;"
    /// </remarks>
    public string ConnectionString { get; set; } = default!;

    /// <summary>
    /// Gets or sets the logical name for this event store instance.
    /// Used as a prefix for all database objects (tables, indexes, etc.).
    /// </summary>
    public string StoreName { get; set; } = default!;

    /// <summary>
    /// Gets or sets the directory path for cold storage archives (NDJSON files).
    /// If null or empty, archiving features will be disabled.
    /// </summary>
    public string? ArchiveDirectory { get; set; }

    /// <summary>
    /// Gets the table name for events: storename_Events
    /// MySQL uses backticks for identifier quoting.
    /// </summary>
    public string EventsTableName => $"`{StoreName}_Events`";

    /// <summary>
    /// Gets the table name for streams: storename_Streams
    /// </summary>
    public string StreamsTableName => $"`{StoreName}_Streams`";

    /// <summary>
    /// Gets the table name for snapshots: storename_Snapshots
    /// </summary>
    public string SnapshotsTableName => $"`{StoreName}_Snapshots`";

    /// <summary>
    /// Gets the table name for archive segments: storename_ArchiveSegments
    /// </summary>
    public string ArchiveSegmentsTableName => $"`{StoreName}_ArchiveSegments`";

    /// <summary>
    /// Validates that the StoreName is a valid MySQL identifier.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StoreName))
            throw new ArgumentException("StoreName must be provided and non-empty.", nameof(StoreName));

        // MySQL identifier validation (alphanumeric, underscores, max 64 chars)
        if (!Regex.IsMatch(StoreName, @"^[a-zA-Z_][a-zA-Z0-9_]{0,62}$"))
            throw new ArgumentException(
                $"'{StoreName}' is not a valid MySQL identifier. Must start with letter or underscore, " +
                "contain only alphanumeric characters and underscores, and be max 63 characters.",
                nameof(StoreName));

        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new ArgumentException("ConnectionString must be provided.", nameof(ConnectionString));
    }
}

