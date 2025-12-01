using System.Data;

namespace DRC.EventSourcing.Infrastructure
{
    // Minimal base connection factory to centralize common behavior.
    public abstract class BaseConnectionFactory<TOptions>
    {
        protected readonly TOptions Options;

        protected BaseConnectionFactory(TOptions options)
        {
            Options = options;
            InitializeProviderBatteries();
        }

        // Providers implement creating a concrete IDbConnection
        protected abstract IDbConnection CreateConnectionCore();

        // Public factory method
        public virtual IDbConnection CreateConnection()
        {
            return CreateConnectionCore();
        }

        // Providers can override when special runtime initialization is required (e.g. SQLite)
        protected virtual void InitializeProviderBatteries() { }
    }
}

