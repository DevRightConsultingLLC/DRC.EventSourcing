namespace DRC.EventSourcing;

public interface ICombinedEventFeed
{
    /// <summary>
    /// Read events from both cold archive and hot store, merged by GlobalPosition.
    /// Each logical event (by position) is returned at most once.
    /// </summary>
    IAsyncEnumerable<EventEnvelope> ReadAllForwards(
        GlobalPosition? fromExclusive = null,
        int batchSize = 512,
        CancellationToken ct = default);
}