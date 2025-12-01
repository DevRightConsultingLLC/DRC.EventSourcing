namespace DRC.EventSourcing;

/// <summary>
/// Core interface for event store operations providing event sourcing capabilities.
/// </summary>
/// <remarks>
/// <para><b>IEventStore</b> defines the fundamental operations for storing and retrieving events in an event-sourced system.</para>
/// 
/// <para><b>Core Capabilities:</b></para>
/// <list type="bullet">
///   <item><b>Event Append:</b> Atomically append events to streams with optimistic concurrency control</item>
///   <item><b>Stream Reading:</b> Read events from a specific stream with filtering</item>
///   <item><b>Global Reading:</b> Read all events across streams in order</item>
///   <item><b>Position Tracking:</b> Track minimum global positions for archival</item>
///   <item><b>Version Queries:</b> Query stream versions for consistency checks</item>
/// </list>
/// 
/// <para><b>Event Store Concepts:</b></para>
/// <list type="bullet">
///   <item><b>Domain:</b> Logical grouping of streams (e.g., "orders", "inventory", "users")</item>
///   <item><b>Stream:</b> Sequence of related events identified by StreamId within a domain</item>
///   <item><b>Stream Version:</b> Sequential number within a stream (1, 2, 3...)</item>
///   <item><b>Global Position:</b> Unique position across all streams for total ordering</item>
///   <item><b>Namespace:</b> Categorization of events within a stream for filtering</item>
/// </list>
/// 
/// <para><b>Concurrency Model:</b></para>
/// <para>The event store uses optimistic concurrency control. When appending events, you specify an expected version.
/// If the actual version differs, a <see cref="ConcurrencyException"/> is thrown, allowing you to retry with updated data.</para>
/// 
/// <para><b>Transactional Guarantees:</b></para>
/// <list type="bullet">
///   <item>All events in a batch are appended atomically (all or nothing)</item>
///   <item>Events within a stream are strictly ordered by version</item>
///   <item>Global positions are monotonically increasing</item>
///   <item>Reads are consistent (no torn reads)</item>
/// </list>
/// 
/// <para><b>Performance Characteristics:</b></para>
/// <list type="bullet">
///   <item><b>Append:</b> O(n) where n is the number of events, typically 1-5ms for small batches</item>
///   <item><b>Read Stream:</b> O(n) where n is the number of events read, optimized with indexes</item>
///   <item><b>Read All:</b> Streaming with configurable batch sizes, connection per batch</item>
/// </list>
/// 
/// <para><b>Database Support:</b></para>
/// <para>Implementations exist for: SQLite, SQL Server, PostgreSQL (via providers), MySQL (via providers)</para>
/// 
/// <para><b>Thread Safety:</b></para>
/// <para>All methods are thread-safe and can be called concurrently. The event store handles internal locking and transactions.</para>
/// </remarks>
/// <example>
/// <para><b>Creating a new stream:</b></para>
/// <code>
/// var events = new[]
/// {
///     new EventData("orders", "OrderCreated", orderCreatedData),
///     new EventData("orders", "PaymentRequested", paymentData)
/// };
/// 
/// await eventStore.AppendToStream(
///     domain: "orders",
///     streamId: "order-123",
///     expectedVersion: StreamVersion.New(),
///     events: events);
/// </code>
/// 
/// <para><b>Appending to existing stream with concurrency control:</b></para>
/// <code>
/// var existingEvents = await eventStore.ReadStream("orders", orderId, null, StreamVersion.New(), 1000);
/// var currentVersion = existingEvents.Count;
/// 
/// var newEvent = new EventData("orders", "OrderShipped", shippedData);
/// 
/// await eventStore.AppendToStream(
///     domain: "orders",
///     streamId: orderId,
///     expectedVersion: new StreamVersion(currentVersion),
///     events: new[] { newEvent });
/// </code>
/// 
/// <para><b>Reading a stream with namespace filtering:</b></para>
/// <code>
/// // Read only metadata events
/// var metadataEvents = await eventStore.ReadStream(
///     domain: "orders",
///     streamId: orderId,
///     nameSpace: "metadata",
///     fromVersionInclusive: StreamVersion.New(),
///     maxCount: 100);
/// </code>
/// 
/// <para><b>Building a catch-up subscription:</b></para>
/// <code>
/// var checkpoint = await LoadLastCheckpoint();
/// 
/// await foreach (var evt in eventStore.ReadAllForwards("orders", null, checkpoint, 500))
/// {
///     await projectionHandler.Handle(evt);
///     await SaveCheckpoint(evt.GlobalPosition);
/// }
/// </code>
/// </example>
public interface IEventStore
{
  
    /// <summary>
    /// Append events to a stream. in the store an the stream is identified by a domain and a streamId. 
    /// </summary>
    /// <param name="domain">the logical domain the event belongs to (ie asset,locations,orders) </param>
    /// <param name="streamId">an identifier that links all related events within a domain</param>
    /// <param name="expectedVersion">the version the data is expected to append against  0=new,n=exact version,-1=just append to end </param>
    /// <param name="events">The events to be appended</param>
    /// <param name="ct"></param>
    /// <returns></returns>
    Task AppendToStream(
        string domain,
        string streamId,
        StreamVersion expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken ct = default);

    
    /// <summary>
    /// Read events from a stream.
    /// </summary>
    /// <param name="domain">The domain to read from</param>
    /// <param name="streamId">The id of the stream</param>
    /// <param name="nameSpace">Optional namespace filter</param>
    /// <param name="fromVersionInclusive"></param>
    /// <param name="maxCount"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    Task<IReadOnlyList<EventEnvelope>> ReadStream(
        string domain,
        string streamId,
        string? nameSpace,
        StreamVersion fromVersionInclusive,
        int maxCount,
        CancellationToken ct = default);

    /// <summary>
    /// Reads all events in a forward direction, starting from a specified global position, with optional domain and namespace restrictions.
    /// </summary>
    /// <param name="domain">The logical domain to filter events or null to include events from all domains.</param>
    /// <param name="nameSpace">The namespace to filter events or null to include events from all namespaces.</param>
    /// <param name="fromExclusive">The global position to start reading events from, exclusive. If null, reading begins from the start.</param>
    /// <param name="batchSize">The maximum number of events to read in a single batch.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>An asynchronous stream of event envelopes in the specified order.</returns>
    IAsyncEnumerable<EventEnvelope> ReadAllForwards(string? domain, string? nameSpace, GlobalPosition? fromExclusive = null, int batchSize = 512, CancellationToken ct = default);

    /// <summary>
    /// Returns the minimum GlobalPosition present in the hot store, or null if the store is empty.
    /// </summary>
    Task<GlobalPosition?> GetMinGlobalPosition(CancellationToken ct = default);

    /// <summary>
    /// Retrieves the maximum version of the stream for a given domain and stream identifier in the event store.
    /// </summary>
    /// <param name="domain">The logical domain the stream belongs to (e.g., assets, locations, orders).</param>
    /// <param name="streamId">An identifier for the stream within the specified domain.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>The maximum version of the stream if it exists; otherwise, null.</returns>
    Task<int?> GetMaxStreamVersion(string domain, string streamId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the stream header metadata for a given domain and stream identifier.
    /// </summary>
    /// <param name="domain">The logical domain the stream belongs to (e.g., assets, locations, orders).</param>
    /// <param name="streamId">An identifier for the stream within the specified domain.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>The stream header if the stream exists; otherwise, null.</returns>
    /// <remarks>
    /// <para>The stream header contains metadata about the stream including:</para>
    /// <list type="bullet">
    ///   <item>Current version (number of events)</item>
    ///   <item>Last global position</item>
    ///   <item>Retention mode</item>
    ///   <item>Deletion status</item>
    ///   <item>Archive information</item>
    /// </list>
    /// <para>This method is useful for checking stream state, monitoring, and administrative purposes.</para>
    /// </remarks>
    Task<StreamHeader?> GetStreamHeader(string domain, string streamId, CancellationToken ct = default);
}