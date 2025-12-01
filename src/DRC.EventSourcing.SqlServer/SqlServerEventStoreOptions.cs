using System.Text.RegularExpressions;

namespace DRC.EventSourcing.SqlServer;

/// <summary>
/// Configuration options for a SQL Server-backed event store.
/// </summary>
/// <remarks>
/// <para>SQL Server-specific features supported:</para>
/// <list type="bullet">
///   <item>Multi-schema support via <see cref="Schema"/> property</item>
///   <item>Table name bracketing for reserved word safety</item>
///   <item>High-performance MERGE operations for upserts</item>
///   <item>Transactional consistency with UPDLOCK/ROWLOCK hints</item>
/// </list>
/// <para>Multiple logical event stores can coexist in a single database using different <see cref="IEventStoreOptions.StoreName"/> values.</para>
/// <para>All table and schema names are validated to prevent SQL injection attacks.</para>
/// </remarks>
/// <example>
/// <code>
/// var options = new SqlServerEventStoreOptions 
/// {
///     StoreName = "Inventory",
///     ConnectionString = "Server=localhost;Database=Events;Integrated Security=true;",
///     Schema = "eventsourcing",
///     ArchiveDirectory = @"\\shared\archives\inventory"
/// };
/// </code>
/// </example>
public class SqlServerEventStoreOptions : IEventStoreOptions
{
    private string _schema = "dbo";

    /// <summary>
    /// Gets or sets the SQL Server connection string for this event store.
    /// </summary>
    /// <remarks>
    /// <para>Common SQL Server connection string options:</para>
    /// <list type="bullet">
    ///   <item>Server: Server name or IP address</item>
    ///   <item>Database: Database name</item>
    ///   <item>Integrated Security: Use Windows authentication (true/false)</item>
    ///   <item>User Id / Password: SQL authentication credentials</item>
    ///   <item>MultipleActiveResultSets: Enable MARS for async operations (recommended: true)</item>
    ///   <item>TrustServerCertificate: For development environments</item>
    /// </list>
    /// <para>Example: "Server=localhost;Database=EventStore;Integrated Security=true;MultipleActiveResultSets=true;"</para>
    /// </remarks>
    public string ConnectionString { get; set; } = default!;

    /// <summary>
    /// Gets or sets the logical store name used as a prefix for all tables.
    /// </summary>
    /// <remarks>
    /// <para>Valid characters: Letters, numbers, and underscores. Must start with a letter or underscore.</para>
    /// <para>Maximum length: 50 characters</para>
    /// <para>Example: "Inventory" creates tables like Inventory_Events, Inventory_Streams</para>
    /// <para>Automatically validated to prevent SQL injection attacks.</para>
    /// </remarks>
    public string StoreName { get; set; } = "Default";

    /// <summary>
    /// Gets or sets the directory path for cold archive NDJSON files.
    /// </summary>
    /// <remarks>
    /// <para>If null or empty, archival is disabled and all events remain in SQL Server.</para>
    /// <para>For production, consider using UNC paths or Azure File Storage for shared access across multiple instances.</para>
    /// </remarks>
    public string? ArchiveDirectory { get; set; }

    /// <summary>
    /// Gets or sets an optional list of stream namespaces eligible for archival.
    /// </summary>
    /// <remarks>
    /// <para>If null or empty, all stream namespaces are eligible for archival (default behavior).</para>
    /// <para>If specified, only streams with namespaces in this collection will be archived.</para>
    /// <para>Use this to selectively archive specific event types while keeping others hot.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// ArchiveScopes = new[] { "audit", "history", "analytics" };
    /// </code>
    /// </example>
    public System.Collections.Generic.IEnumerable<string>? ArchiveScopes { get; set; } = null;

    /// <summary>
    /// Gets or sets the SQL Server schema name for all tables.
    /// </summary>
    /// <remarks>
    /// <para>Default value: "dbo"</para>
    /// <para>Valid characters: Letters, numbers, and underscores. Must start with a letter or underscore.</para>
    /// <para>The schema must exist in the database before initializing the event store.</para>
    /// <para>Validated to prevent SQL injection attacks.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown if schema name contains invalid characters</exception>
    public string Schema 
    { 
        get => _schema;
        init 
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Schema name cannot be null or empty");
            
            if (!Regex.IsMatch(value, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                throw new ArgumentException(
                    $"Schema name '{value}' contains invalid characters. Only letters, numbers, and underscores are allowed.",
                    nameof(Schema));
            
            _schema = value;
        }
    }

    /// <summary>
    /// Gets or sets the global position checkpoint for archive operations.
    /// </summary>
    /// <remarks>
    /// <para>When <see cref="ArchiveByGlobalPosition"/> is true, events with GlobalPosition less than this value are eligible for archival.</para>
    /// <para>Null means no global position-based archival (use stream-level cutoffs instead).</para>
    /// <para>Typically updated by a separate archival coordinator process.</para>
    /// </remarks>
    public long? ArchiveCheckpoint { get; set; } = null;

    /// <summary>
    /// Gets or sets whether archival selection uses global position instead of per-stream version cutoffs.
    /// </summary>
    /// <remarks>
    /// <para>When true: Archives all events where GlobalPosition &lt; <see cref="ArchiveCheckpoint"/></para>
    /// <para>When false: Uses per-stream ArchiveCutoffVersion from the Streams table (default behavior)</para>
    /// <para>Global position-based archival is simpler but less flexible than per-stream cutoffs.</para>
    /// </remarks>
    public bool ArchiveByGlobalPosition { get; set; } = false;

    // Table name properties with schema and bracket wrapping for SQL Server
    // These override the interface default implementation to add SQL Server-specific formatting
    
    /// <summary>
    /// Gets the fully qualified Events table name with schema and brackets.
    /// </summary>
    /// <remarks>
    /// Format: [schema].[StoreName_Events]
    /// Brackets protect against reserved words and special characters.
    /// </remarks>
    string IEventStoreOptions.EventsTableName => $"[{Schema}].[{((IEventStoreOptions)this).ValidateStoreName()}_Events]";
    
    /// <summary>
    /// Gets the fully qualified Streams table name with schema and brackets.
    /// </summary>
    public string StreamsTableName => $"[{Schema}].[{((IEventStoreOptions)this).ValidateStoreName()}_Streams]";
    
    /// <summary>
    /// Gets the fully qualified Snapshots table name with schema and brackets.
    /// </summary>
    string IEventStoreOptions.SnapshotsTableName => $"[{Schema}].[{((IEventStoreOptions)this).ValidateStoreName()}_Snapshots]";
    
    /// <summary>
    /// Gets the fully qualified ArchiveSegments table name with schema and brackets.
    /// </summary>
    string IEventStoreOptions.ArchiveSegmentsTableName => $"[{Schema}].[{((IEventStoreOptions)this).ValidateStoreName()}_ArchiveSegments]";
}

/// <summary>
/// Extension methods for IEventStoreOptions to support SQL Server-specific needs.
/// </summary>
internal static class EventStoreOptionsExtensions
{
    /// <summary>
    /// Validates the StoreName and returns it for use in table name construction.
    /// </summary>
    internal static string ValidateStoreName(this IEventStoreOptions options)
    {
        var storeName = options.StoreName;
        
        if (string.IsNullOrWhiteSpace(storeName))
            throw new ArgumentException("StoreName cannot be null or empty");
        
        if (storeName.Length > 50)
            throw new ArgumentException($"StoreName cannot exceed 50 characters. Current length: {storeName.Length}");
        
        if (!Regex.IsMatch(storeName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            throw new ArgumentException(
                $"StoreName '{storeName}' contains invalid characters. Only letters, numbers, and underscores are allowed.",
                nameof(options.StoreName));
        
        return storeName;
    }
}
