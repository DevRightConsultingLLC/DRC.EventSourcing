namespace DRC.EventSourcing.Infrastructure
{
    public abstract class BaseSnapshotStore<TOptions>
    {
        protected readonly BaseConnectionFactory<TOptions> ConnectionFactory;
        protected readonly TOptions Options;

        protected BaseSnapshotStore(BaseConnectionFactory<TOptions> connectionFactory, TOptions options)
        {
            ConnectionFactory = connectionFactory;
            Options = options;
        }

        public virtual Task<Snapshot?> GetLatest(string streamId, CancellationToken ct = default)
            => ProviderGetLatestAsync(streamId, ct);

        public virtual Task Save(Snapshot snapshot, CancellationToken ct = default)
            => ProviderSaveAsync(snapshot, ct);

        protected abstract Task<Snapshot?> ProviderGetLatestAsync(string streamId, CancellationToken ct);
        protected abstract Task ProviderSaveAsync(Snapshot snapshot, CancellationToken ct);
    }
}
