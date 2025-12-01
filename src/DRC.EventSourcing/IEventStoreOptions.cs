using System.Text.RegularExpressions;

namespace DRC.EventSourcing;

/// <summary>
/// Configuration options for an event store implementation.
/// Provides connection details, store naming, and archival settings.
/// All table names are validated for SQL safety to prevent injection attacks.
/// </summary>
public interface IEventStoreOptions
{
    /// <summary>
    /// Gets or sets the connection string for this store.
    /// You can share the same database instance between multiple stores by using different <see cref="StoreName"/> values.
    /// </summary>
    /// <remarks>
    /// The connection string format depends on the database provider (SQLite, SQL Server, PostgreSQL, MySQL).
    /// Ensure the connection string includes appropriate timeout, pooling, and security settings for your environment.
    /// </remarks>
    string ConnectionString { get; set; }
    
    /// <summary>
    /// Gets or sets the logical store name, used as a table prefix.
    /// </summary>
    /// <remarks>
    /// <para>The store name is prepended to all table names to allow multiple event stores in the same database.</para>
    /// <para>Example: "Asset" results in tables: Asset_Events, Asset_Streams, Asset_Snapshots, Asset_ArchiveSegments</para>
    /// <para>Valid characters: Letters, numbers, and underscores only. Must start with a letter or underscore.</para>
    /// <para>Maximum length: 50 characters</para>
    /// </remarks>
    string StoreName { get; set; }

    /// <summary>
    /// Gets or sets the directory path where cold archive NDJSON files are stored for this event store.
    /// </summary>
    /// <remarks>
    /// <para>If null or empty, archival operations will not be performed and events remain in the hot store indefinitely.</para>
    /// <para>The directory will be created automatically if it doesn't exist.</para>
    /// <para>Archive files are written atomically with temporary files to prevent partial reads.</para>
    /// </remarks>
    string? ArchiveDirectory { get; set; }

    /// <summary>
    /// Gets the validated name for the Events table.
    /// </summary>
    /// <remarks>
    /// Table name format: {StoreName}_Events
    /// Validation ensures SQL injection safety by restricting to alphanumeric and underscore characters.
    /// </remarks>
    string EventsTableName => ValidateAndBuildTableName(StoreName, "Events");
    
    /// <summary>
    /// Gets the validated name for the Streams table.
    /// </summary>
    /// <remarks>
    /// Table name format: {StoreName}_Streams
    /// This table tracks stream headers including version, position, retention mode, and archival status.
    /// </remarks>
    string StreamsTableName => ValidateAndBuildTableName(StoreName, "Streams");
    
    /// <summary>
    /// Gets the validated name for the Snapshots table.
    /// </summary>
    /// <remarks>
    /// Table name format: {StoreName}_Snapshots
    /// Snapshots store point-in-time state captures to optimize event replay for aggregates.
    /// </remarks>
    string SnapshotsTableName => ValidateAndBuildTableName(StoreName, "Snapshots");
    
    /// <summary>
    /// Gets the validated name for the ArchiveSegments table.
    /// </summary>
    /// <remarks>
    /// Table name format: {StoreName}_ArchiveSegments
    /// This table tracks metadata about archived NDJSON files including position ranges and file paths.
    /// </remarks>
    string ArchiveSegmentsTableName => ValidateAndBuildTableName(StoreName, "ArchiveSegments");

    /// <summary>
    /// Validates and constructs a table name by combining store name and suffix.
    /// Prevents SQL injection by ensuring only safe characters are used.
    /// </summary>
    /// <param name="storeName">The store name prefix to validate</param>
    /// <param name="suffix">The table suffix (Events, Streams, etc.)</param>
    /// <returns>A validated table name in the format {storeName}_{suffix}</returns>
    /// <exception cref="ArgumentException">Thrown if storeName contains invalid characters or exceeds length limits</exception>
    /// <remarks>
    /// <para>Valid characters: a-z, A-Z, 0-9, underscore (_)</para>
    /// <para>Must start with a letter or underscore</para>
    /// <para>Maximum length: 50 characters for store name</para>
    /// <para>This validation is critical for security - never bypass it</para>
    /// </remarks>
    private static string ValidateAndBuildTableName(string storeName, string suffix)
    {
        if (string.IsNullOrWhiteSpace(storeName))
            throw new ArgumentException("StoreName cannot be null or empty", nameof(storeName));
        
        if (storeName.Length > 50)
            throw new ArgumentException($"StoreName cannot exceed 50 characters. Current length: {storeName.Length}", nameof(storeName));
        
        // SQL injection prevention: Only allow alphanumeric and underscore, must start with letter or underscore
        if (!Regex.IsMatch(storeName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            throw new ArgumentException(
                $"StoreName '{storeName}' contains invalid characters. Only letters, numbers, and underscores are allowed. Must start with a letter or underscore.",
                nameof(storeName));
        
        if (string.IsNullOrWhiteSpace(suffix))
            throw new ArgumentException("Table suffix cannot be null or empty", nameof(suffix));
        
        return $"{storeName}_{suffix}";
    }
}