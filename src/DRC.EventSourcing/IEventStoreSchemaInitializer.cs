namespace DRC.EventSourcing;

/// <summary>
/// Abstraction for provider-specific schema initialization.
/// Implementations must be safe to call multiple times.
/// </summary>
public interface IEventStoreSchemaInitializer
{
    Task EnsureSchemaCreatedAsync(CancellationToken ct = default);
}