namespace DRC.EventSourcing;

public interface ISnapshotStore
{
    Task<Snapshot?> GetLatest(string streamId, CancellationToken ct = default);
    Task Save(Snapshot snapshot, CancellationToken ct = default);
}