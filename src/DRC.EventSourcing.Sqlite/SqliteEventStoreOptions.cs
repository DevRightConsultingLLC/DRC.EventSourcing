namespace DRC.EventSourcing.Sqlite;

/// <summary>
/// Base configuration options for a SQLite-based event store.
/// </summary>
/// <remarks>
/// <para>Each logical event store should have its own concrete subclass of this options class.</para>
/// <para>SQLite-specific considerations:</para>
/// <list type="bullet">
///   <item>Connection string format: "Data Source=path/to/database.db"</item>
///   <item>Supports in-memory databases: "Data Source=:memory:"</item>
///   <item>Thread safety: SQLite uses serialized mode by default for thread-safe access</item>
///   <item>Performance: Consider using WAL mode for better concurrent read performance</item>
/// </list>
/// <para>Table names are automatically validated for SQL injection safety via the <see cref="IEventStoreOptions"/> interface.</para>
/// </remarks>
/// <example>
/// <code>
/// public class AssetEventStoreOptions : SqliteEventStoreOptions 
/// {
///     public AssetEventStoreOptions() 
///     {
///         StoreName = "Asset";
///         ConnectionString = "Data Source=events.db";
///         ArchiveDirectory = "./archive/assets";
///     }
/// }
/// </code>
/// </example>
public abstract class SqliteEventStoreOptions : IEventStoreOptions
{
    /// <summary>
    /// Gets or sets the SQLite connection string for this event store.
    /// </summary>
    /// <remarks>
    /// <para>Common SQLite connection string options:</para>
    /// <list type="bullet">
    ///   <item>Data Source: Path to the database file or ":memory:" for in-memory</item>
    ///   <item>Mode: ReadWriteCreate (default), ReadWrite, ReadOnly, Memory</item>
    ///   <item>Cache: Shared or Private (default)</item>
    ///   <item>Pooling: True (default) or False</item>
    /// </list>
    /// <para>Example: "Data Source=events.db;Mode=ReadWriteCreate;Cache=Shared"</para>
    /// </remarks>
    public string ConnectionString { get; set; } = default!;

    /// <summary>
    /// Gets or sets the logical store name used as a prefix for all tables.
    /// </summary>
    /// <remarks>
    /// <para>Valid characters: Letters, numbers, and underscores. Must start with a letter or underscore.</para>
    /// <para>Maximum length: 50 characters</para>
    /// <para>Automatically validated to prevent SQL injection attacks.</para>
    /// </remarks>
    public string StoreName { get; set; } = default!;

    /// <summary>
    /// Gets or sets the directory path for cold archive NDJSON files.
    /// </summary>
    /// <remarks>
    /// <para>If null or empty, archival is disabled and all events remain in the hot SQLite database.</para>
    /// <para>The directory is created automatically if it doesn't exist.</para>
    /// <para>Ensure adequate disk space and appropriate file permissions for the archive directory.</para>
    /// </remarks>
    public string? ArchiveDirectory { get; set; }
}
