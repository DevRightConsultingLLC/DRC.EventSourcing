using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;

namespace DRC.EventSourcing.PostgreSQL;

/// <summary>
/// Extension methods for configuring PostgreSQL event store services.
/// </summary>
public static class PostgreSQLEventStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers a PostgreSQL-backed event store for the logical store TStore.
    /// </summary>
    public static IServiceCollection AddPostgreSQLEventStore<TStore>(
        this IServiceCollection services, 
        Action<TStore>? configure = null) 
        where TStore : PostgreSQLEventStoreOptions, new()
    {
        // Register default retention policy provider if not already registered
        if (services.All(d => d.ServiceType != typeof(IDomainRetentionPolicyProvider)))
        {
            services.AddSingleton<IDomainRetentionPolicyProvider>(new DefaultDomainRetentionPolicyProvider());
        }

        // Options instance for this store
        services.AddSingleton<TStore>(sp =>
        {
            var opts = new TStore();
            configure?.Invoke(opts);

            // Validate StoreName
            if (string.IsNullOrWhiteSpace(opts.StoreName))
                throw new ArgumentException("StoreName must be provided and non-empty.");

            var validIdentifier = new Regex("^[A-Za-z0-9_]+$");
            if (!validIdentifier.IsMatch(opts.StoreName))
                throw new ArgumentException("StoreName may only contain letters, digits and underscore.");

            return opts;
        });

        // Connection factory depends on TStore options
        services.AddSingleton<PostgreSQLConnectionFactory<TStore>>(sp =>
        {
            var opts = sp.GetRequiredService<TStore>();
            return new PostgreSQLConnectionFactory<TStore>(opts);
        });

        // Core services for this store
        services.AddSingleton<IEventStoreSchemaInitializer<TStore>, PostgreSQLSchemaInitializer<TStore>>();
        services.AddSingleton<IEventStoreSchemaInitializer>(sp => sp.GetRequiredService<IEventStoreSchemaInitializer<TStore>>());
        services.AddSingleton<IEventStore<TStore>, PostgreSQLEventStore<TStore>>();
        services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<IEventStore<TStore>>());
        services.AddSingleton<ISnapshotStore<TStore>, PostgreSQLSnapshotStore<TStore>>();
        services.AddSingleton<PostgreSQLArchiveSegmentStore<TStore>>();
        services.AddSingleton<IArchiveSegmentStore>(sp => sp.GetRequiredService<PostgreSQLArchiveSegmentStore<TStore>>());
        services.AddSingleton<IArchiveCoordinator<TStore>>(sp =>
        {
            var factory = sp.GetRequiredService<PostgreSQLConnectionFactory<TStore>>();
            var opts = sp.GetRequiredService<TStore>();
            return new PostgreSQLArchiveCoordinator<TStore>(factory, opts);
        });
        services.AddSingleton<PostgreSQLCombinedEventFeed<TStore>>();
        services.AddSingleton<ICombinedEventFeed<TStore>>(sp => sp.GetRequiredService<PostgreSQLCombinedEventFeed<TStore>>());

        return services;
    }
}

