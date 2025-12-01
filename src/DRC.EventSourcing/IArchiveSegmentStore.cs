namespace DRC.EventSourcing
{
    public interface IArchiveSegmentStore
    {
        Task<IReadOnlyList<ArchiveSegment>> GetActiveSegmentsAsync(CancellationToken ct = default);
    }
}

