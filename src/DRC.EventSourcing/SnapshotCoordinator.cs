namespace DRC.EventSourcing
{
    public interface ISnapshotCoordinator
    {
        Task SaveSnapshotAndAdvanceCutoff(
            string domain,
            string streamId,
            int snapshotVersion,
            byte[] snapshotData,
            CancellationToken ct = default);
    }

    /// <summary>
    /// A small interface an underlying provider can implement to advance the archive cutoff for a stream.
    /// Implementations must ensure monotonicity (only advance forward) and be idempotent.
    /// </summary>
    public interface IArchiveCutoffAdvancer
    {
        /// <summary>
        /// Try to advance the archive cutoff for a stream to <paramref name="newCutoffVersion"/>.
        /// Returns true if the cutoff was updated; false if the existing cutoff was already >= newCutoffVersion.
        /// </summary>
        Task<bool> TryAdvanceArchiveCutoff(string domain, string streamId, int newCutoffVersion, CancellationToken ct = default);
    }
}

