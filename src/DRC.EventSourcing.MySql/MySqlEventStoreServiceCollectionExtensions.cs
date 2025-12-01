using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;

namespace DRC.EventSourcing.MySql;

/// <summary>
/// Extension methods for configuring MySQL event store services.
/// </summary>
public static class MySqlEventStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers a MySQL-backed event store for the logical store TStore.
    /// </summary>
    public static IServiceCollection AddMySqlEventStore<TStore>(
        this IServiceCollection services, 
        Action<TStore>? configure = null) 
        where TStore : MySqlEventStoreOptions, new()
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

            var validIdentifier = new Regex(@"^[a-zA-Z_][a-zA-Z0-9_]{0,62}$");
            if (!validIdentifier.IsMatch(opts.StoreName))
                throw new ArgumentException("StoreName may only contain letters, digits and underscore, must start with letter or underscore, and be max 63 characters.");

            return opts;
        });

        // Connection factory depends on TStore options
        services.AddSingleton<MySqlConnectionFactory<TStore>>(sp =>
        {
            var opts = sp.GetRequiredService<TStore>();
            return new MySqlConnectionFactory<TStore>(opts);
        });

        // Core services for this store
        services.AddSingleton<IEventStoreSchemaInitializer<TStore>, MySqlSchemaInitializer<TStore>>();
        services.AddSingleton<IEventStoreSchemaInitializer>(sp => sp.GetRequiredService<IEventStoreSchemaInitializer<TStore>>());
        services.AddSingleton<IEventStore<TStore>, MySqlEventStore<TStore>>();
        services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<IEventStore<TStore>>());
        services.AddSingleton<ISnapshotStore<TStore>, MySqlSnapshotStore<TStore>>();
        services.AddSingleton<MySqlArchiveSegmentStore<TStore>>();
        services.AddSingleton<IArchiveSegmentStore>(sp => sp.GetRequiredService<MySqlArchiveSegmentStore<TStore>>());
        services.AddSingleton<IArchiveCoordinator<TStore>>(sp =>
        {
            var factory = sp.GetRequiredService<MySqlConnectionFactory<TStore>>();
            var opts = sp.GetRequiredService<TStore>();
            return new MySqlArchiveCoordinator<TStore>(factory, opts);
        });
        services.AddSingleton<MySqlCombinedEventFeed<TStore>>();
        services.AddSingleton<ICombinedEventFeed<TStore>>(sp => sp.GetRequiredService<MySqlCombinedEventFeed<TStore>>());

        return services;
    }
}

