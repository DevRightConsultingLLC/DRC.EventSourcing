using System.Runtime.CompilerServices;
using DRC.EventSourcing.Infrastructure;

namespace DRC.EventSourcing.PostgreSQL;

/// <summary>
/// Combines hot PostgreSQL events and cold archive events into a single feed.
/// </summary>
public sealed class PostgreSQLCombinedEventFeed<TStore> : BaseCombinedEventFeed, ICombinedEventFeed<TStore> 
    where TStore : PostgreSQLEventStoreOptions, new()
{
    private readonly PostgreSQLArchiveSegmentStore<TStore> _postgresSegmentStore;

    public PostgreSQLCombinedEventFeed(
        IEventStore hotStore,
        IColdEventArchive coldArchive,
        PostgreSQLArchiveSegmentStore<TStore> segmentStore)
        : base(hotStore, coldArchive, segmentStore)
    {
        _postgresSegmentStore = segmentStore;
    }

    public override async IAsyncEnumerable<EventEnvelope> ReadAllForwards(
        GlobalPosition? fromExclusive = null,
        int batchSize = 512,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var activeSegments = await _postgresSegmentStore.GetActiveSegmentsAsync(ct);

        var cold = FilterColdBySegments(
            _cold.ReadAllForwards(fromExclusive, batchSize, ct), activeSegments, ct);

        var hot = _hot.ReadAllForwards(null, null, fromExclusive, batchSize, ct);

        await foreach (var evt in MergeStreams(cold, hot, ct))
        {
            yield return evt;
        }
    }
}

