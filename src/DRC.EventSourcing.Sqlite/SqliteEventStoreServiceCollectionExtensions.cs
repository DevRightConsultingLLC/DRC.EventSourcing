﻿using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace DRC.EventSourcing.Sqlite;

public static class SqliteEventStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers a SQLite-backed event store for the logical store TStore.
    /// TStore is a strongly-typed options class (inherits SqliteEventStoreOptions).
    /// </summary>
    public static IServiceCollection AddSqliteEventStore<TStore>(this IServiceCollection services, Action<TStore>? configure = null) where TStore : SqliteEventStoreOptions, new()
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
        services.AddSingleton<SqliteConnectionFactory<TStore>>(sp =>
        {
            var opts = sp.GetRequiredService<TStore>();
            return new SqliteConnectionFactory<TStore>(opts);
        });

        // Core services for this store
        services.AddSingleton<IEventStoreSchemaInitializer<TStore>, SqliteSchemaInitializer<TStore>>();
        services.AddSingleton<IEventStoreSchemaInitializer>(sp => sp.GetRequiredService<IEventStoreSchemaInitializer<TStore>>());
        services.AddSingleton<IEventStore<TStore>, SqliteEventStore<TStore>>();
        // Map non-generic IEventStore to this store's implementation (last registration wins)
        services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<IEventStore<TStore>>());
        services.AddSingleton<ISnapshotStore<TStore>, SqliteSnapshotStore<TStore>>();
        services.AddSingleton<IArchiveCoordinator<TStore>>(sp =>
        {
            var factory = sp.GetRequiredService<SqliteConnectionFactory<TStore>>();
            var opts = sp.GetRequiredService<TStore>();
            return new SqliteArchiveCoordinator<TStore>(factory, opts);
        });
        // Register archive cutoff advancer for snapshot coordination
        services.AddSingleton<DRC.EventSourcing.IArchiveCutoffAdvancer>(sp =>
        {
            var factory = sp.GetRequiredService<SqliteConnectionFactory<TStore>>();
            var opts = sp.GetRequiredService<TStore>();
            return new SqliteArchiveCutoffAdvancer<TStore>(factory, opts);
        });
        services.AddSingleton<ICombinedEventFeed<TStore>, SqliteCombinedEventFeed<TStore>>();

        return services;
    }

    /// <summary>
    /// Registers a SQLite-backed event store using a shared in-memory database.
    /// This is a convenience wrapper around <see cref="AddSqliteEventStore{TStore}(IServiceCollection,Action{TStore})"/>.
    /// </summary>
    public static IServiceCollection AddInMemorySqliteEventStore<TStore>(this IServiceCollection services, Action<TStore>? configure = null)
        where TStore : SqliteEventStoreOptions, new()
    {
        return services.AddSqliteEventStore<TStore>(opts =>
        {
            // Ensure StoreName is present (used to name the in-memory DB)
            if (string.IsNullOrWhiteSpace(opts.StoreName))
                opts.StoreName = "InMemory_" + Guid.NewGuid().ToString("N");

            // Request an in-memory DB; the connection factory will translate this into a named shared memory DB
            opts.ConnectionString = ":memory:";

            // Provide a sensible default archive directory if none provided
            if (string.IsNullOrWhiteSpace(opts.ArchiveDirectory))
            {
                var baseDir = Path.Combine(Path.GetTempPath(), "DRC.EventSourcing.Archives");
                Directory.CreateDirectory(baseDir);
                opts.ArchiveDirectory = Path.Combine(baseDir, opts.StoreName);
            }

            configure?.Invoke(opts);
        });
    }
}