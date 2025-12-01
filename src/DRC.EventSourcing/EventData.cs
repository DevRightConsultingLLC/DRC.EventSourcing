namespace DRC.EventSourcing;

/// <summary>
/// Represents the data for an event to be appended to an event stream.
/// </summary>
/// <param name="Namespace">
/// The logical namespace categorizing this event within a stream.
/// Used to organize and filter events by functional area or purpose.
/// </param>
/// <param name="EventType">
/// The type identifier for this event, typically the event class name or a unique string identifier.
/// </param>
/// <param name="Data">
/// The serialized payload of the event containing the business data.
/// Typically JSON, Protocol Buffers, or other serialization format.
/// </param>
/// <param name="Metadata">
/// Optional metadata associated with the event for auditing, correlation, or other cross-cutting concerns.
/// Can include information like user ID, correlation ID, causation ID, or client information.
/// </param>
/// <remarks>
/// <para><b>EventData</b> is an immutable record representing an event before it's persisted to the event store.</para>
/// 
/// <para><b>Namespace Usage:</b></para>
/// <para>The Namespace property provides logical categorization of events within a stream. Common patterns include:</para>
/// <list type="bullet">
///   <item><b>Functional areas:</b> "metadata", "pricing", "inventory", "location-history"</item>
///   <item><b>Event categories:</b> "domain-events", "integration-events", "notifications"</item>
///   <item><b>Processing hints:</b> "archivable", "critical", "analytics"</item>
/// </list>
/// <para>This allows selective reading and archival of specific event types.</para>
/// 
/// <para><b>Example namespace usage:</b></para>
/// <code>
/// // Metadata events about the entity itself
/// new EventData("metadata", "ItemCreated", itemData)
/// 
/// // Location tracking events
/// new EventData("location-history", "ItemMoved", locationData)
/// 
/// // Pricing events that may need different retention
/// new EventData("pricing", "PriceChanged", priceData)
/// </code>
/// 
/// <para><b>Event Type Conventions:</b></para>
/// <para>Event types should uniquely identify the structure and semantics of the event. Common patterns:</para>
/// <list type="bullet">
///   <item><b>Past tense verbs:</b> "OrderPlaced", "PaymentProcessed", "ItemShipped"</item>
///   <item><b>Versioned types:</b> "OrderPlaced.V1", "OrderPlaced.V2" for evolving schemas</item>
///   <item><b>Namespaced types:</b> "Inventory.ItemReceived", "Sales.OrderCreated"</item>
/// </list>
/// 
/// <para><b>Data Serialization:</b></para>
/// <para>The Data property contains the serialized event payload as a byte array. Common formats:</para>
/// <list type="bullet">
///   <item><b>JSON:</b> Most common, human-readable, flexible schema evolution</item>
///   <item><b>Protocol Buffers:</b> Compact, fast, strong schema contracts</item>
///   <item><b>MessagePack:</b> Compact, fast, JSON-like flexibility</item>
/// </list>
/// <para>Choose based on your requirements for readability, performance, and schema evolution.</para>
/// 
/// <para><b>Metadata Usage:</b></para>
/// <para>Metadata provides context for the event without being part of the business payload. Common metadata:</para>
/// <list type="bullet">
///   <item><b>Correlation ID:</b> Links related events across streams and bounded contexts</item>
///   <item><b>Causation ID:</b> ID of the event or command that caused this event</item>
///   <item><b>User/Actor ID:</b> Identity of the user or system that triggered the event</item>
///   <item><b>Client information:</b> IP address, user agent, client version</item>
///   <item><b>Timestamps:</b> Client-side timestamps, processing timestamps</item>
///   <item><b>Routing keys:</b> Hints for event routing and subscription</item>
/// </list>
/// 
/// <para><b>Example with metadata:</b></para>
/// <code>
/// var metadata = new {
///     CorrelationId = "abc-123",
///     CausationId = "evt-456", 
///     UserId = "user-789",
///     ClientIp = "192.168.1.1"
/// };
/// 
/// var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
/// var eventData = new EventData("orders", "OrderPlaced", orderBytes, metadataBytes);
/// </code>
/// 
/// <para><b>Best Practices:</b></para>
/// <list type="number">
///   <item><b>Immutability:</b> Never modify event data after creation</item>
///   <item><b>Completeness:</b> Include all information needed to understand the event independently</item>
///   <item><b>Size limits:</b> Keep events reasonably small (typically under 1MB)</item>
///   <item><b>Schema versioning:</b> Plan for schema evolution from the start</item>
///   <item><b>Required fields:</b> Always provide Namespace and EventType</item>
/// </list>
/// 
/// <para><b>Thread Safety:</b></para>
/// <para>EventData is an immutable record and is inherently thread-safe for reading.</para>
/// 
/// <para><b>Performance:</b></para>
/// <para>EventData instances are lightweight value types with minimal memory overhead.
/// The Data and Metadata byte arrays are referenced, not copied, until serialization.</para>
/// </remarks>
/// <example>
/// <para><b>Basic event creation:</b></para>
/// <code>
/// var orderData = JsonSerializer.SerializeToUtf8Bytes(new { 
///     OrderId = "ORD-123",
///     CustomerId = "CUST-456",
///     TotalAmount = 99.99m
/// });
/// 
/// var eventData = new EventData(
///     Namespace: "orders",
///     EventType: "OrderPlaced",
///     Data: orderData,
///     Metadata: null
/// );
/// </code>
/// 
/// <para><b>Event with correlation metadata:</b></para>
/// <code>
/// var metadata = JsonSerializer.SerializeToUtf8Bytes(new {
///     CorrelationId = Guid.NewGuid().ToString(),
///     UserId = currentUser.Id,
///     Timestamp = DateTime.UtcNow
/// });
/// 
/// var eventData = new EventData(
///     Namespace: "inventory", 
///     EventType: "ItemReceived",
///     Data: itemData,
///     Metadata: metadata
/// );
/// </code>
/// 
/// <para><b>Batch event creation for atomic append:</b></para>
/// <code>
/// var events = new[]
/// {
///     new EventData("orders", "OrderCreated", orderCreatedData),
///     new EventData("orders", "PaymentRequested", paymentData),
///     new EventData("orders", "InventoryReserved", inventoryData)
/// };
/// 
/// await eventStore.AppendToStream("orders", orderId, StreamVersion.New(), events);
/// </code>
/// </example>
public sealed record EventData(
    string Namespace,
    string EventType,
    byte[] Data,
    byte[]? Metadata = null
);