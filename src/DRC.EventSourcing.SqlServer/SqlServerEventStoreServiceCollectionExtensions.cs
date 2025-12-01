﻿using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace DRC.EventSourcing.SqlServer;

public static class SqlServerEventStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers a SQL Server-backed event store for the logical store TStore.
    /// TStore is a strongly-typed options class (inherits SqlServerEventStoreOptions).
    /// </summary>
    public static IServiceCollection AddSqlServerEventStore<TStore>(this IServiceCollection services, Action<TStore>? configure = null) where TStore : SqlServerEventStoreOptions, new()
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

            // Validate StoreName and Schema
            if (string.IsNullOrWhiteSpace(opts.StoreName))
                throw new ArgumentException("StoreName must be provided and non-empty.");

            var validIdentifier = new Regex("^[A-Za-z0-9_]+$");
            if (!validIdentifier.IsMatch(opts.StoreName))
                throw new ArgumentException("StoreName may only contain letters, digits and underscore.");

            if (string.IsNullOrWhiteSpace(opts.Schema))
                throw new ArgumentException("Schema must be provided and non-empty.");

            if (!validIdentifier.IsMatch(opts.Schema))
                throw new ArgumentException("Schema may only contain letters, digits and underscore.");

            return opts;
        });

        // Connection factory depends on TStore options
        services.AddSingleton<SqlServerConnectionFactory<TStore>>(sp =>
        {
            var opts = sp.GetRequiredService<TStore>();
            return new SqlServerConnectionFactory<TStore>(opts);
        });

        // Core services for this store
        services.AddSingleton<IEventStoreSchemaInitializer<TStore>, SqlServerSchemaInitializer<TStore>>();
        services.AddSingleton<IEventStoreSchemaInitializer>(sp => sp.GetRequiredService<IEventStoreSchemaInitializer<TStore>>());
        services.AddSingleton<IEventStore<TStore>, SqlServerEventStore<TStore>>();
        services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<IEventStore<TStore>>());
        services.AddSingleton<ISnapshotStore<TStore>, SqlServerSnapshotStore<TStore>>();
        services.AddSingleton<IArchiveCoordinator<TStore>>(sp =>
        {
            var factory = sp.GetRequiredService<SqlServerConnectionFactory<TStore>>();
            var opts = sp.GetRequiredService<TStore>();
            return new SqlServerArchiveCoordinator<TStore>(factory, opts);
        });
        // Register archive cutoff advancer for snapshot coordination
        services.AddSingleton<DRC.EventSourcing.IArchiveCutoffAdvancer>(sp =>
        {
            var factory = sp.GetRequiredService<SqlServerConnectionFactory<TStore>>();
            var opts = sp.GetRequiredService<TStore>();
            return new SqlServerArchiveCutoffAdvancer<TStore>(factory, opts);
        });
        services.AddSingleton<ICombinedEventFeed<TStore>, SqlServerCombinedEventFeed<TStore>>();

        return services;
    }
}