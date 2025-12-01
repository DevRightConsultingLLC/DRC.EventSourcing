using System.Text;
using Dapper;
using DRC.EventSourcing.Sqlite;
using DRC.EventSourcing.SqlServer;
using DRC.EventSourcing.Tests.Sqlite;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace DRC.EventSourcing.Tests.SqlServer;

/// <summary>
/// Tests for SQL Server archive coordinator functionality including cold/hot storage archival,
/// retention policies, and the combined event feed.
/// NOTE: These tests require a running SQL Server instance.
/// </summary>
public class SqlServerArchiveTests : EventStoreTestBase
{
    private const string TestConnectionString = "Server=localhost;Database=EventStoreTests;Integrated Security=true;TrustServerCertificate=true;";
    
    private readonly string _archiveDirectory;
    private readonly TestSqlServerOptions _options;
    private readonly SqlServerConnectionFactory<TestSqlServerOptions> _connectionFactory;
    private readonly SqlServerEventStore<TestSqlServerOptions> _eventStore;
    private readonly SqlServerArchiveCoordinator<TestSqlServerOptions> _archiveCoordinator;
    private readonly SqlServerSnapshotStore<TestSqlServerOptions> _snapshotStore;
    private readonly TestSnapshotCoordinator _snapshotCoordinator;
    private readonly SqlServerArchiveCutoffAdvancer<TestSqlServerOptions> _cutoffAdvancer;
    private readonly FileColdEventArchive _coldArchive;
    private readonly SqlServerArchiveSegmentStore<TestSqlServerOptions> _segmentStore;
    private readonly SqlServerCombinedEventFeed<TestSqlServerOptions> _combinedFeed;
    private readonly DefaultDomainRetentionPolicyProvider _policyProvider;
    private readonly string _testStoreName;

    public SqlServerArchiveTests()
    {
        // Create unique store name and archive directory for each test
        _testStoreName = $"Test_{Guid.NewGuid():N}";
        _archiveDirectory = Path.Combine(Path.GetTempPath(), $"test-archive-{Guid.NewGuid()}");
        Directory.CreateDirectory(_archiveDirectory);
        
        _options = new TestSqlServerOptions
        {
            ConnectionString = TestConnectionString,
            StoreName = _testStoreName,
            Schema = "dbo",
            ArchiveDirectory = _archiveDirectory
        };

        _connectionFactory = new SqlServerConnectionFactory<TestSqlServerOptions>(_options);
        
        _policyProvider = new DefaultDomainRetentionPolicyProvider();
        
        var logger = NullLogger<SqlServerEventStore<TestSqlServerOptions>>.Instance;
        var metrics = new Infrastructure.EventStoreMetrics(_testStoreName);
        
        _eventStore = new SqlServerEventStore<TestSqlServerOptions>(
            _connectionFactory, 
            _options,
            _policyProvider,
            logger,
            metrics);

        var schemaInitializer = new SqlServerSchemaInitializer<TestSqlServerOptions>(
            _connectionFactory, 
            _options);

        _archiveCoordinator = new SqlServerArchiveCoordinator<TestSqlServerOptions>(
            _connectionFactory,
            _options);

        _snapshotStore = new SqlServerSnapshotStore<TestSqlServerOptions>(
            _connectionFactory,
            _options);

        _cutoffAdvancer = new SqlServerArchiveCutoffAdvancer<TestSqlServerOptions>(
            _connectionFactory,
            _options);

        _snapshotCoordinator = new TestSnapshotCoordinator(
            _snapshotStore,
            _cutoffAdvancer);

        _coldArchive = new FileColdEventArchive(_archiveDirectory);

        _segmentStore = new SqlServerArchiveSegmentStore<TestSqlServerOptions>(_connectionFactory);

        _combinedFeed = new SqlServerCombinedEventFeed<TestSqlServerOptions>(
            _eventStore,
            _coldArchive,
            _segmentStore);

        // Initialize schema
        try
        {
            schemaInitializer.EnsureSchemaCreatedAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // If initialization fails, tests will be skipped
        }
    }

    #region Archive Coordinator Tests

    [Fact(Skip = "Requires SQL Server instance")]
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

    [Fact(Skip = "Requires SQL Server instance")]
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

    [Fact(Skip = "Requires SQL Server instance")]
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
            new EventData(TestNamespace, "Event1", Encoding.UTF8.GetBytes("data-1"), Encoding.UTF8.GetBytes("meta-1")),
            new EventData(TestNamespace, "Event2", Encoding.UTF8.GetBytes("data-2"), null),
            new EventData(TestNamespace, "Event3", Encoding.UTF8.GetBytes("data-3"), Encoding.UTF8.GetBytes("meta-3"))
        };

        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        var snapshotData = Encoding.UTF8.GetBytes("snapshot");
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 3, snapshotData);

        // Act
        await _archiveCoordinator.Archive();

        // Assert - Read from cold archive
        var coldEvents = new List<EventEnvelope>();
        await foreach (var evt in _coldArchive.ReadAllForwards())
        {
            coldEvents.Add(evt);
        }

        coldEvents.Should().HaveCount(3);
        
        // Verify first event
        coldEvents[0].EventType.Should().Be("Event1");
        Encoding.UTF8.GetString(coldEvents[0].Data).Should().Be("data-1");
        coldEvents[0].Metadata.Should().NotBeNull();
        Encoding.UTF8.GetString(coldEvents[0].Metadata!).Should().Be("meta-1");

        // Verify second event (no metadata)
        coldEvents[1].EventType.Should().Be("Event2");
        Encoding.UTF8.GetString(coldEvents[1].Data).Should().Be("data-2");
        coldEvents[1].Metadata.Should().BeNull();

        // Verify third event
        coldEvents[2].EventType.Should().Be("Event3");
        Encoding.UTF8.GetString(coldEvents[2].Data).Should().Be("data-3");
        coldEvents[2].Metadata.Should().NotBeNull();
        Encoding.UTF8.GetString(coldEvents[2].Metadata!).Should().Be("meta-3");
    }

    [Fact(Skip = "Requires SQL Server instance")]
    public async Task Archive_ColdArchivable_RunTwice_ShouldBeIdempotent()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        var snapshotData = Encoding.UTF8.GetBytes("snapshot");
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 5, snapshotData);

        // Act - Run archive twice
        await _archiveCoordinator.Archive();
        await _archiveCoordinator.Archive();

        // Assert
        var archiveFiles = Directory.GetFiles(_archiveDirectory, "events-*.ndjson");
        archiveFiles.Should().HaveCount(1, "archive should be idempotent - no duplicate files");

        var segments = await _segmentStore.GetActiveSegmentsAsync();
        segments.Should().HaveCount(1, "only one segment should exist");

        var hotEvents = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);
        hotEvents.Should().HaveCount(5, "hot store should remain consistent");
    }

    [Fact(Skip = "Requires SQL Server instance")]
    public async Task Archive_ColdArchivable_MultipleStreams_ShouldArchiveAll()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        // Create 3 streams
        await _eventStore.AppendToStream(TestDomain, "stream-1", StreamVersion.New(), CreateTestEvents(5));
        await _eventStore.AppendToStream(TestDomain, "stream-2", StreamVersion.New(), CreateTestEvents(5));
        await _eventStore.AppendToStream(TestDomain, "stream-3", StreamVersion.New(), CreateTestEvents(5));

        // Set cutoffs for each stream
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, "stream-1", 3, Encoding.UTF8.GetBytes("s1"));
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, "stream-2", 4, Encoding.UTF8.GetBytes("s2"));
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, "stream-3", 2, Encoding.UTF8.GetBytes("s3"));

        // Act
        await _archiveCoordinator.Archive();

        // Assert
        var archiveFiles = Directory.GetFiles(_archiveDirectory, "events-*.ndjson");
        archiveFiles.Should().HaveCount(3, "three archive files should be created");

        var segments = await _segmentStore.GetActiveSegmentsAsync();
        segments.Should().HaveCount(3, "three segments should be recorded");

        // Verify hot store counts
        var stream1Hot = await _eventStore.ReadStream(TestDomain, "stream-1", null, StreamVersion.New(), 100);
        stream1Hot.Should().HaveCount(2, "stream-1: 2 events remain (versions 4-5)");

        var stream2Hot = await _eventStore.ReadStream(TestDomain, "stream-2", null, StreamVersion.New(), 100);
        stream2Hot.Should().HaveCount(1, "stream-2: 1 event remains (version 5)");

        var stream3Hot = await _eventStore.ReadStream(TestDomain, "stream-3", null, StreamVersion.New(), 100);
        stream3Hot.Should().HaveCount(3, "stream-3: 3 events remain (versions 3-5)");
    }

    [Fact(Skip = "Requires SQL Server instance")]
    public async Task Archive_FullHistory_ShouldBackupWithoutDeletion()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.FullHistory
        });

        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Set cutoff to archive all 10 events (same as ColdArchivable workflow)
        var snapshotData = Encoding.UTF8.GetBytes("snapshot");
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 10, snapshotData);

        // Act
        await _archiveCoordinator.Archive();

        // Assert
        var hotEvents = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);
        hotEvents.Should().HaveCount(10, "all events should remain in hot store (FullHistory mode)");

        var archiveFiles = Directory.GetFiles(_archiveDirectory, "events-*.ndjson");
        archiveFiles.Should().HaveCount(1, "one archive file should be created");

        var coldEvents = new List<EventEnvelope>();
        await foreach (var evt in _coldArchive.ReadAllForwards())
        {
            coldEvents.Add(evt);
        }
        coldEvents.Should().HaveCount(10, "all events should also be in cold archive");

        var segments = await _segmentStore.GetActiveSegmentsAsync();
        segments.Should().HaveCount(1, "one archive segment should be recorded");
    }

    [Fact(Skip = "Requires SQL Server instance")]
    public async Task Archive_HardDeletable_WithDeletedFlag_ShouldPermanentlyDelete()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.HardDeletable
        });

        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Mark stream as deleted
        using var conn = (SqlConnection)_connectionFactory.CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            $"UPDATE {_options.StreamsTableName} SET IsDeleted = 1 WHERE domain = @Domain AND stream_id = @StreamId",
            new { Domain = TestDomain, StreamId = TestStreamId });

        // Act
        await _archiveCoordinator.Archive();

        // Assert
        var header = await _eventStore.GetStreamHeader(TestDomain, TestStreamId);
        header.Should().BeNull("stream header should be deleted");

        var hotEvents = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);
        hotEvents.Should().BeEmpty("all events should be permanently deleted");

        var archiveFiles = Directory.GetFiles(_archiveDirectory, "*.ndjson");
        archiveFiles.Should().BeEmpty("no archive file should be created for hard deletable streams");

        var segments = await _segmentStore.GetActiveSegmentsAsync();
        segments.Should().BeEmpty("no segments should be recorded");
    }

    [Fact(Skip = "Requires SQL Server instance")]
    public async Task Archive_Default_ShouldNotArchive()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.Default
        });

        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Act
        await _archiveCoordinator.Archive();

        // Assert
        var hotEvents = await _eventStore.ReadStream(TestDomain, TestStreamId, null, StreamVersion.New(), 100);
        hotEvents.Should().HaveCount(10, "all events should remain in hot store (Default mode)");

        var archiveFiles = Directory.GetFiles(_archiveDirectory, "*.ndjson");
        archiveFiles.Should().BeEmpty("no archive file should be created for Default mode");
    }

    #endregion

    #region Snapshot Coordinator Tests

    [Fact(Skip = "Requires SQL Server instance")]
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

    [Fact(Skip = "Requires SQL Server instance")]
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

    [Fact(Skip = "Requires SQL Server instance")]
    public async Task CombinedFeed_WithOnlyHotEvents_ShouldReadAllEvents()
    {
        // Arrange
        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Act
        var allEvents = new List<EventEnvelope>();
        await foreach (var evt in _combinedFeed.ReadAllForwards())
        {
            allEvents.Add(evt);
        }

        // Assert
        allEvents.Should().HaveCount(10, "should read all events from hot store");
        
        // Verify order
        for (int i = 1; i < allEvents.Count; i++)
        {
            allEvents[i].GlobalPosition.Value.Should().BeGreaterThan(
                allEvents[i - 1].GlobalPosition.Value,
                "events should be ordered by GlobalPosition");
        }
    }

    [Fact(Skip = "Requires SQL Server instance")]
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
        await foreach (var evt in _combinedFeed.ReadAllForwards())
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

    [Fact(Skip = "Requires SQL Server instance")]
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
            conn.Execute($"DROP TABLE IF EXISTS {((IEventStoreOptions)_options).EventsTableName}");
            conn.Execute($"DROP TABLE IF EXISTS {((IEventStoreOptions)_options).StreamsTableName}");
            conn.Execute($"DROP TABLE IF EXISTS {((IEventStoreOptions)_options).SnapshotsTableName}");
            conn.Execute($"DROP TABLE IF EXISTS {((IEventStoreOptions)_options).ArchiveSegmentsTableName}");
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

