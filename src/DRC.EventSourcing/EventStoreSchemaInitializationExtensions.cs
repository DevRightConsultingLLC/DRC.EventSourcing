using Microsoft.Extensions.DependencyInjection;

namespace DRC.EventSourcing;

public static class EventStoreSchemaInitializationExtensions
{
    /// <summary>
    /// Resolves all registered IEventStoreSchemaInitializer instances and runs them once.
    /// Creates a scope so any scoped deps are handled correctly.
    /// </summary>
    public static async Task InitializeEventStoreSchemasAsync(this IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        using var scope = serviceProvider.CreateScope();
        var scopedProvider = scope.ServiceProvider;

        var initializers = scopedProvider.GetServices<IEventStoreSchemaInitializer>();

        foreach (var initializer in initializers)
        {
            await initializer.EnsureSchemaCreatedAsync(cancellationToken);
        }
    }
}