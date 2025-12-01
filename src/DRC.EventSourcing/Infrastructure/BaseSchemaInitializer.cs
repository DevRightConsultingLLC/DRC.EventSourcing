using System.Data;

namespace DRC.EventSourcing.Infrastructure
{
    public abstract class BaseSchemaInitializer<TOptions>
    {
        protected readonly BaseConnectionFactory<TOptions> ConnectionFactory;
        protected readonly TOptions Options;

        protected BaseSchemaInitializer(BaseConnectionFactory<TOptions> connectionFactory, TOptions options)
        {
            ConnectionFactory = connectionFactory;
            Options = options;
        }

        public virtual async Task EnsureSchemaCreatedAsync(CancellationToken cancellationToken = default)
        {
            using var conn = ConnectionFactory.CreateConnection();
            if (conn.State == ConnectionState.Closed)
            {
                conn.Open();
            }

            var ddl = GenerateCreateTablesSql();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            await Task.Run(() => cmd.ExecuteNonQuery(), cancellationToken);

            await EnsureSchemaColumnsAsync(conn, Options, cancellationToken).ConfigureAwait(false);
        }

        // Provider must supply DDL string for creating tables.
        protected abstract string GenerateCreateTablesSql();

        // Optional provider hook to adjust columns/constraints post-creation.
        protected virtual Task EnsureSchemaColumnsAsync(IDbConnection conn, TOptions options, CancellationToken ct) => Task.CompletedTask;
    }
}

