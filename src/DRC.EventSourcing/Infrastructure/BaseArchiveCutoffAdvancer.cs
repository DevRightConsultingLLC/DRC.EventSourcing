namespace DRC.EventSourcing.Infrastructure
{
    public abstract class BaseArchiveCutoffAdvancer<TOptions> : DRC.EventSourcing.IArchiveCutoffAdvancer
    {
        protected readonly BaseConnectionFactory<TOptions> ConnectionFactory;
        protected readonly TOptions Options;

        protected BaseArchiveCutoffAdvancer(BaseConnectionFactory<TOptions> connectionFactory, TOptions options)
        {
            ConnectionFactory = connectionFactory;
            Options = options;
        }

        public abstract Task<bool> TryAdvanceArchiveCutoff(string domain, string streamId, int newCutoff, CancellationToken ct = default);
    }
}
