using System.Runtime.CompilerServices;
using DRC.EventSourcing.Infrastructure;

namespace DRC.EventSourcing.Sqlite;

/// <summary>
/// Combines hot SQLite events and cold archive events into a single feed.
/// </summary>
public sealed class SqliteCombinedEventFeed<TStore> : BaseCombinedEventFeed, ICombinedEventFeed<TStore> where TStore : SqliteEventStoreOptions, new()
{
    private readonly SqliteArchiveSegmentStore<TStore> _sqliteSegmentStore;

    public SqliteCombinedEventFeed(
        IEventStore hotStore,
        IColdEventArchive coldArchive,
        SqliteArchiveSegmentStore<TStore> segmentStore)
        : base(hotStore, coldArchive, (IArchiveSegmentStore)segmentStore)
    {
        _sqliteSegmentStore = segmentStore;
    }

    public override async IAsyncEnumerable<EventEnvelope> ReadAllForwards(
        GlobalPosition? fromExclusive = null,
        int batchSize = 512,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var activeSegments = await _sqliteSegmentStore.GetActiveSegmentsAsync(ct);

        var cold = FilterColdBySegments(
            _cold.ReadAllForwards(fromExclusive, batchSize, ct), activeSegments, ct);

        var hot = _hot.ReadAllForwards(null, null, fromExclusive, batchSize, ct);

        await foreach (var env in MergeStreams(cold, hot, ct))
        {
            yield return env;
        }
    }
}
