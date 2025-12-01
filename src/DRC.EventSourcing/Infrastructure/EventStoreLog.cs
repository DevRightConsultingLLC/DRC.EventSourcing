using Microsoft.Extensions.Logging;

namespace DRC.EventSourcing.Infrastructure;

/// <summary>
/// High-performance logging interface for event store operations using source-generated logging.
/// </summary>
/// <remarks>
/// <para>Uses compile-time source generation for zero-allocation, high-performance logging in hot paths.</para>
/// <para>All log methods are partial and implemented by the C# source generator.</para>
/// <para>Performance characteristics:</para>
/// <list type="bullet">
///   <item>Zero allocation for disabled log levels</item>
///   <item>Pre-compiled format strings</item>
///   <item>Optimized parameter boxing</item>
///   <item>Minimal overhead even in ultra-hot paths</item>
/// </list>
/// </remarks>
public static partial class EventStoreLog
{
    // ===== Event Store Operations =====
    
    /// <summary>
    /// Logs the start of an append operation to a stream.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="domain">The domain of the stream</param>
    /// <param name="streamId">The stream identifier</param>
    /// <param name="expectedVersion">The expected version for optimistic concurrency</param>
    /// <param name="eventCount">Number of events being appended</param>
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Debug,
        Message = "Appending {EventCount} event(s) to stream {Domain}/{StreamId} at expected version {ExpectedVersion}")]
    public static partial void AppendingToStream(
        this ILogger logger, 
        string domain, 
        string streamId, 
        int expectedVersion, 
        int eventCount);

    /// <summary>
    /// Logs successful completion of an append operation.
    /// </summary>
    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Successfully appended {EventCount} event(s) to stream {Domain}/{StreamId}, new version: {NewVersion}")]
    public static partial void AppendedToStream(
        this ILogger logger, 
        string domain, 
        string streamId, 
        int newVersion, 
        int eventCount);

    /// <summary>
    /// Logs a concurrency conflict during append.
    /// </summary>
    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Concurrency conflict on stream {Domain}/{StreamId}: expected version {ExpectedVersion}, actual version {ActualVersion}")]
    public static partial void ConcurrencyConflict(
        this ILogger logger, 
        string domain, 
        string streamId, 
        int expectedVersion, 
        int actualVersion);

    /// <summary>
    /// Logs a stream read operation.
    /// </summary>
    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Debug,
        Message = "Reading stream {Domain}/{StreamId} from version {FromVersion}, max count: {MaxCount}")]
    public static partial void ReadingStream(
        this ILogger logger, 
        string domain, 
        string streamId, 
        int fromVersion, 
        int maxCount);

    /// <summary>
    /// Logs completion of a stream read operation.
    /// </summary>
    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Debug,
        Message = "Read {EventCount} event(s) from stream {Domain}/{StreamId}")]
    public static partial void ReadStream(
        this ILogger logger, 
        string domain, 
        string streamId, 
        int eventCount);

    /// <summary>
    /// Logs the start of a ReadAllForwards operation.
    /// </summary>
    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Debug,
        Message = "Reading all events forwards from position {Position}, batch size: {BatchSize}")]
    public static partial void ReadingAllForwards(
        this ILogger logger, 
        long position, 
        int batchSize);

    // ===== Archive Operations =====
    
    /// <summary>
    /// Logs the start of an archive operation.
    /// </summary>
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Starting archive operation")]
    public static partial void ArchiveStarted(this ILogger logger);

    /// <summary>
    /// Logs completion of an archive operation.
    /// </summary>
    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Information,
        Message = "Archive operation completed: {StreamsProcessed} stream(s) processed, {EventsArchived} event(s) archived, duration: {DurationMs}ms")]
    public static partial void ArchiveCompleted(
        this ILogger logger, 
        int streamsProcessed, 
        long eventsArchived, 
        long durationMs);

    /// <summary>
    /// Logs archiving of a specific stream.
    /// </summary>
    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Information,
        Message = "Archiving stream {Domain}/{StreamId}: {EventCount} event(s) from positions {MinPosition} to {MaxPosition}")]
    public static partial void ArchivingStream(
        this ILogger logger, 
        string domain, 
        string streamId, 
        int eventCount, 
        long minPosition, 
        long maxPosition);

    /// <summary>
    /// Logs writing of an archive file.
    /// </summary>
    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Debug,
        Message = "Writing archive file: {FileName}, {EventCount} event(s)")]
    public static partial void WritingArchiveFile(
        this ILogger logger, 
        string fileName, 
        int eventCount);

    /// <summary>
    /// Logs deletion of archived events from hot store.
    /// </summary>
    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Information,
        Message = "Deleted {EventCount} archived event(s) from hot store for stream {Domain}/{StreamId}")]
    public static partial void DeletedArchivedEvents(
        this ILogger logger, 
        string domain, 
        string streamId, 
        int eventCount);

    /// <summary>
    /// Logs hard deletion of a stream.
    /// </summary>
    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Warning,
        Message = "Hard deleting stream {Domain}/{StreamId} and all its events")]
    public static partial void HardDeletingStream(
        this ILogger logger, 
        string domain, 
        string streamId);

    /// <summary>
    /// Logs detection of overlapping archive segments.
    /// </summary>
    [LoggerMessage(
        EventId = 2007,
        Level = LogLevel.Warning,
        Message = "Skipping archive for positions {MinPosition}-{MaxPosition}: overlapping segment(s) already exist")]
    public static partial void OverlappingArchiveSegment(
        this ILogger logger, 
        long minPosition, 
        long maxPosition);

    // ===== Snapshot Operations =====
    
    /// <summary>
    /// Logs saving a snapshot.
    /// </summary>
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Saving snapshot for stream {StreamId} at version {Version}")]
    public static partial void SavingSnapshot(
        this ILogger logger, 
        string streamId, 
        int version);

    /// <summary>
    /// Logs loading a snapshot.
    /// </summary>
    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Debug,
        Message = "Loaded snapshot for stream {StreamId} at version {Version}")]
    public static partial void LoadedSnapshot(
        this ILogger logger, 
        string streamId, 
        int version);

    /// <summary>
    /// Logs when no snapshot is found.
    /// </summary>
    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Debug,
        Message = "No snapshot found for stream {StreamId}")]
    public static partial void NoSnapshotFound(
        this ILogger logger, 
        string streamId);

    // ===== Schema Initialization =====
    
    /// <summary>
    /// Logs schema initialization start.
    /// </summary>
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Information,
        Message = "Initializing event store schema for store '{StoreName}'")]
    public static partial void InitializingSchema(
        this ILogger logger, 
        string storeName);

    /// <summary>
    /// Logs schema initialization completion.
    /// </summary>
    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Information,
        Message = "Schema initialization completed for store '{StoreName}'")]
    public static partial void SchemaInitialized(
        this ILogger logger, 
        string storeName);

    // ===== Error Logging =====
    
    /// <summary>
    /// Logs an error during event append.
    /// </summary>
    [LoggerMessage(
        EventId = 9001,
        Level = LogLevel.Error,
        Message = "Error appending events to stream {Domain}/{StreamId}")]
    public static partial void AppendError(
        this ILogger logger, 
        Exception exception, 
        string domain, 
        string streamId);

    /// <summary>
    /// Logs an error during archive operation.
    /// </summary>
    [LoggerMessage(
        EventId = 9002,
        Level = LogLevel.Error,
        Message = "Error during archive operation for stream {Domain}/{StreamId}")]
    public static partial void ArchiveError(
        this ILogger logger, 
        Exception exception, 
        string domain, 
        string streamId);

    /// <summary>
    /// Logs an error during snapshot operation.
    /// </summary>
    [LoggerMessage(
        EventId = 9003,
        Level = LogLevel.Error,
        Message = "Error saving snapshot for stream {StreamId}")]
    public static partial void SnapshotError(
        this ILogger logger, 
        Exception exception, 
        string streamId);

    /// <summary>
    /// Logs a transaction rollback failure.
    /// </summary>
    [LoggerMessage(
        EventId = 9004,
        Level = LogLevel.Error,
        Message = "Failed to rollback transaction for stream {Domain}/{StreamId}")]
    public static partial void RollbackFailed(
        this ILogger logger, 
        Exception exception, 
        string domain, 
        string streamId);

    /// <summary>
    /// Logs an error during schema initialization.
    /// </summary>
    [LoggerMessage(
        EventId = 9005,
        Level = LogLevel.Error,
        Message = "Error initializing schema for store '{StoreName}'")]
    public static partial void SchemaInitializationError(
        this ILogger logger, 
        Exception exception, 
        string storeName);

    // ===== Performance Metrics =====
    
    /// <summary>
    /// Logs performance metrics for an append operation.
    /// </summary>
    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Debug,
        Message = "Append performance: {EventCount} event(s) in {DurationMs}ms ({EventsPerSecond} events/sec)")]
    public static partial void AppendPerformance(
        this ILogger logger, 
        int eventCount, 
        long durationMs, 
        double eventsPerSecond);

    /// <summary>
    /// Logs when an operation is unusually slow.
    /// </summary>
    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Warning,
        Message = "Slow operation detected: {OperationName} took {DurationMs}ms (threshold: {ThresholdMs}ms)")]
    public static partial void SlowOperation(
        this ILogger logger, 
        string operationName, 
        long durationMs, 
        long thresholdMs);
}

