using Microsoft.Extensions.DependencyInjection;

namespace DRC.EventSourcing.Infrastructure
{
    public static class EventStoreServiceCollectionExtensions
    {
        // Minimal helper used by providers to register common services.
        public static IServiceCollection RegisterEventStoreCore<TOptions>(this IServiceCollection services, Action<TOptions>? configure = null)
            where TOptions : class, new()
        {
            var options = new TOptions();
            configure?.Invoke(options);

            services.AddSingleton(options);
            return services;
        }
    }
}

