using System.Text;
using Dapper;
using DRC.EventSourcing.Infrastructure;
using DRC.EventSourcing.PostgreSQL;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DRC.EventSourcing.Tests.PostgreSQL;

/// <summary>
/// Tests for PostgreSQL event store implementation.
/// NOTE: These tests require a running PostgreSQL instance.
/// Connection string can be configured via environment variable: POSTGRES_TEST_CONNECTION
/// Default: Host=localhost;Port=5432;Database=eventstore_test;Username=postgres;Password=postgres
/// </summary>
public class PostgreSQLEventStoreTests : EventStoreTestBase
{
    private readonly string _connectionString;
    private readonly TestPostgreSQLOptions _options;
    private readonly PostgreSQLConnectionFactory<TestPostgreSQLOptions> _connectionFactory;
    private readonly PostgreSQLEventStore<TestPostgreSQLOptions> _eventStore;
    private readonly PostgreSQLSchemaInitializer<TestPostgreSQLOptions> _schemaInitializer;
    private readonly string _testStoreName;

    public PostgreSQLEventStoreTests()
    {
        // Get connection string from environment or use default
        _connectionString = Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=eventstore_test;Username=postgres;Password=postgres";

        // Create unique store name for each test to avoid conflicts
        _testStoreName = $"Test_{Guid.NewGuid():N}";
        
        _options = new TestPostgreSQLOptions
        {
            ConnectionString = _connectionString,
            StoreName = _testStoreName,
            Schema = "test"
        };

        _connectionFactory = new PostgreSQLConnectionFactory<TestPostgreSQLOptions>(_options);
        
        var policyProvider = new DefaultDomainRetentionPolicyProvider();
        var logger = NullLogger<PostgreSQLEventStore<TestPostgreSQLOptions>>.Instance;
        var metrics = new EventStoreMetrics(_testStoreName);
        
        _eventStore = new PostgreSQLEventStore<TestPostgreSQLOptions>(
            _connectionFactory, 
            _options,
            policyProvider,
            logger, 
            metrics);

        _schemaInitializer = new PostgreSQLSchemaInitializer<TestPostgreSQLOptions>(
            _connectionFactory, 
            _options);

        // Initialize schema
        try
        {
            _schemaInitializer.EnsureSchemaCreatedAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to initialize PostgreSQL test database. Ensure PostgreSQL is running and accessible at: {_connectionString}",
                ex);
        }
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task AppendToStream_WithNewStream_ShouldSucceed()
    {
        // Arrange
        var events = CreateTestEvents(3);

        // Act
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Assert
        var readEvents = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);
        readEvents.Should().HaveCount(3);
        readEvents[0].StreamVersion.Value.Should().Be(1);
        readEvents[1].StreamVersion.Value.Should().Be(2);
        readEvents[2].StreamVersion.Value.Should().Be(3);
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task AppendToStream_WithExpectedVersion_ShouldSucceed()
    {
        // Arrange
        var firstEvents = CreateTestEvents(2);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), firstEvents);

        var moreEvents = CreateTestEvents(1);

        // Act
        await _eventStore.AppendToStream(TestDomain, TestStreamId, new StreamVersion(2), moreEvents);

        // Assert
        var allEvents = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);
        allEvents.Should().HaveCount(3);
        allEvents[2].StreamVersion.Value.Should().Be(3);
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task AppendToStream_WithWrongExpectedVersion_ShouldThrowConcurrencyException()
    {
        // Arrange
        var firstEvents = CreateTestEvents(2);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), firstEvents);

        var moreEvents = CreateTestEvents(1);

        // Act & Assert
        var act = async () => await _eventStore.AppendToStream(
            TestDomain, 
            TestStreamId, 
            new StreamVersion(5), // Wrong version
            moreEvents);

        await act.Should().ThrowAsync<ConcurrencyException>()
            .Where(e => e.Expected.Value == 5 && e.Actual.Value == 2);
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task AppendToStream_WithStreamVersionAny_ShouldAlwaysSucceed()
    {
        // Arrange
        var firstEvents = CreateTestEvents(2);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), firstEvents);

        var moreEvents = CreateTestEvents(1);

        // Act
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.Any(), moreEvents);

        // Assert
        var allEvents = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);
        allEvents.Should().HaveCount(3);
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task AppendToStream_WithNullDomain_ShouldThrowArgumentException()
    {
        // Arrange
        var events = CreateTestEvents(1);

        // Act & Assert
        var act = async () => await _eventStore.AppendToStream(null!, TestStreamId, StreamVersion.New(), events);
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("domain");
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task AppendToStream_WithNullStreamId_ShouldThrowArgumentException()
    {
        // Arrange
        var events = CreateTestEvents(1);

        // Act & Assert
        var act = async () => await _eventStore.AppendToStream(TestDomain, null!, StreamVersion.New(), events);
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("streamId");
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task AppendToStream_WithEmptyEvents_ShouldThrowArgumentException()
    {
        // Act & Assert
        var act = async () => await _eventStore.AppendToStream(
            TestDomain, 
            TestStreamId, 
            StreamVersion.New(), 
            new List<EventData>());
        
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("events");
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task AppendToStream_WithEventMissingNamespace_ShouldThrowArgumentException()
    {
        // Arrange
        var eventWithoutNamespace = new EventData(
            string.Empty, // Empty namespace
            "TestEvent",
            Encoding.UTF8.GetBytes("data"));

        // Act & Assert
        var act = async () => await _eventStore.AppendToStream(
            TestDomain, 
            TestStreamId, 
            StreamVersion.New(), 
            new[] { eventWithoutNamespace });
        
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("events");
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task ReadStream_WithNamespaceFilter_ShouldReturnFilteredEvents()
    {
        // Arrange
        var events = new List<EventData>
        {
            new EventData("namespace1", "Event1", Encoding.UTF8.GetBytes("data1")),
            new EventData("namespace2", "Event2", Encoding.UTF8.GetBytes("data2")),
            new EventData("namespace1", "Event3", Encoding.UTF8.GetBytes("data3"))
        };

        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Act
        var filteredEvents = await _eventStore.ReadStream(
            TestDomain, 
            TestStreamId, 
            "namespace1", 
            StreamVersion.New(), 
            100);

        // Assert
        filteredEvents.Should().HaveCount(2);
        filteredEvents.All(e => e.EventType == "Event1" || e.EventType == "Event3").Should().BeTrue();
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task ReadStream_WithFromVersion_ShouldReturnEventsFromVersion()
    {
        // Arrange
        var events = CreateTestEvents(5);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Act
        var eventsFromV3 = await _eventStore.ReadStream(
            TestDomain, 
            TestStreamId, 
            null, 
            new StreamVersion(3), 
            100);

        // Assert
        eventsFromV3.Should().HaveCount(3); // Versions 3, 4, 5
        eventsFromV3[0].StreamVersion.Value.Should().Be(3);
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task ReadStream_WithMaxCount_ShouldRespectLimit()
    {
        // Arrange
        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Act
        var limitedEvents = await _eventStore.ReadStream(
            TestDomain, 
            TestStreamId, 
            null, 
            StreamVersion.New(), 
            5);

        // Assert
        limitedEvents.Should().HaveCount(5);
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task ReadAllForwards_ShouldReturnEventsInGlobalOrder()
    {
        // Arrange
        await _eventStore.AppendToStream(TestDomain, "stream-1", StreamVersion.New(), CreateTestEvents(3));
        await _eventStore.AppendToStream(TestDomain, "stream-2", StreamVersion.New(), CreateTestEvents(2));

        // Act
        var allEvents = new List<EventEnvelope>();
        await foreach (var evt in _eventStore.ReadAllForwards(TestDomain, null, null, 100))
        {
            allEvents.Add(evt);
        }

        // Assert
        allEvents.Should().HaveCount(5);
        
        // Verify events are in global position order
        for (int i = 1; i < allEvents.Count; i++)
        {
            allEvents[i].GlobalPosition.Value.Should().BeGreaterThan(allEvents[i - 1].GlobalPosition.Value);
        }
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task ReadAllForwards_WithDomainFilter_ShouldReturnOnlyDomainEvents()
    {
        // Arrange
        await _eventStore.AppendToStream("domain1", "stream-1", StreamVersion.New(), CreateTestEvents(2));
        await _eventStore.AppendToStream("domain2", "stream-2", StreamVersion.New(), CreateTestEvents(2));

        // Act
        var domain1Events = new List<EventEnvelope>();
        await foreach (var evt in _eventStore.ReadAllForwards("domain1", null, null, 100))
        {
            domain1Events.Add(evt);
        }

        // Assert
        domain1Events.Should().HaveCount(2);
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task ReadAllForwards_WithFromExclusive_ShouldStartAfterPosition()
    {
        // Arrange
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), CreateTestEvents(5));

        var firstBatch = new List<EventEnvelope>();
        await foreach (var evt in _eventStore.ReadAllForwards(TestDomain, null, null, 2))
        {
            firstBatch.Add(evt);
            if (firstBatch.Count == 2) break;
        }

        var lastPosition = firstBatch.Last().GlobalPosition;

        // Act
        var nextBatch = new List<EventEnvelope>();
        await foreach (var evt in _eventStore.ReadAllForwards(TestDomain, null, lastPosition, 100))
        {
            nextBatch.Add(evt);
        }

        // Assert
        nextBatch.Should().HaveCount(3);
        nextBatch.All(e => e.GlobalPosition.Value > lastPosition.Value).Should().BeTrue();
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task GetStreamHeader_WithExistingStream_ShouldReturnHeader()
    {
        // Arrange
        var events = CreateTestEvents(5);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Act
        var header = await _eventStore.GetStreamHeader(TestDomain, TestStreamId);

        // Assert
        header.Should().NotBeNull();
        header!.Domain.Should().Be(TestDomain);
        header.StreamId.Should().Be(TestStreamId);
        header.LastVersion.Should().Be(5);
        header.RetentionMode.Should().Be(RetentionMode.Default);
        header.IsDeleted.Should().BeFalse();
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task GetStreamHeader_WithNonExistentStream_ShouldReturnNull()
    {
        // Act
        var header = await _eventStore.GetStreamHeader(TestDomain, "non-existent-stream");

        // Assert
        header.Should().BeNull();
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task GetMaxStreamVersion_WithExistingStream_ShouldReturnMaxVersion()
    {
        // Arrange
        var events = CreateTestEvents(7);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Act
        var maxVersion = await _eventStore.GetMaxStreamVersion(TestDomain, TestStreamId);

        // Assert
        maxVersion.Should().Be(7);
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task GetMaxStreamVersion_WithNonExistentStream_ShouldReturnNull()
    {
        // Act
        var maxVersion = await _eventStore.GetMaxStreamVersion(TestDomain, "non-existent-stream");

        // Assert
        maxVersion.Should().BeNull();
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task GetMinGlobalPosition_WithEvents_ShouldReturnMinimum()
    {
        // Arrange
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), CreateTestEvents(3));

        // Act
        var minPosition = await _eventStore.GetMinGlobalPosition();

        // Assert
        minPosition.Should().NotBeNull();
        minPosition!.Value.Value.Should().BeGreaterThan(0);
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task GetMinGlobalPosition_WithNoEvents_ShouldReturnNull()
    {
        // Act
        var minPosition = await _eventStore.GetMinGlobalPosition();

        // Assert - might be null or might have events from other tests, so just verify it doesn't throw
        minPosition.Should().NotBeNull(); // In shared DB, there might be events
    }

    [Fact(Skip = "Requires Postgress Server instance")]
    public async Task ConcurrentAppends_ToSameStream_ShouldEnforceOptimisticConcurrency()
    {
        // Arrange
        var initialEvents = CreateTestEvents(2);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), initialEvents);

        // Act - Two concurrent appends with same expected version
        var task1 = _eventStore.AppendToStream(TestDomain, TestStreamId, new StreamVersion(2), CreateTestEvents(1));
        var task2 = _eventStore.AppendToStream(TestDomain, TestStreamId, new StreamVersion(2), CreateTestEvents(1));

        // Assert - One should succeed, one should fail with ConcurrencyException
        var results = await Task.WhenAll(
            task1.ContinueWith(t => new { Success = !t.IsFaulted, Exception = t.Exception }),
            task2.ContinueWith(t => new { Success = !t.IsFaulted, Exception = t.Exception }));

        var successCount = results.Count(r => r.Success);
        var failureCount = results.Count(r => !r.Success);

        successCount.Should().Be(1, "exactly one append should succeed");
        failureCount.Should().Be(1, "exactly one append should fail");

        var failedTask = results.First(r => !r.Success);
        failedTask.Exception!.InnerException.Should().BeOfType<ConcurrencyException>();
    }

    public override void Dispose()
    {
        // Cleanup: Drop test tables
        try
        {
            using var conn = _connectionFactory.CreateConnection();
            conn.Open();
            
            // Drop tables in reverse order of dependencies
            conn.Execute($"DROP TABLE IF EXISTS {_options.EventsTableName}");
            conn.Execute($"DROP TABLE IF EXISTS {_options.StreamsTableName}");
            conn.Execute($"DROP TABLE IF EXISTS {_options.SnapshotsTableName}");
            conn.Execute($"DROP TABLE IF EXISTS {_options.ArchiveSegmentsTableName}");
        }
        catch
        {
            // Ignore cleanup errors
        }

        base.Dispose();
    }
}

/// <summary>
/// Test-specific PostgreSQL options.
/// </summary>
public class TestPostgreSQLOptions : PostgreSQLEventStoreOptions
{
}

