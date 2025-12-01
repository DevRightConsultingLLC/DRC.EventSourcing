using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DRC.EventSourcing.Infrastructure;

/// <summary>
/// High-performance metrics infrastructure for event store operations using System.Diagnostics.Metrics.
/// </summary>
/// <remarks>
/// <para>Provides observable metrics compatible with OpenTelemetry and other monitoring systems.</para>
/// <para>Performance characteristics:</para>
/// <list type="bullet">
///   <item>Near-zero overhead when no listeners are attached</item>
///   <item>Lock-free metric recording</item>
///   <item>Batched aggregation for high-frequency operations</item>
///   <item>Optimized for ultra-hot path code execution</item>
/// </list>
/// <para>Metrics are exported via the standard .NET Metrics API and can be consumed by:</para>
/// <list type="bullet">
///   <item>OpenTelemetry collectors</item>
///   <item>Application Insights</item>
///   <item>Prometheus exporters</item>
///   <item>Custom metric listeners</item>
/// </list>
/// </remarks>
/// <example>
/// To enable metrics collection with OpenTelemetry:
/// <code>
/// services.AddOpenTelemetry()
///     .WithMetrics(builder => builder
///         .AddMeter(EventStoreMetrics.MeterName));
/// </code>
/// </example>
public sealed class EventStoreMetrics : IDisposable
{
    /// <summary>
    /// The meter name for all event store metrics.
    /// Use this when configuring metric exporters and listeners.
    /// </summary>
    public const string MeterName = "DRC.EventSourcing";

    private readonly Meter _meter;
    private readonly string _storeName;

    // Counters - accumulate values over time
    private readonly Counter<long> _eventsAppended;
    private readonly Counter<long> _eventsRead;
    private readonly Counter<long> _eventsArchived;
    private readonly Counter<long> _eventsDeleted;
    private readonly Counter<long> _snapshotsSaved;
    private readonly Counter<long> _snapshotsLoaded;
    private readonly Counter<long> _concurrencyConflicts;
    private readonly Counter<long> _errors;

    // Histograms - track distribution of values
    private readonly Histogram<double> _appendDuration;
    private readonly Histogram<double> _readDuration;
    private readonly Histogram<double> _archiveDuration;
    private readonly Histogram<int> _batchSize;
    private readonly Histogram<int> _streamVersion;

    // Observable gauges - poll current state
    private long _activeConnections;
    private long _activeTransactions;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventStoreMetrics"/> class.
    /// </summary>
    /// <param name="storeName">The logical name of the event store (used for metric tags)</param>
    /// <remarks>
    /// Create one instance per event store and reuse it for the lifetime of the application.
    /// The instance is thread-safe and optimized for concurrent access.
    /// </remarks>
    public EventStoreMetrics(string storeName)
    {
        _storeName = storeName ?? throw new ArgumentNullException(nameof(storeName));
        _meter = new Meter(MeterName, "1.0.0");

        // Initialize counters
        _eventsAppended = _meter.CreateCounter<long>(
            "eventsourcing.events.appended",
            unit: "events",
            description: "Total number of events appended to streams");

        _eventsRead = _meter.CreateCounter<long>(
            "eventsourcing.events.read",
            unit: "events",
            description: "Total number of events read from streams");

        _eventsArchived = _meter.CreateCounter<long>(
            "eventsourcing.events.archived",
            unit: "events",
            description: "Total number of events archived to cold storage");

        _eventsDeleted = _meter.CreateCounter<long>(
            "eventsourcing.events.deleted",
            unit: "events",
            description: "Total number of events hard-deleted");

        _snapshotsSaved = _meter.CreateCounter<long>(
            "eventsourcing.snapshots.saved",
            unit: "snapshots",
            description: "Total number of snapshots saved");

        _snapshotsLoaded = _meter.CreateCounter<long>(
            "eventsourcing.snapshots.loaded",
            unit: "snapshots",
            description: "Total number of snapshots loaded");

        _concurrencyConflicts = _meter.CreateCounter<long>(
            "eventsourcing.conflicts",
            unit: "conflicts",
            description: "Total number of concurrency conflicts detected");

        _errors = _meter.CreateCounter<long>(
            "eventsourcing.errors",
            unit: "errors",
            description: "Total number of errors encountered");

        // Initialize histograms
        _appendDuration = _meter.CreateHistogram<double>(
            "eventsourcing.append.duration",
            unit: "ms",
            description: "Duration of event append operations");

        _readDuration = _meter.CreateHistogram<double>(
            "eventsourcing.read.duration",
            unit: "ms",
            description: "Duration of event read operations");

        _archiveDuration = _meter.CreateHistogram<double>(
            "eventsourcing.archive.duration",
            unit: "ms",
            description: "Duration of archive operations");

        _batchSize = _meter.CreateHistogram<int>(
            "eventsourcing.batch.size",
            unit: "events",
            description: "Number of events processed in batch operations");

        _streamVersion = _meter.CreateHistogram<int>(
            "eventsourcing.stream.version",
            unit: "version",
            description: "Stream version distribution");

        // Initialize observable gauges
        _meter.CreateObservableGauge(
            "eventsourcing.connections.active",
            () => new Measurement<long>(_activeConnections, new KeyValuePair<string, object?>("store", _storeName)),
            unit: "connections",
            description: "Number of active database connections");

        _meter.CreateObservableGauge(
            "eventsourcing.transactions.active",
            () => new Measurement<long>(_activeTransactions, new KeyValuePair<string, object?>("store", _storeName)),
            unit: "transactions",
            description: "Number of active database transactions");
    }

    /// <summary>
    /// Records an event append operation with performance metrics.
    /// </summary>
    /// <param name="domain">The domain of the stream</param>
    /// <param name="eventCount">Number of events appended</param>
    /// <param name="durationMs">Duration of the operation in milliseconds</param>
    /// <remarks>
    /// Ultra-fast operation suitable for hot path execution.
    /// Includes tags for domain-level metric aggregation.
    /// </remarks>
    public void RecordAppend(string domain, int eventCount, double durationMs)
    {
        var tags = new TagList
        {
            { "store", _storeName },
            { "domain", domain }
        };

        _eventsAppended.Add(eventCount, tags);
        _appendDuration.Record(durationMs, tags);
        _batchSize.Record(eventCount, tags);
    }

    /// <summary>
    /// Records an event read operation with performance metrics.
    /// </summary>
    /// <param name="domain">The domain of the stream</param>
    /// <param name="eventCount">Number of events read</param>
    /// <param name="durationMs">Duration of the operation in milliseconds</param>
    public void RecordRead(string domain, int eventCount, double durationMs)
    {
        var tags = new TagList
        {
            { "store", _storeName },
            { "domain", domain }
        };

        _eventsRead.Add(eventCount, tags);
        _readDuration.Record(durationMs, tags);
    }

    /// <summary>
    /// Records an archive operation with performance metrics.
    /// </summary>
    /// <param name="domain">The domain of the archived stream</param>
    /// <param name="eventCount">Number of events archived</param>
    /// <param name="durationMs">Duration of the operation in milliseconds</param>
    public void RecordArchive(string domain, int eventCount, double durationMs)
    {
        var tags = new TagList
        {
            { "store", _storeName },
            { "domain", domain }
        };

        _eventsArchived.Add(eventCount, tags);
        _archiveDuration.Record(durationMs, tags);
    }

    /// <summary>
    /// Records events being deleted from the hot store.
    /// </summary>
    /// <param name="domain">The domain of the deleted events</param>
    /// <param name="eventCount">Number of events deleted</param>
    public void RecordDelete(string domain, int eventCount)
    {
        var tags = new TagList
        {
            { "store", _storeName },
            { "domain", domain }
        };

        _eventsDeleted.Add(eventCount, tags);
    }

    /// <summary>
    /// Records a snapshot save operation.
    /// </summary>
    /// <param name="domain">The domain of the stream</param>
    public void RecordSnapshotSaved(string domain)
    {
        var tags = new TagList
        {
            { "store", _storeName },
            { "domain", domain }
        };

        _snapshotsSaved.Add(1, tags);
    }

    /// <summary>
    /// Records a snapshot load operation.
    /// </summary>
    /// <param name="domain">The domain of the stream</param>
    public void RecordSnapshotLoaded(string domain)
    {
        var tags = new TagList
        {
            { "store", _storeName },
            { "domain", domain }
        };

        _snapshotsLoaded.Add(1, tags);
    }

    /// <summary>
    /// Records a concurrency conflict.
    /// </summary>
    /// <param name="domain">The domain where the conflict occurred</param>
    public void RecordConcurrencyConflict(string domain)
    {
        var tags = new TagList
        {
            { "store", _storeName },
            { "domain", domain }
        };

        _concurrencyConflicts.Add(1, tags);
    }

    /// <summary>
    /// Records an error occurrence.
    /// </summary>
    /// <param name="operation">The operation that failed (e.g., "append", "read", "archive")</param>
    /// <param name="errorType">The type of error (e.g., exception type name)</param>
    public void RecordError(string operation, string errorType)
    {
        var tags = new TagList
        {
            { "store", _storeName },
            { "operation", operation },
            { "error_type", errorType }
        };

        _errors.Add(1, tags);
    }

    /// <summary>
    /// Records the current stream version for distribution analysis.
    /// </summary>
    /// <param name="domain">The domain of the stream</param>
    /// <param name="version">The current version of the stream</param>
    public void RecordStreamVersion(string domain, int version)
    {
        var tags = new TagList
        {
            { "store", _storeName },
            { "domain", domain }
        };

        _streamVersion.Record(version, tags);
    }

    /// <summary>
    /// Increments the active connection count.
    /// Call this when opening a database connection.
    /// </summary>
    public void IncrementActiveConnections() => Interlocked.Increment(ref _activeConnections);

    /// <summary>
    /// Decrements the active connection count.
    /// Call this when closing a database connection.
    /// </summary>
    public void DecrementActiveConnections() => Interlocked.Decrement(ref _activeConnections);

    /// <summary>
    /// Increments the active transaction count.
    /// Call this when beginning a database transaction.
    /// </summary>
    public void IncrementActiveTransactions() => Interlocked.Increment(ref _activeTransactions);

    /// <summary>
    /// Decrements the active transaction count.
    /// Call this when committing or rolling back a transaction.
    /// </summary>
    public void DecrementActiveTransactions() => Interlocked.Decrement(ref _activeTransactions);

    /// <summary>
    /// Creates a timer scope for measuring operation duration.
    /// Use with a using statement for automatic duration recording.
    /// </summary>
    /// <param name="onComplete">Action to call with the elapsed milliseconds when disposed</param>
    /// <returns>A disposable scope that measures elapsed time</returns>
    /// <example>
    /// <code>
    /// using (metrics.CreateTimerScope(ms => metrics.RecordAppend(domain, count, ms)))
    /// {
    ///     // Perform operation
    /// }
    /// </code>
    /// </example>
    public IDisposable CreateTimerScope(Action<double> onComplete)
    {
        return new TimerScope(onComplete);
    }

    /// <summary>
    /// Disposes the metrics instance and releases all resources.
    /// </summary>
    public void Dispose()
    {
        _meter?.Dispose();
    }

    /// <summary>
    /// Internal timer scope for measuring operation duration with minimal overhead.
    /// </summary>
    private sealed class TimerScope : IDisposable
    {
        private readonly Action<double> _onComplete;
        private readonly long _startTimestamp;

        public TimerScope(Action<double> onComplete)
        {
            _onComplete = onComplete;
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - _startTimestamp;
            var elapsedMs = (double)elapsedTicks / Stopwatch.Frequency * 1000.0;
            _onComplete(elapsedMs);
        }
    }
}

