using System.Text;
using Dapper;
using DRC.EventSourcing.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DRC.EventSourcing.Tests.Sqlite;

/// <summary>
/// Tests for SQLite archive coordinator functionality including cold/hot storage archival,
/// retention policies, and the combined event feed.
/// </summary>
public class SqliteArchiveTests : EventStoreTestBase
{
    private readonly string _dbPath;
    private readonly string _archiveDirectory;
    private readonly TestSqliteOptions _options;
    private readonly SqliteConnectionFactory<TestSqliteOptions> _connectionFactory;
    private readonly SqliteEventStore<TestSqliteOptions> _eventStore;
    private readonly SqliteArchiveCoordinator<TestSqliteOptions> _archiveCoordinator;
    private readonly SqliteSnapshotStore<TestSqliteOptions> _snapshotStore;
    private readonly TestSnapshotCoordinator _snapshotCoordinator;
    private readonly SqliteArchiveCutoffAdvancer<TestSqliteOptions> _cutoffAdvancer;
    private readonly FileColdEventArchive _coldArchive;
    private readonly SqliteArchiveSegmentStore<TestSqliteOptions> _segmentStore;
    private readonly SqliteCombinedEventFeed<TestSqliteOptions> _combinedFeed;
    private readonly DefaultDomainRetentionPolicyProvider _policyProvider;

    public SqliteArchiveTests()
    {
        // Create unique database and archive directory for each test
        _dbPath = Path.Combine(Path.GetTempPath(), $"test-archive-{Guid.NewGuid()}.db");
        _archiveDirectory = Path.Combine(Path.GetTempPath(), $"test-archive-{Guid.NewGuid()}");
        Directory.CreateDirectory(_archiveDirectory);
        
        _options = new TestSqliteOptions
        {
            ConnectionString = $"Data Source={_dbPath}",
            StoreName = "Test",
            ArchiveDirectory = _archiveDirectory
        };

        _connectionFactory = new SqliteConnectionFactory<TestSqliteOptions>(_options);
        
        _policyProvider = new DefaultDomainRetentionPolicyProvider();
        
        var logger = NullLogger<SqliteEventStore<TestSqliteOptions>>.Instance;
        
        _eventStore = new SqliteEventStore<TestSqliteOptions>(
            _connectionFactory, 
            _options,
            _policyProvider,
            logger);

        var schemaInitializer = new SqliteSchemaInitializer<TestSqliteOptions>(
            _connectionFactory, 
            _options);

        _archiveCoordinator = new SqliteArchiveCoordinator<TestSqliteOptions>(
            _connectionFactory,
            _options);

        _snapshotStore = new SqliteSnapshotStore<TestSqliteOptions>(
            _connectionFactory,
            _options);

        _cutoffAdvancer = new SqliteArchiveCutoffAdvancer<TestSqliteOptions>(
            _connectionFactory,
            _options);

        _snapshotCoordinator = new TestSnapshotCoordinator(
            _snapshotStore,
            _cutoffAdvancer);

        _coldArchive = new FileColdEventArchive(_archiveDirectory);

        _segmentStore = new SqliteArchiveSegmentStore<TestSqliteOptions>(
            _connectionFactory,
            _options);

        _combinedFeed = new SqliteCombinedEventFeed<TestSqliteOptions>(
            _eventStore,
            _coldArchive,
            _segmentStore);

        // Initialize schema
        schemaInitializer.EnsureSchemaCreatedAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    #region Archive Coordinator Tests

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
        await MarkStreamAsDeleted(TestDomain, TestStreamId);

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

    [Fact]
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

    #region Combined Event Feed Tests

    [Fact]
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

    [Fact]
    public async Task CombinedFeed_WithOnlyColdEvents_ShouldReadAllEvents()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Archive all events
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 10, Encoding.UTF8.GetBytes("snapshot"));
        await _archiveCoordinator.Archive();

        // Act
        var allEvents = new List<EventEnvelope>();
        await foreach (var evt in _combinedFeed.ReadAllForwards())
        {
            allEvents.Add(evt);
        }

        // Assert
        allEvents.Should().HaveCount(10, "should read all events from cold archive");
        
        // Verify order
        for (int i = 1; i < allEvents.Count; i++)
        {
            allEvents[i].GlobalPosition.Value.Should().BeGreaterThan(
                allEvents[i - 1].GlobalPosition.Value,
                "events should be ordered by GlobalPosition");
        }
    }

    [Fact]
    public async Task CombinedFeed_WithMixedHotAndCold_ShouldMergeInOrder()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        // Create 15 events
        var events = CreateTestEvents(15);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Archive first 10 events
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 10, Encoding.UTF8.GetBytes("snapshot"));
        await _archiveCoordinator.Archive();

        // Act - Read from combined feed
        var allEvents = new List<EventEnvelope>();
        await foreach (var evt in _combinedFeed.ReadAllForwards())
        {
            allEvents.Add(evt);
        }

        // Assert
        allEvents.Should().HaveCount(15, "should read all events from both cold and hot");
        
        // Verify versions are sequential
        for (int i = 0; i < allEvents.Count; i++)
        {
            allEvents[i].StreamVersion.Value.Should().Be(i + 1, $"event {i} should have version {i + 1}");
        }

        // Verify order by GlobalPosition
        for (int i = 1; i < allEvents.Count; i++)
        {
            allEvents[i].GlobalPosition.Value.Should().BeGreaterThan(
                allEvents[i - 1].GlobalPosition.Value,
                "events should be ordered by GlobalPosition");
        }
    }

    [Fact]
    public async Task CombinedFeed_WithMultipleStreams_ShouldMergeCorrectly()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        // Create events in three streams
        await _eventStore.AppendToStream(TestDomain, "stream-1", StreamVersion.New(), CreateTestEvents(5));
        await _eventStore.AppendToStream(TestDomain, "stream-2", StreamVersion.New(), CreateTestEvents(5));
        await _eventStore.AppendToStream(TestDomain, "stream-3", StreamVersion.New(), CreateTestEvents(5));

        // Archive first 2 streams
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, "stream-1", 5, Encoding.UTF8.GetBytes("s1"));
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, "stream-2", 5, Encoding.UTF8.GetBytes("s2"));
        await _archiveCoordinator.Archive();

        // Act
        var allEvents = new List<EventEnvelope>();
        await foreach (var evt in _combinedFeed.ReadAllForwards())
        {
            allEvents.Add(evt);
        }

        // Assert
        allEvents.Should().HaveCount(15, "should read all events from all streams");

        // Verify no duplicates by checking unique GlobalPositions
        var positions = allEvents.Select(e => e.GlobalPosition.Value).ToList();
        positions.Should().OnlyHaveUniqueItems("no duplicate GlobalPositions should exist");

        // Verify order
        for (int i = 1; i < allEvents.Count; i++)
        {
            allEvents[i].GlobalPosition.Value.Should().BeGreaterThan(
                allEvents[i - 1].GlobalPosition.Value,
                "events should be ordered by GlobalPosition");
        }
    }

    [Fact]
    public async Task CombinedFeed_WithFromExclusive_ShouldStartAfterPosition()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        var events = CreateTestEvents(20);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Archive first 10
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 10, Encoding.UTF8.GetBytes("snapshot"));
        await _archiveCoordinator.Archive();

        // Get position of 5th event
        var firstBatch = new List<EventEnvelope>();
        await foreach (var evt in _combinedFeed.ReadAllForwards())
        {
            firstBatch.Add(evt);
            if (firstBatch.Count == 5) break;
        }
        var fifthPosition = firstBatch.Last().GlobalPosition;

        // Act - Read from position 5 onwards
        var remainingEvents = new List<EventEnvelope>();
        await foreach (var evt in _combinedFeed.ReadAllForwards(fromExclusive: fifthPosition))
        {
            remainingEvents.Add(evt);
        }

        // Assert
        remainingEvents.Should().HaveCount(15, "should read events 6-20");
        remainingEvents.All(e => e.GlobalPosition.Value > fifthPosition.Value).Should().BeTrue(
            "all events should be after the specified position");
    }

    [Fact]
    public async Task CombinedFeed_ShouldNotReturnDuplicates()
    {
        // Arrange - This tests the deduplication during transition period
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.FullHistory // Keeps events in both hot and cold
        });

        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        // Archive (FullHistory keeps in hot store)
        await _archiveCoordinator.Archive();

        // Act - Read from combined feed
        var allEvents = new List<EventEnvelope>();
        await foreach (var evt in _combinedFeed.ReadAllForwards())
        {
            allEvents.Add(evt);
        }

        // Assert
        allEvents.Should().HaveCount(10, "should return each event only once despite being in both stores");

        var positions = allEvents.Select(e => e.GlobalPosition.Value).ToList();
        positions.Should().OnlyHaveUniqueItems("no duplicate GlobalPositions should be returned");
    }

    [Fact]
    public async Task CombinedFeed_WithEmptyStore_ShouldReturnEmpty()
    {
        // Act
        var allEvents = new List<EventEnvelope>();
        await foreach (var evt in _combinedFeed.ReadAllForwards())
        {
            allEvents.Add(evt);
        }

        // Assert
        allEvents.Should().BeEmpty("should return no events from empty store");
    }

    #endregion

    #region Cold Archive Tests

    [Fact]
    public async Task ColdArchive_GetRange_WithNoArchives_ShouldReturnNull()
    {
        // Act
        var range = await _coldArchive.GetRange();

        // Assert
        range.Should().BeNull("should return null when no archives exist");
    }

    [Fact]
    public async Task ColdArchive_GetRange_WithArchives_ShouldReturnMinMax()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        // Create multiple streams to get a range of positions
        await _eventStore.AppendToStream(TestDomain, "stream-1", StreamVersion.New(), CreateTestEvents(5));
        await _eventStore.AppendToStream(TestDomain, "stream-2", StreamVersion.New(), CreateTestEvents(5));

        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, "stream-1", 5, Encoding.UTF8.GetBytes("s1"));
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, "stream-2", 5, Encoding.UTF8.GetBytes("s2"));
        await _archiveCoordinator.Archive();

        // Act
        var range = await _coldArchive.GetRange();

        // Assert
        range.Should().NotBeNull("should return range when archives exist");
        range!.Value.Min.Value.Should().BeGreaterThan(0);
        range.Value.Max.Value.Should().BeGreaterThan(range.Value.Min.Value);
    }

    [Fact]
    public async Task ColdArchive_ReadAllForwards_ShouldStreamEventsEfficiently()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        var events = CreateTestEvents(100);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 100, Encoding.UTF8.GetBytes("snapshot"));
        await _archiveCoordinator.Archive();

        // Act - Read in batches
        var readEvents = new List<EventEnvelope>();
        await foreach (var evt in _coldArchive.ReadAllForwards(batchSize: 10))
        {
            readEvents.Add(evt);
        }

        // Assert
        readEvents.Should().HaveCount(100, "should stream all events");
        
        // Verify no duplicates
        var positions = readEvents.Select(e => e.GlobalPosition.Value).ToList();
        positions.Should().OnlyHaveUniqueItems();
    }

    #endregion

    #region Archive Segment Store Tests

    [Fact]
    public async Task ArchiveSegmentStore_WithNoSegments_ShouldReturnEmpty()
    {
        // Act
        var segments = await _segmentStore.GetActiveSegmentsAsync();

        // Assert
        segments.Should().BeEmpty("should return empty list when no segments exist");
    }

    [Fact]
    public async Task ArchiveSegmentStore_AfterArchival_ShouldReturnActiveSegments()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), CreateTestEvents(10));
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 10, Encoding.UTF8.GetBytes("snapshot"));
        await _archiveCoordinator.Archive();

        // Act
        var segments = await _segmentStore.GetActiveSegmentsAsync();

        // Assert
        segments.Should().HaveCount(1, "should return one active segment");
        segments[0].MinPosition.Value.Should().BeGreaterThan(0);
        segments[0].MaxPosition.Value.Should().BeGreaterThan(segments[0].MinPosition.Value);
        segments[0].FileName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ArchiveSegmentStore_MultipleArchives_ShouldReturnAllSegmentsOrdered()
    {
        // Arrange
        _policyProvider.AddOrReplace(new DomainRetentionPolicy
        {
            Domain = TestDomain,
            RetentionMode = RetentionMode.ColdArchivable
        });

        // Create and archive in batches
        await _eventStore.AppendToStream(TestDomain, "stream-1", StreamVersion.New(), CreateTestEvents(5));
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, "stream-1", 5, Encoding.UTF8.GetBytes("s1"));
        await _archiveCoordinator.Archive();

        await _eventStore.AppendToStream(TestDomain, "stream-2", StreamVersion.New(), CreateTestEvents(5));
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, "stream-2", 5, Encoding.UTF8.GetBytes("s2"));
        await _archiveCoordinator.Archive();

        // Act
        var segments = await _segmentStore.GetActiveSegmentsAsync();

        // Assert
        segments.Should().HaveCount(2, "should return two active segments");
        
        // Verify ordering by MinPosition
        segments[0].MinPosition.Value.Should().BeLessThan(segments[1].MinPosition.Value,
            "segments should be ordered by MinPosition");
    }

    #endregion

    #region Snapshot and Cutoff Advancer Tests

    [Fact]
    public async Task SnapshotCoordinator_ShouldSaveSnapshotAndAdvanceCutoff()
    {
        // Arrange
        var events = CreateTestEvents(10);
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), events);

        var snapshotData = Encoding.UTF8.GetBytes("snapshot-state");

        // Act
        await _snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(TestDomain, TestStreamId, 10, snapshotData);

        // Assert
        var snapshot = await _snapshotStore.GetLatest(TestStreamId);
        snapshot.Should().NotBeNull();
        snapshot!.StreamVersion.Value.Should().Be(10);
        Encoding.UTF8.GetString(snapshot.Data).Should().Be("snapshot-state");

        var header = await _eventStore.GetStreamHeader(TestDomain, TestStreamId);
        header.Should().NotBeNull();
        header!.ArchiveCutoffVersion.Should().Be(10);
    }

    [Fact]
    public async Task CutoffAdvancer_TryAdvance_ShouldAdvanceMonotonically()
    {
        // Arrange
        await _eventStore.AppendToStream(TestDomain, TestStreamId, StreamVersion.New(), CreateTestEvents(20));

        // Act & Assert
        var result1 = await _cutoffAdvancer.TryAdvanceArchiveCutoff(TestDomain, TestStreamId, 10);
        result1.Should().BeTrue("should advance from null to 10");

        var header1 = await _eventStore.GetStreamHeader(TestDomain, TestStreamId);
        header1!.ArchiveCutoffVersion.Should().Be(10);

        var result2 = await _cutoffAdvancer.TryAdvanceArchiveCutoff(TestDomain, TestStreamId, 15);
        result2.Should().BeTrue("should advance from 10 to 15");

        var header2 = await _eventStore.GetStreamHeader(TestDomain, TestStreamId);
        header2!.ArchiveCutoffVersion.Should().Be(15);

        var result3 = await _cutoffAdvancer.TryAdvanceArchiveCutoff(TestDomain, TestStreamId, 12);
        result3.Should().BeFalse("should not advance backwards from 15 to 12");

        var header3 = await _eventStore.GetStreamHeader(TestDomain, TestStreamId);
        header3!.ArchiveCutoffVersion.Should().Be(15, "cutoff should remain at 15");
    }

    #endregion

    #region Helper Methods

    private async Task MarkStreamAsDeleted(string domain, string streamId)
    {
        // Directly update the database to mark stream as deleted
        using var conn = (SqliteConnection)_connectionFactory.CreateConnection();
        await conn.OpenAsync();

        var sql = $@"UPDATE {((IEventStoreOptions)_options).StreamsTableName}
                     SET IsDeleted = 1
                     WHERE domain = @Domain AND stream_id = @StreamId";

        await conn.ExecuteAsync(sql, new { Domain = domain, StreamId = streamId });
    }

    #endregion

    public override void Dispose()
    {
        try
        {
            // Force cleanup of SQLite connections
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }

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

/// <summary>
/// Simple test implementation of ISnapshotCoordinator for testing purposes.
/// </summary>
internal class TestSnapshotCoordinator : ISnapshotCoordinator
{
    private readonly ISnapshotStore _snapshotStore;
    private readonly IArchiveCutoffAdvancer _cutoffAdvancer;

    public TestSnapshotCoordinator(ISnapshotStore snapshotStore, IArchiveCutoffAdvancer cutoffAdvancer)
    {
        _snapshotStore = snapshotStore;
        _cutoffAdvancer = cutoffAdvancer;
    }

    public async Task SaveSnapshotAndAdvanceCutoff(
        string domain,
        string streamId,
        int snapshotVersion,
        byte[] snapshotData,
        CancellationToken ct = default)
    {
        var snapshot = new Snapshot(
            StreamId: streamId,
            StreamVersion: new StreamVersion(snapshotVersion),
            Data: snapshotData,
            CreatedUtc: DateTime.UtcNow
        );

        await _snapshotStore.Save(snapshot, ct);
        await _cutoffAdvancer.TryAdvanceArchiveCutoff(domain, streamId, snapshotVersion, ct);
    }
}

