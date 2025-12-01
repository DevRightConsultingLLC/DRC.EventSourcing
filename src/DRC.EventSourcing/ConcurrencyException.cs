namespace DRC.EventSourcing;

/// <summary>
/// Exception thrown when an optimistic concurrency conflict is detected during event append operations.
/// </summary>
/// <remarks>
/// <para><b>ConcurrencyException</b> is thrown when attempting to append events to a stream with an expected version
/// that doesn't match the actual current version. This indicates that another process has modified the stream
/// concurrently.</para>
/// 
/// <para><b>Optimistic Concurrency Control:</b></para>
/// <para>Event sourcing uses optimistic concurrency to prevent lost updates:</para>
/// <list type="number">
///   <item>Read events from stream (noting the current version)</item>
///   <item>Apply business logic and generate new events</item>
///   <item>Attempt to append with expected version</item>
///   <item>If version changed, ConcurrencyException is thrown</item>
/// </list>
/// 
/// <para><b>When This Exception Occurs:</b></para>
/// <list type="bullet">
///   <item>Two users edit the same aggregate simultaneously</item>
///   <item>Multiple processes handle commands for the same stream</item>
///   <item>Retry logic attempts to reprocess a command</item>
///   <item>Distributed system has concurrent operations on same aggregate</item>
/// </list>
/// 
/// <para><b>Handling Strategies:</b></para>
/// <list type="number">
///   <item><b>Retry:</b> Reload aggregate state and retry the operation (most common)</item>
///   <item><b>Merge:</b> If possible, merge both changes (requires domain knowledge)</item>
///   <item><b>Report:</b> Return conflict to user for manual resolution</item>
///   <item><b>Fail:</b> Abort the operation (when retry doesn't make sense)</item>
/// </list>
/// 
/// <para><b>Preventing Conflicts:</b></para>
/// <list type="bullet">
///   <item>Keep command handlers fast to reduce time windows</item>
///   <item>Use message deduplication to prevent duplicate processing</item>
///   <item>Consider UI locking for long-running user operations</item>
///   <item>Design aggregates to minimize contention hotspots</item>
/// </list>
/// 
/// <para><b>Performance Impact:</b></para>
/// <para>Concurrency exceptions are normal in high-throughput scenarios. The retry logic
/// typically succeeds on first or second retry. Design for graceful handling rather than
/// treating as exceptional errors.</para>
/// 
/// <para><b>Logging Recommendation:</b></para>
/// <para>Log concurrency conflicts at DEBUG or INFO level, not ERROR, as they're expected
/// operational events in concurrent systems.</para>
/// </remarks>
/// <example>
/// <para><b>Basic retry pattern:</b></para>
/// <code>
/// for (int attempt = 0; attempt &lt; 3; attempt++)
/// {
///     try
///     {
///         var events = await eventStore.ReadStream("orders", orderId, null, StreamVersion.New(), 1000);
///         var aggregate = OrderAggregate.LoadFromHistory(events);
///         
///         var newEvents = aggregate.ProcessCommand(command);
///         
///         await eventStore.AppendToStream(
///             "orders", 
///             orderId, 
///             new StreamVersion(events.Count), 
///             newEvents);
///         
///         return; // Success
///     }
///     catch (ConcurrencyException ex)
///     {
///         if (attempt == 2) throw; // Max retries exceeded
///         
///         Console.WriteLine($"Concurrency conflict on attempt {attempt + 1}, retrying...");
///         await Task.Delay(100 * (attempt + 1)); // Exponential backoff
///     }
/// }
/// </code>
/// 
/// <para><b>Reporting conflict to user:</b></para>
/// <code>
/// try
/// {
///     await eventStore.AppendToStream("orders", orderId, expectedVersion, events);
/// }
/// catch (ConcurrencyException ex)
/// {
///     return new ConflictResult
///     {
///         Message = $"This order was modified by another user. " +
///                   $"Expected version {ex.Expected.Value}, but current version is {ex.Actual.Value}. " +
///                   $"Please refresh and try again.",
///         ExpectedVersion = ex.Expected.Value,
///         ActualVersion = ex.Actual.Value
///     };
/// }
/// </code>
/// 
/// <para><b>Merge strategy (domain-specific):</b></para>
/// <code>
/// try
/// {
///     await eventStore.AppendToStream("orders", orderId, expectedVersion, events);
/// }
/// catch (ConcurrencyException ex)
/// {
///     // Reload and try to merge
///     var latestEvents = await eventStore.ReadStream("orders", orderId, null, expectedVersion, 1000);
///     
///     if (CanBeMerged(events, latestEvents))
///     {
///         var mergedEvents = MergeChanges(events, latestEvents);
///         await eventStore.AppendToStream("orders", orderId, ex.Actual, mergedEvents);
///     }
///     else
///     {
///         throw; // Can't merge, propagate conflict
///     }
/// }
/// </code>
/// 
/// <para><b>Metrics and monitoring:</b></para>
/// <code>
/// try
/// {
///     await eventStore.AppendToStream("orders", orderId, expectedVersion, events);
/// }
/// catch (ConcurrencyException ex)
/// {
///     metrics.IncrementCounter("concurrency_conflicts", new { domain = "orders" });
///     logger.LogInformation(
///         "Concurrency conflict on stream {StreamId}: expected {Expected}, actual {Actual}",
///         ex.StreamId, ex.Expected.Value, ex.Actual.Value);
///     throw;
/// }
/// </code>
/// </example>
public sealed class ConcurrencyException : Exception
{
    /// <summary>
    /// Gets the identifier of the stream where the conflict occurred.
    /// </summary>
    public string StreamId { get; }
    
    /// <summary>
    /// Gets the expected stream version that was specified in the append operation.
    /// </summary>
    public StreamVersion Expected { get; }
    
    /// <summary>
    /// Gets the actual current version of the stream at the time of the conflict.
    /// </summary>
    public StreamVersion Actual { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrencyException"/> class.
    /// </summary>
    /// <param name="streamId">The stream identifier where the conflict occurred</param>
    /// <param name="expected">The expected stream version</param>
    /// <param name="actual">The actual stream version</param>
    /// <param name="message">Optional custom error message; if null, a default message is generated</param>
    public ConcurrencyException(
        string streamId,
        StreamVersion expected,
        StreamVersion actual,
        string? message = null)
        : base(message ?? $"Concurrency conflict on stream '{streamId}': expected version {expected.Value}, actual version {actual.Value}.")
    {
        StreamId = streamId ?? throw new ArgumentNullException(nameof(streamId));
        Expected = expected;
        Actual = actual;
    }
}