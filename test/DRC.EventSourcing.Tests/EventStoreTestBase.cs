using FluentAssertions;

namespace DRC.EventSourcing.Tests;

/// <summary>
/// Base class for event store tests providing common test data and utilities.
/// </summary>
public abstract class EventStoreTestBase : IDisposable
{
    protected const string TestDomain = "test-domain";
    protected const string TestStreamId = "test-stream-123";
    protected const string TestNamespace = "test-namespace";

    /// <summary>
    /// Creates sample event data for testing.
    /// </summary>
    protected static EventData CreateTestEvent(string eventType, string data = "test-data")
    {
        var dataBytes = System.Text.Encoding.UTF8.GetBytes(data);
        return new EventData(TestNamespace, eventType, dataBytes);
    }

    /// <summary>
    /// Creates multiple test events.
    /// </summary>
    protected static List<EventData> CreateTestEvents(int count)
    {
        var events = new List<EventData>();
        for (int i = 0; i < count; i++)
        {
            events.Add(CreateTestEvent($"TestEvent{i}", $"data-{i}"));
        }
        return events;
    }

    /// <summary>
    /// Creates an event with metadata.
    /// </summary>
    protected static EventData CreateEventWithMetadata(string eventType, string data, string metadata)
    {
        var dataBytes = System.Text.Encoding.UTF8.GetBytes(data);
        var metadataBytes = System.Text.Encoding.UTF8.GetBytes(metadata);
        return new EventData(TestNamespace, eventType, dataBytes, metadataBytes);
    }

    public virtual void Dispose()
    {
        // Cleanup resources in derived classes
    }
}

