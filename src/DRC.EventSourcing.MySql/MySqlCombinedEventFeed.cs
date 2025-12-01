using System.Runtime.CompilerServices;
using DRC.EventSourcing.Infrastructure;

namespace DRC.EventSourcing.MySql;

/// <summary>
/// Combines hot MySQL events and cold archive events into a single feed.
/// </summary>
public sealed class MySqlCombinedEventFeed<TStore> : BaseCombinedEventFeed, ICombinedEventFeed<TStore> 
    where TStore : MySqlEventStoreOptions, new()
{
    private readonly MySqlArchiveSegmentStore<TStore> _mysqlSegmentStore;

    public MySqlCombinedEventFeed(
        IEventStore hotStore,
        IColdEventArchive coldArchive,
        MySqlArchiveSegmentStore<TStore> segmentStore)
        : base(hotStore, coldArchive, segmentStore)
    {
        _mysqlSegmentStore = segmentStore;
    }

    public override async IAsyncEnumerable<EventEnvelope> ReadAllForwards(
        GlobalPosition? fromExclusive = null,
        int batchSize = 512,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var activeSegments = await _mysqlSegmentStore.GetActiveSegmentsAsync(ct);

        var cold = FilterColdBySegments(
            _cold.ReadAllForwards(fromExclusive, batchSize, ct), activeSegments, ct);

        var hot = _hot.ReadAllForwards(null, null, fromExclusive, batchSize, ct);

        await foreach (var evt in MergeStreams(cold, hot, ct))
        {
            yield return evt;
        }
    }
}

