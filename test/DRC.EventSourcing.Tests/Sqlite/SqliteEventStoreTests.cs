using System.Text;
using DRC.EventSourcing.Infrastructure;
using DRC.EventSourcing.Sqlite;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DRC.EventSourcing.Tests.Sqlite;

/// <summary>
/// Tests for SQLite event store implementation.
/// </summary>
public class SqliteEventStoreTests : EventStoreTestBase
{
    private readonly string _dbPath;
    private readonly TestSqliteOptions _options;
    private readonly SqliteConnectionFactory<TestSqliteOptions> _connectionFactory;
    private readonly SqliteEventStore<TestSqliteOptions> _eventStore;
    private readonly SqliteSchemaInitializer<TestSqliteOptions> _schemaInitializer;

    public SqliteEventStoreTests()
    {
        // Create unique database for each test
        _dbPath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.db");
        
        _options = new TestSqliteOptions
        {
            ConnectionString = $"Data Source={_dbPath}",
            StoreName = "Test"
        };

        _connectionFactory = new SqliteConnectionFactory<TestSqliteOptions>(_options);
        
        var policyProvider = new DefaultDomainRetentionPolicyProvider();
        var logger = NullLogger<SqliteEventStore<TestSqliteOptions>>.Instance;
        var metrics = new EventStoreMetrics("Test");
        
        _eventStore = new SqliteEventStore<TestSqliteOptions>(
            _connectionFactory, 
            _options,
            policyProvider,
            logger, 
            metrics);

        _schemaInitializer = new SqliteSchemaInitializer<TestSqliteOptions>(
            _connectionFactory, 
            _options);

        // Initialize schema
        _schemaInitializer.EnsureSchemaCreatedAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task AppendToStream_WithNullDomain_ShouldThrowArgumentException()
    {
        // Arrange
        var events = CreateTestEvents(1);

        // Act & Assert
        var act = async () => await _eventStore.AppendToStream(null!, TestStreamId, StreamVersion.New(), events);
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("domain");
    }

    [Fact]
    public async Task AppendToStream_WithNullStreamId_ShouldThrowArgumentException()
    {
        // Arrange
        var events = CreateTestEvents(1);

        // Act & Assert
        var act = async () => await _eventStore.AppendToStream(TestDomain, null!, StreamVersion.New(), events);
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("streamId");
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task GetMaxStreamVersion_WithExistingStream_ShouldReturnMaxVersion()
    {
        // Arrange
        var events = CreateTestEvents(5);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Act
        var maxVersion = await _eventStore.GetMaxStreamVersion(TestDomain, TestStreamId);

        // Assert
        maxVersion.Should().Be(5);
    }

    [Fact]
    public async Task GetMaxStreamVersion_WithNonExistentStream_ShouldReturnNull()
    {
        // Act
        var maxVersion = await _eventStore.GetMaxStreamVersion(TestDomain, "non-existent-stream");

        // Assert
        maxVersion.Should().BeNull();
    }

    [Fact]
    public async Task GetStreamHeader_WithExistingStream_ShouldReturnHeader()
    {
        // Arrange
        var events = CreateTestEvents(3);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Act
        var header = await _eventStore.GetStreamHeader(TestDomain, TestStreamId);

        // Assert
        header.Should().NotBeNull();
        header!.Domain.Should().Be(TestDomain);
        header.StreamId.Should().Be(TestStreamId);
        header.LastVersion.Should().Be(3);
        header.LastPosition.Should().BeGreaterThan(0);
        header.RetentionMode.Should().Be(RetentionMode.ColdArchivable); // DefaultDomainRetentionPolicyProvider uses ColdArchivable as default
        header.IsDeleted.Should().BeFalse();
        header.ArchivedAtUtc.Should().BeNull();
        header.ArchiveCutoffVersion.Should().BeNull();
    }

    [Fact]
    public async Task GetStreamHeader_WithNonExistentStream_ShouldReturnNull()
    {
        // Act
        var header = await _eventStore.GetStreamHeader(TestDomain, "non-existent-stream");

        // Assert
        header.Should().BeNull();
    }

    [Fact]
    public async Task StreamRetentionMode_ShouldBeSetFromPolicyProvider()
    {
        // Arrange - Create a new event store with a specific policy
        var testDb = Path.Combine(Path.GetTempPath(), $"test-retention-{Guid.NewGuid()}.db");
        var options = new TestSqliteOptions
        {
            ConnectionString = $"Data Source={testDb}",
            StoreName = "Test"
        };

        var connectionFactory = new SqliteConnectionFactory<TestSqliteOptions>(options);
        
        // Configure policy provider with FullHistory for TestDomain
        var policyProvider = new DefaultDomainRetentionPolicyProvider(new[]
        {
            new DomainRetentionPolicy { Domain = TestDomain, RetentionMode = RetentionMode.FullHistory }
        });
        
        var logger = NullLogger<SqliteEventStore<TestSqliteOptions>>.Instance;
        var eventStore = new SqliteEventStore<TestSqliteOptions>(
            connectionFactory, 
            options,
            policyProvider,
            logger);

        var schemaInitializer = new SqliteSchemaInitializer<TestSqliteOptions>(connectionFactory, options);
        await schemaInitializer.EnsureSchemaCreatedAsync(CancellationToken.None);

        try
        {
            // Act - Append events which creates stream with retention mode from policy
            var events = CreateTestEvents(2);
            await eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

            // Assert
            var header = await eventStore.GetStreamHeader(TestDomain, TestStreamId);
            header.Should().NotBeNull();
            header!.RetentionMode.Should().Be(RetentionMode.FullHistory);
        }
        finally
        {
            // Force cleanup of SQLite connections
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            
            if (File.Exists(testDb))
            {
                try { File.Delete(testDb); } catch { /* Ignore if file is still locked */ }
            }
        }
    }

    [Fact]
    public async Task StreamRetentionMode_WithDifferentDomains_ShouldUseDifferentPolicies()
    {
        // Arrange
        var testDb = Path.Combine(Path.GetTempPath(), $"test-multi-policy-{Guid.NewGuid()}.db");
        var options = new TestSqliteOptions
        {
            ConnectionString = $"Data Source={testDb}",
            StoreName = "Test"
        };

        var connectionFactory = new SqliteConnectionFactory<TestSqliteOptions>(options);
        
        // Configure different policies for different domains
        var policyProvider = new DefaultDomainRetentionPolicyProvider(new[]
        {
            new DomainRetentionPolicy { Domain = "orders", RetentionMode = RetentionMode.FullHistory },
            new DomainRetentionPolicy { Domain = "audit", RetentionMode = RetentionMode.Default },
            new DomainRetentionPolicy { Domain = "temp", RetentionMode = RetentionMode.HardDeletable }
        });
        
        var logger = NullLogger<SqliteEventStore<TestSqliteOptions>>.Instance;
        var eventStore = new SqliteEventStore<TestSqliteOptions>(
            connectionFactory, 
            options,
            policyProvider,
            logger);

        var schemaInitializer = new SqliteSchemaInitializer<TestSqliteOptions>(connectionFactory, options);
        await schemaInitializer.EnsureSchemaCreatedAsync(CancellationToken.None);

        try
        {
            // Act - Create streams in different domains
            await eventStore.AppendToStream("orders", "order-1", StreamVersion.New(), CreateTestEvents(1));
            await eventStore.AppendToStream("audit", "audit-1", StreamVersion.New(), CreateTestEvents(1));
            await eventStore.AppendToStream("temp", "temp-1", StreamVersion.New(), CreateTestEvents(1));

            // Assert
            var ordersHeader = await eventStore.GetStreamHeader("orders", "order-1");
            ordersHeader.Should().NotBeNull();
            ordersHeader!.RetentionMode.Should().Be(RetentionMode.FullHistory);

            var auditHeader = await eventStore.GetStreamHeader("audit", "audit-1");
            auditHeader.Should().NotBeNull();
            auditHeader!.RetentionMode.Should().Be(RetentionMode.Default);

            var tempHeader = await eventStore.GetStreamHeader("temp", "temp-1");
            tempHeader.Should().NotBeNull();
            tempHeader!.RetentionMode.Should().Be(RetentionMode.HardDeletable);
        }
        finally
        {
            // Force cleanup of SQLite connections
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            
            if (File.Exists(testDb))
            {
                try { File.Delete(testDb); } catch { /* Ignore if file is still locked */ }
            }
        }
    }

    [Fact]
    public async Task StreamRetentionMode_WithUnknownDomain_ShouldUseDefaultPolicy()
    {
        // Arrange
        var testDb = Path.Combine(Path.GetTempPath(), $"test-default-policy-{Guid.NewGuid()}.db");
        var options = new TestSqliteOptions
        {
            ConnectionString = $"Data Source={testDb}",
            StoreName = "Test"
        };

        var connectionFactory = new SqliteConnectionFactory<TestSqliteOptions>(options);
        
        // Configure policy provider with no specific policy for our test domain
        var policyProvider = new DefaultDomainRetentionPolicyProvider(new[]
        {
            new DomainRetentionPolicy { Domain = "orders", RetentionMode = RetentionMode.FullHistory }
        });
        
        var logger = NullLogger<SqliteEventStore<TestSqliteOptions>>.Instance;
        var eventStore = new SqliteEventStore<TestSqliteOptions>(
            connectionFactory, 
            options,
            policyProvider,
            logger);

        var schemaInitializer = new SqliteSchemaInitializer<TestSqliteOptions>(connectionFactory, options);
        await schemaInitializer.EnsureSchemaCreatedAsync(CancellationToken.None);

        try
        {
            // Act - Create stream in domain with no configured policy
            await eventStore.AppendToStream("unknown-domain", "stream-1", StreamVersion.New(), CreateTestEvents(1));

            // Assert - Should use default policy (ColdArchivable)
            var header = await eventStore.GetStreamHeader("unknown-domain", "stream-1");
            header.Should().NotBeNull();
            header!.RetentionMode.Should().Be(RetentionMode.ColdArchivable); // Default from DefaultDomainRetentionPolicyProvider
        }
        finally
        {
            // Force cleanup of SQLite connections
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            
            if (File.Exists(testDb))
            {
                try { File.Delete(testDb); } catch { /* Ignore if file is still locked */ }
            }
        }
    }

    [Fact]
    public async Task GetStreamHeader_AfterMultipleAppends_ShouldReflectLatestVersion()
    {
        // Arrange
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), CreateTestEvents(3));
        await _eventStore.AppendToStream(TestDomain, TestStreamId, new StreamVersion(3), CreateTestEvents(2));

        // Act
        var header = await _eventStore.GetStreamHeader(TestDomain, TestStreamId);

        // Assert
        header.Should().NotBeNull();
        header!.LastVersion.Should().Be(5);
        header.Domain.Should().Be(TestDomain);
        header.StreamId.Should().Be(TestStreamId);
    }

    [Fact]
    public async Task GetStreamHeader_WithInvalidDomain_ShouldThrowArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await _eventStore.GetStreamHeader("", TestStreamId));
        
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await _eventStore.GetStreamHeader(null!, TestStreamId));
    }

    [Fact]
    public async Task GetStreamHeader_WithInvalidStreamId_ShouldThrowArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await _eventStore.GetStreamHeader(TestDomain, ""));
        
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await _eventStore.GetStreamHeader(TestDomain, null!));
    }

    [Fact]
    public async Task GetMinGlobalPosition_WithEvents_ShouldReturnMinPosition()
    {
        // Arrange
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), CreateTestEvents(3));

        // Act
        var minPosition = await _eventStore.GetMinGlobalPosition();

        // Assert
        minPosition.Should().NotBeNull();
        minPosition!.Value.Value.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetMinGlobalPosition_WithEmptyStore_ShouldReturnNull()
    {
        // Act
        var minPosition = await _eventStore.GetMinGlobalPosition();

        // Assert
        minPosition.Should().BeNull();
    }

    [Fact]
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

    [Fact]
    public async Task EventsWithMetadata_ShouldBeStoredAndRetrieved()
    {
        // Arrange
        var eventWithMetadata = CreateEventWithMetadata("TestEvent", "data", "metadata");

        // Act
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), new[] { eventWithMetadata });

        var retrieved = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);

        // Assert
        retrieved.Should().HaveCount(1);
        retrieved[0].Metadata.Should().NotBeNull();
        Encoding.UTF8.GetString(retrieved[0].Metadata!).Should().Be("metadata");
    }

    [Fact]
    public async Task LongDomain_ShouldThrowArgumentException()
    {
        // Arrange
        var longDomain = new string('a', 101); // > 100 chars
        var events = CreateTestEvents(1);

        // Act & Assert
        var act = async () => await _eventStore.AppendToStream(longDomain, TestStreamId, StreamVersion.New(), events);
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("domain");
    }

    [Fact]
    public async Task LongStreamId_ShouldThrowArgumentException()
    {
        // Arrange
        var longStreamId = new string('a', 201); // > 200 chars
        var events = CreateTestEvents(1);

        // Act & Assert
        var act = async () => await _eventStore.AppendToStream(TestDomain, longStreamId, StreamVersion.New(), events);
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("streamId");
    }

    public override void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
        base.Dispose();
    }
}

/// <summary>
/// Test options class for SQLite.
/// </summary>
public class TestSqliteOptions : SqliteEventStoreOptions
{
}

