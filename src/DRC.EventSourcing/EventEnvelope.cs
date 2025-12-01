namespace DRC.EventSourcing;

/// <summary>
/// Represents a persisted event with its full metadata envelope.
/// </summary>
/// <param name="StreamId">The unique identifier of the stream this event belongs to.</param>
/// <param name="StreamVersion">The sequential version number of this event within its stream.</param>
/// <param name="GlobalPosition">The unique, monotonically increasing position of this event across all streams.</param>
/// <param name="EventType">The type identifier of this event.</param>
/// <param name="Data">The serialized payload of the event. May be null for marker/tombstone events.</param>
/// <param name="Metadata">Optional metadata associated with the event for correlation and auditing.</param>
/// <param name="CreatedUtc">The UTC timestamp when this event was persisted to the event store.</param>
/// <remarks>
/// <para><b>EventEnvelope</b> is the read model of an event after it has been persisted to the event store.</para>
/// <para>It includes both the original event data (<see cref="Data"/> and <see cref="Metadata"/>) and
/// system-assigned metadata (stream version, global position, timestamp).</para>
/// 
/// <para><b>Key Properties:</b></para>
/// <list type="bullet">
///   <item><b>StreamId:</b> Identifies which logical stream this event belongs to within a domain</item>
///   <item><b>StreamVersion:</b> Sequentially numbered starting from 1 within each stream for ordering and concurrency</item>
///   <item><b>GlobalPosition:</b> Unique across ALL events in the store, used for global ordering and subscriptions</item>
///   <item><b>EventType:</b> Identifies the schema/structure of the Data payload</item>
///   <item><b>CreatedUtc:</b> Server-side UTC timestamp when the event was persisted (not when it occurred)</item>
/// </list>
/// 
/// <para><b>StreamVersion vs GlobalPosition:</b></para>
/// <list type="bullet">
///   <item><b>StreamVersion:</b> Sequential within a single stream (1, 2, 3...). Used for reading stream history and optimistic concurrency.</item>
///   <item><b>GlobalPosition:</b> Sequential across all streams (1, 2, 3...). Used for global event ordering, subscriptions, and catch-up projections.</item>
/// </list>
/// <para>A stream might have versions 1-100, but those events might have global positions 532, 534, 540, etc. (interleaved with other streams).</para>
/// 
/// <para><b>Null Data Semantics:</b></para>
/// <para>The Data property is nullable to support special event types:</para>
/// <list type="bullet">
///   <item><b>Marker events:</b> Events that signal state changes without carrying payload (e.g., "StreamArchived")</item>
///   <item><b>Tombstone events:</b> Events indicating deletion or end-of-stream</item>
///   <item><b>Link events:</b> References to events in other streams</item>
/// </list>
/// <para>Most business events will have non-null Data.</para>
/// 
/// <para><b>CreatedUtc Timestamp:</b></para>
/// <para>This timestamp represents when the event was persisted to the database, not necessarily when the business event occurred.
/// If you need business timestamps, include them in the event payload or metadata.</para>
/// 
/// <para><b>Metadata Usage:</b></para>
/// <para>Metadata typically contains cross-cutting concerns:</para>
/// <list type="bullet">
///   <item>Correlation IDs for distributed tracing</item>
///   <item>Causation IDs linking cause-and-effect events</item>
///   <item>User/actor identifiers</item>
///   <item>Client information (IP, user agent)</item>
///   <item>Business timestamps (if different from CreatedUtc)</item>
/// </list>
/// 
/// <para><b>Thread Safety:</b></para>
/// <para>EventEnvelope is an immutable record and is inherently thread-safe.</para>
/// 
/// <para><b>Performance Considerations:</b></para>
/// <para>EventEnvelope is a lightweight value type. The Data and Metadata byte arrays are referenced,
/// not copied, keeping memory overhead minimal.</para>
/// </remarks>
/// <example>
/// <para><b>Reading events from a stream:</b></para>
/// <code>
/// var events = await eventStore.ReadStream("orders", "order-123", null, StreamVersion.New(), 100);
/// 
/// foreach (var envelope in events)
/// {
///     Console.WriteLine($"Event {envelope.EventType} at version {envelope.StreamVersion.Value}");
///     Console.WriteLine($"Global position: {envelope.GlobalPosition.Value}");
///     Console.WriteLine($"Created: {envelope.CreatedUtc}");
///     
///     // Deserialize the payload
///     var eventData = JsonSerializer.Deserialize&lt;OrderEvent&gt;(envelope.Data);
/// }
/// </code>
/// 
/// <para><b>Global event subscription:</b></para>
/// <code>
/// await foreach (var envelope in eventStore.ReadAllForwards("orders", null, lastPosition, 100))
/// {
///     // Process event in global order
///     await projectionBuilder.Handle(envelope);
///     
///     // Save checkpoint for resume after restart
///     await checkpointStore.Save(envelope.GlobalPosition);
/// }
/// </code>
/// 
/// <para><b>Checking for specific event types:</b></para>
/// <code>
/// var events = await eventStore.ReadStream("orders", orderId, null, StreamVersion.New(), 1000);
/// 
/// var orderPlaced = events.FirstOrDefault(e => e.EventType == "OrderPlaced");
/// if (orderPlaced != null)
/// {
///     var data = JsonSerializer.Deserialize&lt;OrderPlacedEvent&gt;(orderPlaced.Data);
///     Console.WriteLine($"Order placed at: {orderPlaced.CreatedUtc}");
/// }
/// </code>
/// </example>
public sealed record EventEnvelope(
    string StreamId,
    StreamVersion StreamVersion,
    GlobalPosition GlobalPosition,
    string EventType,
    byte[]? Data,
    byte[]? Metadata,
    DateTime CreatedUtc
);