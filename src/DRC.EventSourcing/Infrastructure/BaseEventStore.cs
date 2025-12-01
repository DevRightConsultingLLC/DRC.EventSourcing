using System.Data;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DRC.EventSourcing.Infrastructure
{
    /// <summary>
    /// Abstract base class for event store implementations providing core event sourcing operations.
    /// </summary>
    /// <typeparam name="TOptions">The options type implementing <see cref="IEventStoreOptions"/></typeparam>
    /// <remarks>
    /// <para>This class provides the foundational implementation for event store operations including:</para>
    /// <list type="bullet">
    ///   <item>Optimistic concurrency control via expected version checking</item>
    ///   <item>Transactional append operations with automatic rollback</item>
    ///   <item>Stream reading with namespace filtering</item>
    ///   <item>Forward-only global event feed</item>
    ///   <item>Stream version and position tracking</item>
    /// </list>
    /// <para>Database-specific implementations must override the abstract provider methods.</para>
    /// <para>Performance characteristics:</para>
    /// <list type="bullet">
    ///   <item>Append operations use serializable isolation to prevent conflicts</item>
    ///   <item>Read operations are non-blocking and can be streamed</item>
    ///   <item>Connection pooling is managed by the underlying ADO.NET provider</item>
    ///   <item>Metrics and logging add minimal overhead (sub-millisecond)</item>
    /// </list>
    /// </remarks>
    public abstract class BaseEventStore<TOptions> : IEventStore<TOptions> where TOptions : IEventStoreOptions
    {
        /// <summary>
        /// Gets the connection factory for creating database connections.
        /// </summary>
        protected readonly BaseConnectionFactory<TOptions> ConnectionFactory;
        
        /// <summary>
        /// Gets the configuration options for this event store.
        /// </summary>
        protected readonly TOptions Options;

        private readonly IDomainRetentionPolicyProvider _policyProvider;

        /// <summary>
        /// Gets the logger instance for this event store.
        /// </summary>
        protected readonly ILogger Logger;
        
        /// <summary>
        /// Gets the metrics collector for this event store.
        /// </summary>
        protected readonly EventStoreMetrics Metrics;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseEventStore{TOptions}"/> class.
        /// </summary>
        /// <param name="connectionFactory">Factory for creating database connections</param>
        /// <param name="options">Configuration options for the event store</param>
        /// <param name="policyProvider"></param>
        /// <param name="logger">Logger instance for diagnostic logging</param>
        /// <param name="metrics">Metrics collector for observability (optional)</param>
        /// <exception cref="ArgumentNullException">Thrown if any required parameter is null</exception>
        protected BaseEventStore(
            BaseConnectionFactory<TOptions> connectionFactory, 
            TOptions options,
            IDomainRetentionPolicyProvider policyProvider,
            ILogger logger,
            EventStoreMetrics? metrics = null)
        {
            ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            Options = options ?? throw new ArgumentNullException(nameof(options));
            _policyProvider = policyProvider;
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Metrics = metrics ?? new EventStoreMetrics(options.StoreName);
        }

        /// <summary>
        /// Appends a batch of events to a stream with optimistic concurrency control.
        /// </summary>
        /// <param name="domain">The logical domain the stream belongs to (e.g., "orders", "inventory")</param>
        /// <param name="streamId">The unique identifier for the stream within the domain</param>
        /// <param name="expectedVersion">The expected current version for optimistic concurrency control</param>
        /// <param name="events">The collection of events to append</param>
        /// <param name="ct">Cancellation token to cancel the operation</param>
        /// <returns>A task representing the asynchronous operation</returns>
        /// <exception cref="ArgumentException">Thrown if domain, streamId, or events are invalid</exception>
        /// <exception cref="ConcurrencyException">Thrown if the expected version doesn't match the actual version</exception>
        /// <exception cref="InvalidOperationException">Thrown if the stream is closed or archived</exception>
        /// <remarks>
        /// <para>This method provides transactional, atomic appending of events with the following guarantees:</para>
        /// <list type="bullet">
        ///   <item>All events in the batch are appended together or none are (atomicity)</item>
        ///   <item>Events are assigned sequential version numbers starting from lastVersion + 1</item>
        ///   <item>Each event receives a unique, monotonically increasing global position</item>
        ///   <item>Optimistic concurrency prevents lost updates via version checking</item>
        ///   <item>The stream header is updated atomically with the events</item>
        /// </list>
        /// <para>Expected version semantics:</para>
        /// <list type="bullet">
        ///   <item>0: Expect new stream (will fail if stream exists)</item>
        ///   <item>-1: Any version (append to end regardless of current version - use with caution)</item>
        ///   <item>n: Expect stream to be at exactly version n</item>
        /// </list>
        /// <para>Performance: Typically 1-5ms for small batches. Uses serializable isolation for consistency.</para>
        /// <para>On concurrency conflict, the operation is automatically retried once to get the actual version.</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var events = new[]
        /// {
        ///     new EventData("inventory", "ItemCreated", itemCreatedData),
        ///     new EventData("inventory", "ItemPriceSet", priceSetData)
        /// };
        /// await eventStore.AppendToStream("inventory", "item-123", StreamVersion.New(), events);
        /// </code>
        /// </example>
        public virtual async Task AppendToStream(
            string domain, 
            string streamId, 
            StreamVersion expectedVersion, 
            IReadOnlyList<EventData> events, 
            CancellationToken ct = default)
        {
            // Validation: Ensure all required parameters are provided
            if (string.IsNullOrWhiteSpace(domain)) 
                throw new ArgumentException("Domain must be provided and non-empty", nameof(domain));
            
            if (string.IsNullOrWhiteSpace(streamId)) 
                throw new ArgumentException("StreamId must be provided and non-empty", nameof(streamId));
            
            if (events is null || events.Count == 0) 
                throw new ArgumentException("At least one event must be provided", nameof(events));

            // Additional validation: Check domain and streamId format
            if (domain.Length > 100)
                throw new ArgumentException("Domain cannot exceed 100 characters", nameof(domain));
            
            if (streamId.Length > 200)
                throw new ArgumentException("StreamId cannot exceed 200 characters", nameof(streamId));

            var retentionPolicy = _policyProvider.GetPolicy(domain);

            // Validation: Ensure all events have valid namespaces
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (string.IsNullOrWhiteSpace(e.Namespace))
                {
                    throw new ArgumentException($"Event at index {i} has null or empty Namespace. All events must have a valid namespace.", nameof(events));
                }
                
                if (string.IsNullOrWhiteSpace(e.EventType))
                {
                    throw new ArgumentException($"Event at index {i} has null or empty EventType. All events must have a valid event type.", nameof(events));}
            }

            Logger.AppendingToStream(domain, streamId, expectedVersion.Value, events.Count);

            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                using var conn = ConnectionFactory.CreateConnection();
                Metrics.IncrementActiveConnections();
                
                try
                {
                    if (conn.State == ConnectionState.Closed)
                    {
                        conn.Open();
                    }

                    using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
                    Metrics.IncrementActiveTransactions();
                    
                    try
                    {
                        // Read the current stream head (version, status, position)
                        var head = await ReadStreamHeadAsync(conn, tx, domain, streamId, ct).ConfigureAwait(false);

                        var lastVersion = head?.lastVersion ?? 0;
                        var status = head?.status ?? 0;
                        var lastPosition = head?.lastPosition ?? 0L;
                        var retentionMode=_policyProvider.GetPolicy(domain).RetentionMode;

                        // Validate stream status
                        if (status != 0)
                        {
                            throw new InvalidOperationException($"Stream {domain}/{streamId} is closed or archived (status: {status})");
                        }

                        // Optimistic concurrency check
                        if (expectedVersion.Value != StreamVersion.Any().Value)
                        {
                            if (expectedVersion.Value != lastVersion)
                            {
                                Logger.ConcurrencyConflict(domain, streamId, expectedVersion.Value, lastVersion);
                                Metrics.RecordConcurrencyConflict(domain);
                                throw new ConcurrencyException(streamId, expectedVersion, new StreamVersion(lastVersion));
                            }
                        }

                        var version = lastVersion;
                        long newLastPosition = lastPosition;

                        // Append each event and track the final global position
                        var createdUtc = DateTime.UtcNow;
                        foreach (var e in events)
                        {
                            version++;
                            var gp = await InsertEventAndReturnGlobalPositionAsync(conn, tx, e, domain, streamId, version, createdUtc, ct)
                                .ConfigureAwait(false);
                            
                            if (!gp.HasValue) 
                                throw new InvalidOperationException($"Failed to obtain GlobalPosition from insert for event at version {version}");
                            
                            newLastPosition = gp.Value;
                        }

                        // Update stream header with new version and position
                        await UpsertStreamHeadAsync(conn, tx, domain, streamId,retentionMode, version, newLastPosition, ct).ConfigureAwait(false);

                        tx.Commit();
                        Metrics.DecrementActiveTransactions();

                        stopwatch.Stop();
                        
                        // Record successful metrics
                        Metrics.RecordAppend(domain, events.Count, stopwatch.Elapsed.TotalMilliseconds);
                        Metrics.RecordStreamVersion(domain, version);
                        
                        Logger.AppendedToStream(domain, streamId, version, events.Count);

                        // Warn on slow operations
                        if (stopwatch.ElapsedMilliseconds > 100)
                        {
                            Logger.SlowOperation("AppendToStream", stopwatch.ElapsedMilliseconds, 100);
                        }
                    }
                    catch (Exception ex) when (IsUniqueConstraintViolation(ex))
                    {
                        // Unique constraint violation indicates a concurrent append
                        // Roll back and probe for actual version
                        try 
                        { 
                            tx.Rollback(); 
                            Metrics.DecrementActiveTransactions();
                        } 
                        catch (Exception rollbackEx) 
                        { 
                            Logger.RollbackFailed(rollbackEx, domain, streamId);
                        }

                        // Probe the database to get the actual current version
                        using var probeConn = ConnectionFactory.CreateConnection();
                        if (probeConn.State == ConnectionState.Closed) 
                            probeConn.Open();
                        
                        var probe = await ProbeLatestVersionAsync(probeConn, domain, streamId, ct).ConfigureAwait(false);
                        var actual = new StreamVersion(probe ?? 0);
                        
                        Logger.ConcurrencyConflict(domain, streamId, expectedVersion.Value, actual.Value);
                        Metrics.RecordConcurrencyConflict(domain);
                        
                        throw new ConcurrencyException(streamId, expectedVersion, actual);
                    }
                    catch
                    {
                        // Roll back on any other error
                        try 
                        { 
                            tx.Rollback(); 
                            Metrics.DecrementActiveTransactions();
                        } 
                        catch (Exception rollbackEx) 
                        { 
                            Logger.RollbackFailed(rollbackEx, domain, streamId);
                        }
                        throw;
                    }
                }
                finally
                {
                    Metrics.DecrementActiveConnections();
                }
            }
            catch (Exception ex) when (ex is not ConcurrencyException)
            {
                Logger.AppendError(ex, domain, streamId);
                Metrics.RecordError("append", ex.GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Reads events from a specific stream with optional namespace filtering.
        /// </summary>
        /// <param name="domain">The domain of the stream to read</param>
        /// <param name="streamId">The unique identifier of the stream</param>
        /// <param name="nameSpace">Optional namespace filter; null returns events from all namespaces</param>
        /// <param name="fromVersionInclusive">The stream version to start reading from (inclusive)</param>
        /// <param name="maxCount">Maximum number of events to return</param>
        /// <param name="ct">Cancellation token to cancel the operation</param>
        /// <returns>A read-only list of event envelopes ordered by stream version</returns>
        /// <exception cref="ArgumentException">Thrown if domain or streamId are invalid</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if maxCount is invalid</exception>
        /// <remarks>
        /// <para>This method reads events from a single stream in version order.</para>
        /// <para>The namespace parameter allows filtering events by namespace, useful for reading specific event categories.</para>
        /// <para>Performance: Non-blocking read operation, typically sub-millisecond for small result sets.</para>
        /// <para>For large streams, use pagination by making multiple calls with increasing fromVersion.</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// // Read all events from a stream
        /// var events = await eventStore.ReadStream("orders", "order-123", null, StreamVersion.New(), 1000);
        /// 
        /// // Read only metadata events
        /// var metadata = await eventStore.ReadStream("orders", "order-123", "metadata", StreamVersion.New(), 100);
        /// </code>
        /// </example>
        public virtual async Task<IReadOnlyList<EventEnvelope>> ReadStream(
            string domain, 
            string streamId, 
            string? nameSpace, 
            StreamVersion fromVersionInclusive, 
            int maxCount, 
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(domain)) 
                throw new ArgumentException("Domain must be provided", nameof(domain));
            
            if (string.IsNullOrWhiteSpace(streamId)) 
                throw new ArgumentException("StreamId must be provided", nameof(streamId));
            
            if (maxCount <= 0) 
                throw new ArgumentOutOfRangeException(nameof(maxCount), "MaxCount must be greater than 0");
            
            if (maxCount > 10000) 
                throw new ArgumentOutOfRangeException(nameof(maxCount), "MaxCount cannot exceed 10000 for performance reasons");

            Logger.ReadingStream(domain, streamId, fromVersionInclusive.Value, maxCount);

            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                using var conn = ConnectionFactory.CreateConnection();
                Metrics.IncrementActiveConnections();
                
                try
                {
                    if (conn.State == ConnectionState.Closed) 
                        conn.Open();
                    
                    var result = await ReadStreamRowsAsync(conn, domain, streamId, nameSpace, fromVersionInclusive, maxCount, ct)
                        .ConfigureAwait(false);
                    
                    stopwatch.Stop();
                    
                    Metrics.RecordRead(domain, result.Count, stopwatch.Elapsed.TotalMilliseconds);
                    Logger.ReadStream(domain, streamId, result.Count);
                    
                    return result;
                }
                finally
                {
                    Metrics.DecrementActiveConnections();
                }
            }
            catch (Exception ex)
            {
                Logger.AppendError(ex, domain, streamId);
                Metrics.RecordError("read", ex.GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Reads all events in forward order (by global position) with optional domain and namespace filtering.
        /// </summary>
        /// <param name="domain">Optional domain filter; null includes all domains</param>
        /// <param name="nameSpace">Optional namespace filter; null includes all namespaces</param>
        /// <param name="fromExclusive">Starting global position (exclusive); null starts from the beginning</param>
        /// <param name="batchSize">Number of events to fetch per database round-trip</param>
        /// <param name="ct">Cancellation token to cancel the streaming operation</param>
        /// <returns>An async enumerable stream of event envelopes ordered by global position</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if batchSize is invalid</exception>
        /// <remarks>
        /// <para>This method provides a forward-only cursor over all events in the store.</para>
        /// <para>Events are streamed in batches to minimize memory usage for large event stores.</para>
        /// <para>The enumeration ends when no more events are available.</para>
        /// <para>Performance: Each batch requires one database round-trip. Optimize batchSize for your workload.</para>
        /// <para>Typical batch sizes: 100-1000 for projection building, 10-100 for real-time subscriptions.</para>
        /// <para>The enumeration is safe for long-running operations - connections are opened/closed per batch.</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// await foreach (var evt in eventStore.ReadAllForwards("orders", null, null, 500, ct))
        /// {
        ///     await projectionHandler.Handle(evt, ct);
        /// }
        /// </code>
        /// </example>
        public virtual async IAsyncEnumerable<EventEnvelope> ReadAllForwards(
            string? domain = null, 
            string? nameSpace = null, 
            GlobalPosition? fromExclusive = null, 
            int batchSize = 512, 
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (batchSize <= 0) 
                throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be greater than 0");
            
            if (batchSize > 10000) 
                throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size cannot exceed 10000");

            var position = fromExclusive?.Value ?? -1L; // -1 ensures we start from position 0

            Logger.ReadingAllForwards(position, batchSize);

            while (!ct.IsCancellationRequested)
            {
                using var conn = ConnectionFactory.CreateConnection();
                Metrics.IncrementActiveConnections();
                
                try
                {
                    if (conn.State == ConnectionState.Closed) 
                        conn.Open();

                    var rows = await ReadAllForwardsRowsAsync(conn, domain, nameSpace, position, batchSize, ct)
                        .ConfigureAwait(false);
                    
                    if (rows.Count == 0) 
                        yield break;

                    foreach (var r in rows)
                    {
                        yield return r;
                        position = r.GlobalPosition.Value;
                    }
                }
                finally
                {
                    Metrics.DecrementActiveConnections();
                }
            }
        }

        /// <summary>
        /// Gets the minimum global position currently present in the hot event store.
        /// </summary>
        /// <param name="ct">Cancellation token</param>
        /// <returns>The minimum global position, or null if the store is empty</returns>
        /// <remarks>
        /// <para>This is useful for determining the starting point of the hot store, especially after archival operations.</para>
        /// <para>Returns null if no events exist in the hot store.</para>
        /// </remarks>
        public virtual async Task<GlobalPosition?> GetMinGlobalPosition(CancellationToken ct = default)
        {
            using var conn = ConnectionFactory.CreateConnection();
            Metrics.IncrementActiveConnections();
            
            try
            {
                if (conn.State == ConnectionState.Closed) 
                    conn.Open();
                
                var v = await SelectMinGlobalPositionAsync(conn, ct).ConfigureAwait(false);
                return v.HasValue ? new GlobalPosition(v.Value) : null;
            }
            finally
            {
                Metrics.DecrementActiveConnections();
            }
        }

        /// <summary>
        /// Gets the maximum version of a specific stream.
        /// </summary>
        /// <param name="domain">The domain of the stream</param>
        /// <param name="streamId">The stream identifier</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>The maximum stream version, or null if the stream doesn't exist</returns>
        /// <remarks>
        /// <para>This method queries the event table directly, not the stream head table.</para>
        /// <para>Useful for verifying stream state and detecting stream existence.</para>
        /// </remarks>
        public virtual async Task<int?> GetMaxStreamVersion(string domain, string streamId, CancellationToken ct = default)
        {
            using var conn = ConnectionFactory.CreateConnection();
            Metrics.IncrementActiveConnections();
            
            try
            {
                if (conn.State == ConnectionState.Closed) 
                    conn.Open();
                
                return await SelectMaxStreamVersionAsync(conn, domain, streamId, ct).ConfigureAwait(false);
            }
            finally
            {
                Metrics.DecrementActiveConnections();
            }
        }

        /// <summary>
        /// Gets the stream header metadata for a specific stream.
        /// </summary>
        /// <param name="domain">The domain of the stream</param>
        /// <param name="streamId">The stream identifier</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>The stream header, or null if the stream doesn't exist</returns>
        /// <remarks>
        /// <para>This method queries the stream header table to retrieve metadata including:</para>
        /// <list type="bullet">
        ///   <item>Current version and global position</item>
        ///   <item>Retention mode and deletion status</item>
        ///   <item>Archive information</item>
        /// </list>
        /// </remarks>
        public virtual async Task<StreamHeader?> GetStreamHeader(string domain, string streamId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(domain))
                throw new ArgumentException("Domain must be provided", nameof(domain));
            
            if (string.IsNullOrWhiteSpace(streamId))
                throw new ArgumentException("StreamId must be provided", nameof(streamId));

            using var conn = ConnectionFactory.CreateConnection();
            Metrics.IncrementActiveConnections();
            
            try
            {
                if (conn.State == ConnectionState.Closed)
                    conn.Open();
                
                return await ReadStreamHeaderAsync(conn, domain, streamId, ct).ConfigureAwait(false);
            }
            finally
            {
                Metrics.DecrementActiveConnections();
            }
        }

        /// <summary>
        /// Determines whether an exception represents a unique constraint violation.
        /// </summary>
        /// <param name="ex">The exception to check</param>
        /// <returns>True if the exception is a unique constraint violation; otherwise false</returns>
        /// <remarks>
        /// Uses database-agnostic detection logic defined in <see cref="DbExceptionExtensions"/>.
        /// </remarks>
        protected virtual bool IsUniqueConstraintViolation(Exception ex) => ex.IsUniqueConstraintViolation();

        #region Abstract Provider Methods

        /// <summary>
        /// Reads the stream header information within a transaction.
        /// </summary>
        /// <param name="conn">The database connection</param>
        /// <param name="tx">The active transaction</param>
        /// <param name="domain">The stream domain</param>
        /// <param name="streamId">The stream identifier</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Tuple of (lastVersion, status, lastPosition) or null if stream doesn't exist</returns>
        /// <remarks>
        /// Implementations should use appropriate locking (e.g., UPDLOCK in SQL Server) to prevent concurrent modifications.
        /// </remarks>
        protected abstract Task<(int lastVersion, int status, long lastPosition)?> ReadStreamHeadAsync(
            IDbConnection conn, 
            IDbTransaction tx, 
            string domain, 
            string streamId, 
            CancellationToken ct);

        /// <summary>
        /// Inserts an event and returns its assigned global position.
        /// </summary>
        /// <param name="conn">The database connection</param>
        /// <param name="tx">The active transaction</param>
        /// <param name="e">The event data to insert</param>
        /// <param name="domain">The stream domain</param>
        /// <param name="streamId">The stream identifier</param>
        /// <param name="version">The stream version for this event</param>
        /// <param name="createdUtc">The UTC timestamp for the event</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>The assigned global position</returns>
        /// <remarks>
        /// The global position must be monotonically increasing and unique across all events.
        /// Typically implemented using database sequences or auto-increment columns.
        /// </remarks>
        protected abstract Task<long?> InsertEventAndReturnGlobalPositionAsync(
            IDbConnection conn, 
            IDbTransaction tx, 
            EventData e, 
            string domain, 
            string streamId, 
            int version, 
            DateTime createdUtc, 
            CancellationToken ct);

        /// <summary>
        /// Upserts (inserts or updates) the stream header with new version and position.
        /// </summary>
        /// <param name="conn">The database connection</param>
        /// <param name="tx">The active transaction</param>
        /// <param name="domain">The stream domain</param>
        /// <param name="streamId">The stream identifier</param>
        /// <param name="retentionMode"></param>
        /// <param name="lastVersion">The new last version</param>
        /// <param name="lastPosition">The new last global position</param>
        /// <param name="ct">Cancellation token</param>
        /// <remarks>
        /// Implementations should preserve the stream status when updating (don't reset archived streams to active).
        /// </remarks>
        protected abstract Task UpsertStreamHeadAsync(IDbConnection conn,
            IDbTransaction tx,
            string domain,
            string streamId,
            RetentionMode retentionMode,
            int lastVersion,
            long lastPosition,
            CancellationToken ct);

        /// <summary>
        /// Probes the database for the actual latest version of a stream.
        /// </summary>
        /// <param name="conn">The database connection</param>
        /// <param name="domain">The stream domain</param>
        /// <param name="streamId">The stream identifier</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>The latest version or null if stream doesn't exist</returns>
        /// <remarks>
        /// Used after detecting a concurrency conflict to report the actual version to the caller.
        /// </remarks>
        protected abstract Task<int?> ProbeLatestVersionAsync(
            IDbConnection conn, 
            string domain, 
            string streamId, 
            CancellationToken ct);

        /// <summary>
        /// Reads the full stream header information including all metadata.
        /// </summary>
        /// <param name="conn">The database connection</param>
        /// <param name="domain">The stream domain</param>
        /// <param name="streamId">The stream identifier</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>The stream header or null if stream doesn't exist</returns>
        protected abstract Task<StreamHeader?> ReadStreamHeaderAsync(
            IDbConnection conn,
            string domain,
            string streamId,
            CancellationToken ct);

        /// <summary>
        /// Reads event rows from a specific stream.
        /// </summary>
        protected abstract Task<IReadOnlyList<EventEnvelope>> ReadStreamRowsAsync(
            IDbConnection conn, 
            string domain, 
            string streamId, 
            string? nameSpace, 
            StreamVersion fromVersionInclusive, 
            int maxCount, 
            CancellationToken ct);

        /// <summary>
        /// Reads event rows in forward order by global position.
        /// </summary>
        protected abstract Task<IReadOnlyList<EventEnvelope>> ReadAllForwardsRowsAsync(
            IDbConnection conn, 
            string? domain, 
            string? nameSpace, 
            long positionExclusive, 
            int batchSize, 
            CancellationToken ct);

        /// <summary>
        /// Selects the minimum global position from the events table.
        /// </summary>
        protected abstract Task<long?> SelectMinGlobalPositionAsync(
            IDbConnection conn, 
            CancellationToken ct);

        /// <summary>
        /// Selects the maximum stream version for a specific stream.
        /// </summary>
        protected abstract Task<int?> SelectMaxStreamVersionAsync(
            IDbConnection conn, 
            string domain, 
            string streamId, 
            CancellationToken ct);

        #endregion
    }
}
