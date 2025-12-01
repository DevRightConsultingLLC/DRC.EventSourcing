namespace DRC.EventSourcing;

/// <summary>
/// Represents the unique, monotonically increasing position of an event across all streams in the event store.
/// </summary>
/// <param name="Value">The numeric global position value</param>
/// <remarks>
/// <para><b>GlobalPosition</b> is a value type that encapsulates an event's position in the global event log,
/// providing total ordering of all events across all streams.</para>
/// 
/// <para><b>Key Characteristics:</b></para>
/// <list type="bullet">
///   <item><b>Unique:</b> Each event has exactly one global position</item>
///   <item><b>Monotonic:</b> Positions strictly increase (no gaps or duplicates in sequence)</item>
///   <item><b>Dense:</b> Typically sequential integers (1, 2, 3...) though implementation-specific</item>
///   <item><b>Immutable:</b> Once assigned, an event's global position never changes</item>
/// </list>
/// 
/// <para><b>Global Position vs Stream Version:</b></para>
/// <list type="table">
///   <listheader>
///     <term>Aspect</term>
///     <description>GlobalPosition</description>
///     <description>StreamVersion</description>
///   </listheader>
///   <item>
///     <term>Scope</term>
///     <description>Across ALL streams in the store</description>
///     <description>Within a single stream</description>
///   </item>
///   <item>
///     <term>Purpose</term>
///     <description>Global event ordering, subscriptions</description>
///     <description>Stream consistency, concurrency control</description>
///   </item>
///   <item>
///     <term>Sequence</term>
///     <description>Interleaved across streams</description>
///     <description>Sequential within stream</description>
///   </item>
/// </list>
/// 
/// <para><b>Use Cases:</b></para>
/// <list type="bullet">
///   <item><b>Event subscriptions:</b> Subscribe from a specific global position</item>
///   <item><b>Catch-up projections:</b> Process all events in order from last checkpoint</item>
///   <item><b>Global event ordering:</b> Maintain total order for audit logs</item>
///   <item><b>Archival checkpoints:</b> Mark boundaries for hot/cold storage</item>
///   <item><b>Replication:</b> Track replication progress across systems</item>
/// </list>
/// 
/// <para><b>Database Implementation:</b></para>
/// <list type="bullet">
///   <item><b>SQL Server:</b> IDENTITY column on Events table</item>
///   <item><b>SQLite:</b> AUTOINCREMENT on Events table</item>
///   <item><b>PostgreSQL:</b> SERIAL or BIGSERIAL column</item>
///   <item><b>MySQL:</b> AUTO_INCREMENT column</item>
/// </list>
/// 
/// <para><b>Checkpointing Pattern:</b></para>
/// <para>Store the last processed GlobalPosition to resume after restarts:</para>
/// <code>
/// await foreach (var evt in eventStore.ReadAllForwards(null, null, lastCheckpoint))
/// {
///     await projection.Handle(evt);
///     lastCheckpoint = evt.GlobalPosition;
///     await checkpointStore.Save(lastCheckpoint);
/// }
/// </code>
/// 
/// <para><b>Thread Safety:</b></para>
/// <para>GlobalPosition is an immutable value type and is inherently thread-safe.</para>
/// 
/// <para><b>Performance Considerations:</b></para>
/// <para>GlobalPosition lookups benefit from database indexes. Ensure the Events table has an index on GlobalPosition
/// for optimal performance when reading forwards.</para>
/// </remarks>
/// <example>
/// <para><b>Starting a subscription from a checkpoint:</b></para>
/// <code>
/// var lastPosition = await checkpointStore.Load(); // e.g., GlobalPosition(5432)
/// 
/// await foreach (var evt in eventStore.ReadAllForwards(null, null, lastPosition, 100))
/// {
///     await projectionHandler.Handle(evt);
///     await checkpointStore.Save(evt.GlobalPosition);
/// }
/// </code>
/// 
/// <para><b>Finding the oldest event in hot storage:</b></para>
/// <code>
/// var minPosition = await eventStore.GetMinGlobalPosition();
/// if (minPosition.HasValue)
/// {
///     Console.WriteLine($"Oldest event at position: {minPosition.Value.Value}");
/// }
/// else
/// {
///     Console.WriteLine("Event store is empty");
/// }
/// </code>
/// 
/// <para><b>Filtering events by global position range:</b></para>
/// <code>
/// var startPos = new GlobalPosition(1000);
/// var endPos = new GlobalPosition(2000);
/// 
/// await foreach (var evt in eventStore.ReadAllForwards(null, null, startPos, 500))
/// {
///     if (evt.GlobalPosition.Value > endPos.Value)
///         break;
///     
///     await ProcessEvent(evt);
/// }
/// </code>
/// </example>
public readonly record struct GlobalPosition(long Value);