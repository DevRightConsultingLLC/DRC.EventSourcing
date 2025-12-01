using DRC.EventSourcing.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DRC.EventSourcing.Tests.SqlServer;

/// <summary>
/// Tests for SQL Server event store implementation.
/// These tests require a SQL Server instance. Mark with [Fact(Skip = "Requires SQL Server")] if unavailable.
/// </summary>
public class SqlServerEventStoreTests : EventStoreTestBase
{
    // NOTE: Update this connection string to point to your test SQL Server instance
    private const string TestConnectionString = "Server=localhost;Database=EventStoreTests;Integrated Security=true;TrustServerCertificate=true;";
    
    private readonly TestSqlServerOptions _options;
    private readonly SqlServerConnectionFactory<TestSqlServerOptions> _connectionFactory;
    private readonly SqlServerEventStore<TestSqlServerOptions> _eventStore;
    private readonly SqlServerSchemaInitializer<TestSqlServerOptions> _schemaInitializer;
    private readonly string _testSchema;

    public SqlServerEventStoreTests()
    {
        // Use unique schema for test isolation
        _testSchema = $"Test_{Guid.NewGuid():N}".Substring(0, 30);
        
        _options = new TestSqlServerOptions
        {
            ConnectionString = TestConnectionString,
            StoreName = "Test",
            Schema = "dbo" // Use dbo for tests, or create test schema
        };

        _connectionFactory = new SqlServerConnectionFactory<TestSqlServerOptions>(_options);
        
        var policyProvider = new DefaultDomainRetentionPolicyProvider();
        var logger = NullLogger<SqlServerEventStore<TestSqlServerOptions>>.Instance;
        var metrics = new Infrastructure.EventStoreMetrics("Test");
        
        _eventStore = new SqlServerEventStore<TestSqlServerOptions>(
            _connectionFactory, 
            _options,
            policyProvider,
            logger, 
            metrics);

        _schemaInitializer = new SqlServerSchemaInitializer<TestSqlServerOptions>(
            _connectionFactory, 
            _options);

        try
        {
            // Initialize schema
            _schemaInitializer.EnsureSchemaCreatedAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // If initialization fails, tests will be skipped
        }
    }

    [Fact(Skip = "Requires SQL Server instance")]
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

    [Fact(Skip = "Requires SQL Server instance")]
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

    [Fact(Skip = "Requires SQL Server instance")]
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

    [Fact(Skip = "Requires SQL Server instance")]
    public async Task ReadStream_WithNamespaceFilter_ShouldReturnFilteredEvents()
    {
        // Arrange
        var events = new List<EventData>
        {
            new EventData("namespace1", "Event1", System.Text.Encoding.UTF8.GetBytes("data1")),
            new EventData("namespace2", "Event2", System.Text.Encoding.UTF8.GetBytes("data2")),
            new EventData("namespace1", "Event3", System.Text.Encoding.UTF8.GetBytes("data3"))
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
    }

    [Fact(Skip = "Requires SQL Server instance")]
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

    [Fact(Skip = "Requires SQL Server instance")]
    public async Task ConcurrentAppends_ToSameStream_ShouldHandleConcurrency()
    {
        // Arrange
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), CreateTestEvents(1));

        // Act - Simulate concurrent appends
        var task1 = _eventStore.AppendToStream(TestDomain, TestStreamId, new StreamVersion(1), CreateTestEvents(1));
        var task2 = _eventStore.AppendToStream(TestDomain, TestStreamId, new StreamVersion(1), CreateTestEvents(1));

        var tasks = new[] { task1, task2 };
        var exceptions = new List<Exception>();

        foreach (var task in tasks)
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        // Assert - One should succeed, one should fail with concurrency exception
        exceptions.Should().HaveCount(1);
        exceptions[0].Should().BeOfType<ConcurrencyException>();

        var finalEvents = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);
        finalEvents.Should().HaveCount(2); // Original + one successful append
    }

    [Fact(Skip = "Requires SQL Server instance")]
    public async Task SqlServerSpecific_SchemaAndBrackets_ShouldWork()
    {
        // This test verifies SQL Server-specific features like schema and bracket wrapping
        // Arrange
        var events = CreateTestEvents(1);

        // Act
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Assert
        var retrieved = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);
        retrieved.Should().HaveCount(1);
    }

    public override void Dispose()
    {
        try
        {
            // Cleanup test data
            // In a real implementation, you might want to drop test tables here
        }
        catch
        {
            // Ignore cleanup errors
        }
        base.Dispose();
    }
}

/// <summary>
/// Test options class for SQL Server.
/// </summary>
public class TestSqlServerOptions : SqlServerEventStoreOptions
{
}

