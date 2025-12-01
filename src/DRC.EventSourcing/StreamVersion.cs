namespace DRC.EventSourcing;

/// <summary>
/// Represents the version number of an event stream, used for ordering events and optimistic concurrency control.
/// </summary>
/// <param name="Value">The numeric stream version value</param>
/// <remarks>
/// <para><b>StreamVersion</b> is a value type that encapsulates a stream's version number, providing type safety
/// and semantic clarity for version-based operations.</para>
/// 
/// <para><b>Version Semantics:</b></para>
/// <list type="bullet">
///   <item><b>0:</b> New stream (no events exist yet). Use <see cref="New"/> method.</item>
///   <item><b>1-N:</b> Actual stream versions (N events have been appended)</item>
///   <item><b>-1:</b> "Any" version - append regardless of current version. Use <see cref="Any"/> method with caution.</item>
/// </list>
/// 
/// <para><b>Optimistic Concurrency Control:</b></para>
/// <para>StreamVersion enables optimistic concurrency to prevent lost updates:</para>
/// <list type="number">
///   <item>Read the current stream version when loading an aggregate</item>
///   <item>Make business logic changes in memory</item>
///   <item>Append new events specifying the expected version</item>
///   <item>If another process has appended events concurrently, a <see cref="ConcurrencyException"/> is thrown</item>
/// </list>
/// 
/// <para><b>Version Strategies:</b></para>
/// <list type="bullet">
///   <item><b>StreamVersion.New():</b> Creating a new stream. Fails if stream already exists.</item>
///   <item><b>new StreamVersion(n):</b> Expecting exactly version n. Fails if version doesn't match.</item>
///   <item><b>StreamVersion.Any():</b> Append regardless of version. DANGEROUS - use only for idempotent operations.</item>
/// </list>
/// 
/// <para><b>Why Use StreamVersion.Any() Sparingly:</b></para>
/// <para>StreamVersion.Any() (-1) bypasses concurrency control, which can lead to:</para>
/// <list type="bullet">
///   <item>Lost updates from concurrent modifications</item>
///   <item>Inconsistent aggregate state</item>
///   <item>Business rule violations</item>
///   <item>Difficult-to-debug race conditions</item>
/// </list>
/// <para>Only use Any() for truly idempotent operations (e.g., logging, telemetry) or when you have external locking.</para>
/// 
/// <para><b>Thread Safety:</b></para>
/// <para>StreamVersion is an immutable value type and is inherently thread-safe.</para>
/// </remarks>
/// <example>
/// <para><b>Creating a new stream with concurrency protection:</b></para>
/// <code>
/// var events = new[] { 
///     new EventData("orders", "OrderCreated", orderData) 
/// };
/// 
/// // Expect new stream - will throw if stream exists
/// await eventStore.AppendToStream("orders", "order-123", StreamVersion.New(), events);
/// </code>
/// 
/// <para><b>Appending to existing stream with version check:</b></para>
/// <code>
/// // Load current state
/// var existingEvents = await eventStore.ReadStream("orders", orderId, null, StreamVersion.New(), 1000);
/// var currentVersion = existingEvents.Count; // Last version number
/// 
/// // Apply business logic and generate new events
/// var newEvents = ProcessBusinessLogic(existingEvents);
/// 
/// // Append with expected version - will throw ConcurrencyException if version changed
/// await eventStore.AppendToStream("orders", orderId, new StreamVersion(currentVersion), newEvents);
/// </code>
/// 
/// <para><b>Handling concurrency conflicts:</b></para>
/// <code>
/// try
/// {
///     await eventStore.AppendToStream("orders", orderId, new StreamVersion(5), events);
/// }
/// catch (ConcurrencyException ex)
/// {
///     Console.WriteLine($"Expected version {ex.Expected.Value}, actual version {ex.Actual.Value}");
///     
///     // Option 1: Reload and retry
///     // Option 2: Report conflict to user
///     // Option 3: Merge changes if possible
/// }
/// </code>
/// 
/// <para><b>Using Any() for idempotent logging (use cautiously):</b></para>
/// <code>
/// // Logging event - safe to append without version check
/// var logEvent = new EventData("system", "ActionLogged", logData);
/// await eventStore.AppendToStream("logs", streamId, StreamVersion.Any(), new[] { logEvent });
/// </code>
/// </example>
public readonly record struct StreamVersion(int Value)
{
   /// <summary>
   /// Creates a StreamVersion representing a new stream (version 0).
   /// </summary>
   /// <returns>A StreamVersion with value 0, indicating no events exist yet</returns>
   /// <remarks>
   /// <para>Use this when creating a new stream. If the stream already exists, 
   /// <see cref="IEventStore.AppendToStream"/> will throw a <see cref="ConcurrencyException"/>.</para>
   /// <para>This provides safety against accidentally overwriting an existing stream.</para>
   /// </remarks>
    public static StreamVersion New() => new StreamVersion(0);
    
   /// <summary>
   /// Creates a StreamVersion indicating "any version" - bypasses concurrency control.
   /// </summary>
   /// <returns>A StreamVersion with value -1, indicating no version check should be performed</returns>
   /// <remarks>
   /// <para><b>⚠️ WARNING:</b> This bypasses optimistic concurrency control!</para>
   /// <para>Events will be appended regardless of the current stream version, which can lead to:</para>
   /// <list type="bullet">
   ///   <item>Lost updates from concurrent modifications</item>
   ///   <item>Inconsistent aggregate state</item>
   ///   <item>Business rule violations</item>
   /// </list>
   /// <para><b>Safe use cases:</b></para>
   /// <list type="bullet">
   ///   <item>Idempotent operations (logging, metrics)</item>
   ///   <item>Single-writer scenarios with external locking</item>
   ///   <item>Compensating operations in sagas</item>
   /// </list>
   /// <para><b>Always prefer explicit version checking for business operations.</b></para>
   /// </remarks>
   public static StreamVersion Any() => new StreamVersion(-1);
}

/// <summary>
/// Generic interface marker for event stores bound to specific options types.
/// </summary>
/// <typeparam name="TStore">The options type implementing <see cref="IEventStoreOptions"/></typeparam>
/// <remarks>
/// This interface enables dependency injection of event stores with specific configuration types
/// while maintaining compatibility with the non-generic <see cref="IEventStore"/> interface.
/// </remarks>
public interface IEventStore<TStore> : IEventStore where TStore : IEventStoreOptions
{ }

/// <summary>
/// Generic interface marker for snapshot stores bound to specific options types.
/// </summary>
/// <typeparam name="TStore">The options type implementing <see cref="IEventStoreOptions"/></typeparam>
public interface ISnapshotStore<TStore> : ISnapshotStore
    where TStore : IEventStoreOptions
{ }

/// <summary>
/// Generic interface marker for combined event feeds bound to specific options types.
/// </summary>
/// <typeparam name="TStore">The options type implementing <see cref="IEventStoreOptions"/></typeparam>
public interface ICombinedEventFeed<TStore> : ICombinedEventFeed
    where TStore : IEventStoreOptions
{ }

/// <summary>
/// Generic interface marker for archive coordinators bound to specific options types.
/// </summary>
/// <typeparam name="TStore">The options type implementing <see cref="IEventStoreOptions"/></typeparam>
public interface IArchiveCoordinator<TStore> : IArchiveCoordinator
    where TStore : IEventStoreOptions
{ }

/// <summary>
/// Generic interface marker for schema initializers bound to specific options types.
/// </summary>
/// <typeparam name="TStore">The options type implementing <see cref="IEventStoreOptions"/></typeparam>
public interface IEventStoreSchemaInitializer<TStore> : IEventStoreSchemaInitializer
    where TStore : IEventStoreOptions
{ }