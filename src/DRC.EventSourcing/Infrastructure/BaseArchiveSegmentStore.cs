namespace DRC.EventSourcing.Infrastructure
{
    public abstract class BaseArchiveSegmentStore<TOptions>
    {
        protected readonly BaseConnectionFactory<TOptions> ConnectionFactory;
        protected readonly TOptions Options;

        protected BaseArchiveSegmentStore(BaseConnectionFactory<TOptions> connectionFactory, TOptions options)
        {
            ConnectionFactory = connectionFactory;
            Options = options;
        }

        public abstract Task<IReadOnlyList<ArchiveSegment>> GetActiveSegmentsAsync(CancellationToken ct = default);
    }
}

