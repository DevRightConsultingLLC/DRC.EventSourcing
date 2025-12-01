namespace DRC.EventSourcing.Infrastructure
{
    /// <summary>
    /// Abstract base class for combining hot (database) and cold (archive) event feeds into a unified stream.
    /// </summary>
    /// <remarks>
    /// <para>This class merges events from two sources:</para>
    /// <list type="bullet">
    ///   <item><b>Hot store:</b> Recent events stored in the database for fast access</item>
    ///   <item><b>Cold archive:</b> Older events stored in NDJSON files for long-term retention</item>
    /// </list>
    /// <para>The merge algorithm ensures:</para>
    /// <list type="bullet">
    ///   <item>Events are returned in strict GlobalPosition order</item>
    ///   <item>No duplicate events (each GlobalPosition appears exactly once)</item>
    ///   <item>Seamless transition between cold and hot sources</item>
    ///   <item>Memory-efficient streaming (no large buffers)</item>
    /// </list>
    /// <para>Archive segments are filtered to only include active (successfully archived) segments,
    /// preventing reads from incomplete or failed archive operations.</para>
    /// </remarks>
    public abstract class BaseCombinedEventFeed
    {
        /// <summary>
        /// Gets the hot event store containing recent events.
        /// </summary>
        protected readonly IEventStore _hot;
        
        /// <summary>
        /// Gets the cold event archive containing historical events.
        /// </summary>
        protected readonly IColdEventArchive _cold;
        
        /// <summary>
        /// Gets the archive segment store for tracking which events are archived.
        /// </summary>
        protected readonly IArchiveSegmentStore _segmentStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseCombinedEventFeed"/> class.
        /// </summary>
        /// <param name="hot">The hot event store</param>
        /// <param name="cold">The cold event archive</param>
        /// <param name="segmentStore">The archive segment store</param>
        /// <exception cref="ArgumentNullException">Thrown if any parameter is null</exception>
        protected BaseCombinedEventFeed(IEventStore hot, IColdEventArchive cold, IArchiveSegmentStore segmentStore)
        {
            _hot = hot ?? throw new ArgumentNullException(nameof(hot));
            _cold = cold ?? throw new ArgumentNullException(nameof(cold));
            _segmentStore = segmentStore ?? throw new ArgumentNullException(nameof(segmentStore));
        }

        /// <summary>
        /// Reads all events forwards from the combined hot and cold sources.
        /// </summary>
        /// <param name="fromExclusive">Starting position (exclusive); null to start from beginning</param>
        /// <param name="batchSize">Number of events to fetch per batch</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Unified stream of events ordered by GlobalPosition</returns>
        /// <remarks>
        /// Implementations must query active archive segments and merge hot/cold streams appropriately.
        /// </remarks>
        public abstract IAsyncEnumerable<EventEnvelope> ReadAllForwards(
            GlobalPosition? fromExclusive = null, 
            int batchSize = 512, 
            CancellationToken ct = default);

        /// <summary>
        /// Merges two ordered async streams of events by GlobalPosition, ensuring no duplicates.
        /// </summary>
        /// <param name="a">First event stream (e.g., cold archive)</param>
        /// <param name="b">Second event stream (e.g., hot store)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Merged stream with events ordered by GlobalPosition, no duplicates</returns>
        /// <remarks>
        /// <para>Merge algorithm:</para>
        /// <list type="number">
        ///   <item>Compare the current event from each stream</item>
        ///   <item>Yield the event with the smaller GlobalPosition</item>
        ///   <item>If positions are equal, prefer stream 'a' and skip stream 'b' (deduplication)</item>
        ///   <item>Advance the stream that was yielded</item>
        ///   <item>Repeat until both streams are exhausted</item>
        /// </list>
        /// <para>This ensures each GlobalPosition appears exactly once in the output.</para>
        /// <para>Performance: O(n) where n is the total number of events, minimal memory overhead.</para>
        /// </remarks>
        protected async IAsyncEnumerable<EventEnvelope> MergeStreams(
            IAsyncEnumerable<EventEnvelope> a, 
            IAsyncEnumerable<EventEnvelope> b, 
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await using var ae = a.GetAsyncEnumerator(ct);
            await using var be = b.GetAsyncEnumerator(ct);

            var hasA = await ae.MoveNextAsync();
            var hasB = await be.MoveNextAsync();

            while (hasA || hasB)
            {
                ct.ThrowIfCancellationRequested();

                // If only stream A has data, yield from A
                if (hasA && !hasB)
                {
                    yield return ae.Current;
                    hasA = await ae.MoveNextAsync();
                }
                // If only stream B has data, yield from B
                else if (!hasA && hasB)
                {
                    yield return be.Current;
                    hasB = await be.MoveNextAsync();
                }
                // Both streams have data - compare positions
                else if (hasA && hasB)
                {
                    var posA = ae.Current.GlobalPosition.Value;
                    var posB = be.Current.GlobalPosition.Value;

                    if (posA < posB)
                    {
                        // Stream A has the earlier event
                        yield return ae.Current;
                        hasA = await ae.MoveNextAsync();
                    }
                    else if (posA > posB)
                    {
                        // Stream B has the earlier event
                        yield return be.Current;
                        hasB = await be.MoveNextAsync();
                    }
                    else
                    {
                        // Same position - duplicate event (happens during archive transition)
                        // Yield from stream A (cold archive - authoritative source)
                        // Skip stream B to avoid duplicate
                        yield return ae.Current;
                        hasA = await ae.MoveNextAsync();
                        hasB = await be.MoveNextAsync(); // Skip the duplicate in stream B
                    }
                }
            }
        }

        /// <summary>
        /// Filters cold archive events to only include those within active archive segment ranges.
        /// </summary>
        /// <param name="source">Source stream of events from cold archive</param>
        /// <param name="segments">List of active archive segments</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Filtered stream containing only events within active segments</returns>
        /// <remarks>
        /// <para>This prevents returning events from incomplete or failed archive operations.</para>
        /// <para>An event is included if its GlobalPosition falls within any segment's [MinPosition, MaxPosition] range.</para>
        /// <para>Segments are assumed to be non-overlapping in production scenarios.</para>
        /// <para>Performance: O(n * m) where n = events, m = segments. Optimized for small segment lists.</para>
        /// </remarks>
        protected async IAsyncEnumerable<EventEnvelope> FilterColdBySegments(
            IAsyncEnumerable<EventEnvelope> source, 
            IReadOnlyList<ArchiveSegment> segments, 
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            // Build sorted ranges for efficient lookup
            var ranges = segments
                .Select(s => (Min: s.MinPosition.Value, Max: s.MaxPosition.Value))
                .OrderBy(r => r.Min)
                .ToArray();

            if (ranges.Length == 0)
                yield break; // No active segments, no events to return

            await foreach (var e in source.WithCancellation(ct))
            {
                var pos = e.GlobalPosition.Value;
                
                // Check if this event's position falls within any active segment
                var inActiveSegment = false;
                foreach (var range in ranges)
                {
                    if (pos >= range.Min && pos <= range.Max)
                    {
                        inActiveSegment = true;
                        break;
                    }
                    
                    // Early exit optimization: if position is before this range, check next range
                    // Ranges are sorted, so if we're past the current range, no point checking earlier ones
                    if (pos < range.Min)
                        break;
                }

                if (inActiveSegment)
                    yield return e;
            }
        }
    }
}
