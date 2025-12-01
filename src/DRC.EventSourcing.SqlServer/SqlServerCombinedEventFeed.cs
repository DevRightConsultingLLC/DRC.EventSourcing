using System.Runtime.CompilerServices;
using DRC.EventSourcing.Infrastructure;

namespace DRC.EventSourcing.SqlServer;

/// <summary>
/// Combines events from the SQL Server hot store and the file-based cold archive,
/// using ArchiveSegments in SQL Server to decide which cold ranges are active.
/// </summary>
public sealed class SqlServerCombinedEventFeed<TStore> : BaseCombinedEventFeed, ICombinedEventFeed<TStore> where TStore : SqlServerEventStoreOptions
{
    private readonly SqlServerArchiveSegmentStore<TStore> _serverSegmentStore;

    public SqlServerCombinedEventFeed(
        IEventStore hotStore,
        IColdEventArchive coldArchive,
        SqlServerArchiveSegmentStore<TStore> segmentStore)
        : base(hotStore, coldArchive, (IArchiveSegmentStore)segmentStore)
    {
        _serverSegmentStore = segmentStore;
    }

    public override async IAsyncEnumerable<EventEnvelope> ReadAllForwards(
        GlobalPosition? fromExclusive = null,
        int batchSize = 512,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var activeSegments = await _serverSegmentStore.GetActiveSegmentsAsync(ct);

        var cold = FilterColdBySegments(_cold.ReadAllForwards(fromExclusive, batchSize, ct), activeSegments, ct);

        var hot = _hot.ReadAllForwards(null, null, fromExclusive, batchSize, ct);

        await foreach (var env in MergeStreams(cold, hot, ct))
        {
            yield return env;
        }
    }
}
