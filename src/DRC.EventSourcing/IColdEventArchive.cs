namespace DRC.EventSourcing;

public interface IColdEventArchive
{
    /// <summary>
    /// Returns the minimum and maximum GlobalPosition stored in the cold archive, if any.
    /// </summary>
    Task<(GlobalPosition Min, GlobalPosition Max)?> GetRange(CancellationToken ct = default);

    /// <summary>
    /// Reads events from cold storage in ascending GlobalPosition order, starting after fromExclusive.
    /// This may include events from segments that are not yet "active"; higher layers filter.
    /// </summary>
    IAsyncEnumerable<EventEnvelope> ReadAllForwards(
        GlobalPosition? fromExclusive = null,
        int batchSize = 512,
        CancellationToken ct = default);
}