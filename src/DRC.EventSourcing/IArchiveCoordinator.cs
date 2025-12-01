namespace DRC.EventSourcing;

/// <summary>
/// Process thew streams table and archives events based on retention policy.
/// </summary>
public interface IArchiveCoordinator
{
    
    /// <summary>
    /// Archives events based on retention policy.
    /// </summary>
    /// <param name="ct"></param>
    /// <returns></returns>
    Task Archive(CancellationToken ct = default);
}