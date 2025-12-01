using System.Text;
using Dapper;
using DRC.EventSourcing.MySql;
using DRC.EventSourcing.Sqlite;
using DRC.EventSourcing.Tests.Sqlite;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace DRC.EventSourcing.Tests.MySql;

/// <summary>
/// Tests for MySQL archive coordinator functionality including cold/hot storage archival,
/// retention policies, and the combined event feed.
/// NOTE: These tests require a running MySQL/MariaDB instance.
/// </summary>
public class MySqlArchiveTests : EventStoreTestBase
{
    private readonly string _connectionString;
    private readonly string _archiveDirectory;
    private readonly TestMySqlOptions _options;
    private readonly MySqlConnectionFactory<TestMySqlOptions> _connectionFactory;
    private readonly MySqlEventStore<TestMySqlOptions> _eventStore;
    private readonly MySqlArchiveCoordinator<TestMySqlOptions> _archiveCoordinator;
    private readonly MySqlSnapshotStore<TestMySqlOptions> _snapshotStore;
    private readonly TestSnapshotCoordinator _snapshotCoordinator;
    private readonly MySqlArchiveCutoffAdvancer<TestMySqlOptions> _cutoffAdvancer;
    private readonly FileColdEventArchive _coldArchive;
    private readonly MySqlArchiveSegmentStore<TestMySqlOptions> _segmentStore;
    private readonly MySqlCombinedEventFeed<TestMySqlOptions> _combinedFeed;
    private readonly DefaultDomainRetentionPolicyProvider _policyProvider;
    private readonly string _testStoreName;

    public MySqlArchiveTests()
    {
        // Get connection string from environment or use default
        _connectionString = Environment.GetEnvironmentVariable("MYSQL_TEST_CONNECTION")
            ?? "Server=localhost;Port=3306;Database=eventstore_test;User=root;Password=root";

        // Create unique store name and archive directory for each test
        _testStoreName = $"Test_{Guid.NewGuid():N}";
        _archiveDirectory = Path.Combine(Path.GetTempPath(), $"test-archive-{Guid.NewGuid()}");
        Directory.CreateDirectory(_archiveDirectory);
        
        _options = new TestMySqlOptions
        {
            ConnectionString = _connectionString,
            StoreName = _testStoreName,
            ArchiveDirectory = _archiveDirectory
        };

        _connectionFactory = new MySqlConnectionFactory<TestMySqlOptions>(_options);
        
        _policyProvider = new DefaultDomainRetentionPolicyProvider();
        
        var logger = NullLogger<MySqlEventStore<TestMySqlOptions>>.Instance;
        
        _eventStore = new MySqlEventStore<TestMySqlOptions>(
            _connectionFactory, 
            _options,
            _policyProvider,
            logger);

        var schemaInitializer = new MySqlSchemaInitializer<TestMySqlOptions>(
            _connectionFactory, 
            _options);

        _archiveCoordinator = new MySqlArchiveCoordinator<TestMySqlOptions>(
            _connectionFactory,
            _options);

        _snapshotStore = new MySqlSnapshotStore<TestMySqlOptions>(
            _connectionFactory,
            _options);

        _cutoffAdvancer = new MySqlArchiveCutoffAdvancer<TestMySqlOptions>(
            _connectionFactory,
            _options);

        _snapshotCoordinator = new TestSnapshotCoordinator(
            _snapshotStore,
            _cutoffAdvancer);

        _coldArchive = new FileColdEventArchive(_archiveDirectory);

        _segmentStore = new MySqlArchiveSegmentStore<TestMySqlOptions>(
            _connectionFactory,
            _options);

        _combinedFeed = new MySqlCombinedEventFeed<TestMySqlOptions>(
            _eventStore,
            _coldArchive,
            _segmentStore);

        // Initialize schema
        try
        {
            schemaInitializer.EnsureSchemaCreatedAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to initialize MySQL test database. Ensure MySQL is running and accessible at: {_connectionString}",
                ex);
        }
    }

    #region Archive Coordinator Tests

    [Fact(Skip = "Requires MySQL Server instance")]
    public async Task Archive_ColdArchivable_WithNoCutoff_ShouldNotArchive()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Act - No snapshot created, so ArchiveCutoffVersion is null
        await _archiveCoordinator.Archive();

        // Assert
        var hotEvents = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);
        hotEvents.Should().HaveCount(10, "events should remain in hot store when no cutoff is set");

        var archiveFiles = Directory.GetFiles(_archiveDirectory, "events-*.ndjson");
        archiveFiles.Should().BeEmpty("no archive file should be created without cutoff");
    }

    [Fact(Skip = "Requires MySQL Server instance")]
    public async Task Archive_ColdArchivable_WithCutoff_ShouldMoveEventsToArchive()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Create snapshot at version 5 to enable archival
        var snapshotData = Encoding.UTF8.GetBytes("snapshot-data");
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 5, snapshotData);

        // Act
        await _archiveCoordinator.Archive();

        // Assert
        var hotEvents = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);
        hotEvents.Should().HaveCount(5, "events 1-5 should be archived, 6-10 remain in hot store");
        hotEvents.First().StreamVersion.Value.Should().Be(6, "remaining events should start from version 6");

        var archiveFiles = Directory.GetFiles(_archiveDirectory, "events-*.ndjson");
        archiveFiles.Should().HaveCount(1, "one archive file should be created");

        var segments = await _segmentStore.GetActiveSegmentsAsync();
        segments.Should().HaveCount(1, "one archive segment should be recorded");
    }

    [Fact(Skip = "Requires MySQL Server instance")]
    public async Task Archive_ColdArchivable_ShouldPreserveEventData()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        var events = new List<EventData>
        {
            new EventData("ns1", "Event1", Encoding.UTF8.GetBytes("data1"), Encoding.UTF8.GetBytes("meta1")),
            new EventData("ns2", "Event2", Encoding.UTF8.GetBytes("data2"), Encoding.UTF8.GetBytes("meta2")),
            new EventData("ns1", "Event3", Encoding.UTF8.GetBytes("data3"), null)
        };

        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Create snapshot at version 3 to archive all events
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 3, Encoding.UTF8.GetBytes("snap"));

        // Act
        await _archiveCoordinator.Archive();

        // Assert
        var coldEvents = new List<EventEnvelope>();
        await foreach (var evt in _coldArchive.ReadAllForwards(null, 100))
        {
            coldEvents.Add(evt);
        }

        coldEvents.Should().HaveCount(3);
        coldEvents[0].EventType.Should().Be("Event1");
        coldEvents[0].Data.Should().BeEquivalentTo(Encoding.UTF8.GetBytes("data1"));
        coldEvents[0].Metadata.Should().BeEquivalentTo(Encoding.UTF8.GetBytes("meta1"));
        
        coldEvents[2].Metadata.Should().BeNull();
    }

    [Fact(Skip = "Requires MySQL Server instance")]
    public async Task Archive_FullHistory_ShouldKeepEventsInHotStore()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.FullHistory
        });

        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Create snapshot at version 5
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 5, Encoding.UTF8.GetBytes("snap"));

        // Act
        await _archiveCoordinator.Archive();

        // Assert
        var hotEvents = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);
        hotEvents.Should().HaveCount(10, "all events should remain in hot store for FullHistory mode");

        var archiveFiles = Directory.GetFiles(_archiveDirectory, "events-*.ndjson");
        archiveFiles.Should().HaveCount(1, "archive file should still be created");
    }

    [Fact(Skip = "Requires MySQL Server instance")]
    public async Task Archive_HardDeletable_ShouldDeleteEventsAndStream()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.HardDeletable
        });

        var events = CreateTestEvents(5);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Mark stream as deleted (simulating application logic)
        using var conn = (MySqlConnection)_connectionFactory.CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            $"UPDATE {_options.StreamsTableName} SET IsDeleted = 1 WHERE domain = @Domain AND stream_id = @StreamId",
            new { Domain = TestDomain, StreamId = TestStreamId });

        // Act
        await _archiveCoordinator.Archive();

        // Assert
        var hotEvents = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);
        hotEvents.Should().BeEmpty("all events should be deleted");

        var header = await _eventStore.GetStreamHeader(TestDomain, TestStreamId);
        header.Should().BeNull("stream header should be deleted");
    }

    #endregion

    #region Snapshot Coordinator Tests

    [Fact(Skip = "Requires MySQL Server instance")]
    public async Task SaveSnapshotAndAdvanceCutoff_ShouldSaveSnapshotAndUpdateCutoff()
    {
        // Arrange
        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        var snapshotData = Encoding.UTF8.GetBytes("snapshot-at-version-7");

        // Act
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 7, snapshotData);

        // Assert
        var snapshot = await _snapshotStore.GetLatest(TestStreamId);
        snapshot.Should().NotBeNull();
        snapshot!.StreamVersion.Value.Should().Be(7);
        snapshot.Data.Should().BeEquivalentTo(snapshotData);

        var header = await _eventStore.GetStreamHeader(TestDomain, TestStreamId);
        header.Should().NotBeNull();
        header!.ArchiveCutoffVersion.Should().Be(7);
    }

    [Fact(Skip = "Requires MySQL Server instance")]
    public async Task SaveSnapshotAndAdvanceCutoff_MultipleSnapshots_ShouldKeepLatest()
    {
        // Arrange
        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Act - Save multiple snapshots
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 5, Encoding.UTF8.GetBytes("snap-5"));
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 8, Encoding.UTF8.GetBytes("snap-8"));

        // Assert
        var snapshot = await _snapshotStore.GetLatest(TestStreamId);
        snapshot.Should().NotBeNull();
        snapshot!.StreamVersion.Value.Should().Be(8);
        snapshot.Data.Should().BeEquivalentTo(Encoding.UTF8.GetBytes("snap-8"));
    }

    #endregion

    #region Combined Event Feed Tests

    [Fact(Skip = "Requires MySQL Server instance")]
    public async Task CombinedFeed_WithOnlyHotEvents_ShouldReturnAllEvents()
    {
        // Arrange
        var events = CreateTestEvents(5);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Act
        var combinedEvents = new List<EventEnvelope>();
        await foreach (var evt in _combinedFeed.ReadAllForwards(null, 100))
        {
            combinedEvents.Add(evt);
        }

        // Assert
        combinedEvents.Should().HaveCount(5);
    }

    [Fact(Skip = "Requires MySQL Server instance")]
    public async Task CombinedFeed_WithHotAndColdEvents_ShouldMergeInOrder()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Archive first 5 events
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 5, Encoding.UTF8.GetBytes("snap"));
        await _archiveCoordinator.Archive();

        // Act
        var combinedEvents = new List<EventEnvelope>();
        await foreach (var evt in _combinedFeed.ReadAllForwards(null, 100))
        {
            combinedEvents.Add(evt);
        }

        // Assert
        combinedEvents.Should().HaveCount(10, "should include both cold (1-5) and hot (6-10) events");
        
        // Verify ordering
        for (int i = 1; i < combinedEvents.Count; i++)
        {
            combinedEvents[i].GlobalPosition.Value.Should().BeGreaterThan(
                combinedEvents[i - 1].GlobalPosition.Value,
                "events should be in global position order");
        }
    }

    [Fact(Skip = "Requires MySQL Server instance")]
    public async Task CombinedFeed_WithFromExclusive_ShouldStartAfterPosition()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 5, Encoding.UTF8.GetBytes("snap"));
        await _archiveCoordinator.Archive();

        // Get first 3 events to determine position
        var firstEvents = new List<EventEnvelope>();
        await foreach (var evt in _combinedFeed.ReadAllForwards(null, 3))
        {
            firstEvents.Add(evt);
            if (firstEvents.Count == 3) break;
        }

        var lastPos = firstEvents.Last().GlobalPosition;

        // Act
        var remainingEvents = new List<EventEnvelope>();
        await foreach (var evt in _combinedFeed.ReadAllForwards(lastPos, 100))
        {
            remainingEvents.Add(evt);
        }

        // Assert
        remainingEvents.Should().HaveCount(7);
        remainingEvents.All(e => e.GlobalPosition.Value > lastPos.Value).Should().BeTrue();
    }

    #endregion

    public override void Dispose()
    {
        // Cleanup: Drop test tables and archive directory
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

        // Cleanup archive directory
        try
        {
            if (Directory.Exists(_archiveDirectory))
            {
                Directory.Delete(_archiveDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }

        base.Dispose();
    }
}

