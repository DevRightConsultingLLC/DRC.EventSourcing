using System.Text;
using System.Text.Json;
using DRC.EventSourcing;
using DRC.EventSourcing.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DRC.EventSourcing.Demo;

public class AssetStoreSqliteOptions : SqliteEventStoreOptions
{
}

// Event DTOs
public record AssetCreatedEvent(string PartNumber, string SerialNumber, string InitialLocation, DateTime CreatedAt);
public record AssetMovedEvent(string FromLocation, string ToLocation, DateTime MovedAt, string? Notes);
public record AssetSnapshot(string PartNumber, string SerialNumber, string CurrentLocation, int MoveCount, DateTime LastUpdated);

public static class Program
{
    private const string Domain = "assets";
    
    // Common locations in a warehouse/facility
    private static readonly string[] Locations = 
    {
        "Receiving Dock", "Warehouse-A1", "Warehouse-A2", "Warehouse-B1", "Warehouse-B2",
        "Production Floor", "Quality Control", "Shipping Dock", "Maintenance Area", "Storage Room"
    };
    
    // Sample part numbers for different asset types
    private static readonly string[] PartNumbers = 
    {
        "MOT-1000", "MOT-2000", "SEN-500", "CTL-300", "PWR-750"
    };
    
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== Asset Management Demo ===");
        Console.WriteLine("Demonstrating Event Sourcing with Snapshots and Archiving\n");

        // Configure DI
        var services = new ServiceCollection()
            .AddInMemorySqliteEventStore<AssetStoreSqliteOptions>(opts =>
            {
                opts.StoreName = "asset_tracking";
                opts.ArchiveDirectory = Path.Combine(Path.GetTempPath(), "asset_archive");
            })
            .BuildServiceProvider();

        // Initialize schema
        Console.WriteLine("Initializing database schema...");
        await services.InitializeEventStoreSchemasAsync();
        
        var store = services.GetRequiredService<IEventStore>();
        var snapshotStore = services.GetRequiredService<ISnapshotStore>();
        var archiveCoordinator = services.GetRequiredService<IArchiveCoordinator<AssetStoreSqliteOptions>>();
        
        // Step 1: Create 5 assets
        Console.WriteLine("\n=== Step 1: Creating Assets ===");
        var assets = await CreateAssetsAsync(store);
        Console.WriteLine($"Created {assets.Count} assets\n");
        
        // Step 2: Move each asset ~100-200 times
        Console.WriteLine("=== Step 2: Simulating Asset Movements ===");
        await MoveAssetsAsync(store, assets);
        
        // Step 3: Create snapshots for each asset
        Console.WriteLine("\n=== Step 3: Creating Snapshots ===");
        await CreateSnapshotsAsync(store, snapshotStore, assets);
        
        // Step 4: Archive old events
        Console.WriteLine("\n=== Step 4: Archiving Events ===");
        await ArchiveEventsAsync(archiveCoordinator);
        
        // Step 5: Demonstrate reading
        Console.WriteLine("\n=== Step 5: Reading Asset History ===");
        await DemonstrateReadingAsync(store, snapshotStore, assets.First());
        
        // Step 6: Show statistics
        Console.WriteLine("\n=== Final Statistics ===");
        await ShowStatisticsAsync(services, store, assets);
        
        Console.WriteLine("\n=== Demo Complete ===");
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
        
        return 0;
    }

    private static async Task<List<(string PartNumber, string SerialNumber, string StreamId)>> CreateAssetsAsync(IEventStore store)
    {
        var assets = new List<(string, string, string)>();
        var random = new Random(42); // Fixed seed for reproducibility
        
        for (int i = 0; i < 5; i++)
        {
            var partNumber = PartNumbers[random.Next(PartNumbers.Length)];
            var serialNumber = $"SN{DateTime.UtcNow.Ticks + i:X}";
            var streamId = $"{partNumber}-{serialNumber}";
            
            var createdEvent = new AssetCreatedEvent(
                partNumber,
                serialNumber,
                "Receiving Dock",
                DateTime.UtcNow
            );
            
            var eventData = new EventData(
                null, // No namespace
                "AssetCreated",
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(createdEvent)),
                Encoding.UTF8.GetBytes($"{{\"createdBy\":\"System\",\"assetIndex\":{i}}}")
            );
            
            await store.AppendToStream(Domain, streamId, StreamVersion.New(), new[] { eventData });
            
            assets.Add((partNumber, serialNumber, streamId));
            Console.WriteLine($"  ✓ Created asset: {streamId} at {createdEvent.InitialLocation}");
        }
        
        return assets;
    }

    private static async Task MoveAssetsAsync(IEventStore store, List<(string PartNumber, string SerialNumber, string StreamId)> assets)
    {
        var random = new Random(42);
        
        foreach (var asset in assets)
        {
            var moveCount = random.Next(100, 201); // 100-200 moves per asset
            var currentLocation = "Receiving Dock";
            var currentVersion = new StreamVersion(1); // Start after creation event
            
            Console.Write($"  Moving {asset.StreamId}: ");
            
            // Batch moves in groups of 50 for better performance
            const int batchSize = 50;
            for (int batch = 0; batch < (moveCount / batchSize) + 1; batch++)
            {
                var movesInBatch = Math.Min(batchSize, moveCount - (batch * batchSize));
                if (movesInBatch <= 0) break;
                
                var events = new List<EventData>();
                
                for (int i = 0; i < movesInBatch; i++)
                {
                    var newLocation = Locations[random.Next(Locations.Length)];
                    
                    var movedEvent = new AssetMovedEvent(
                        currentLocation,
                        newLocation,
                        DateTime.UtcNow.AddMinutes(batch * batchSize + i),
                        i % 10 == 0 ? "Scheduled maintenance" : null
                    );
                    
                    events.Add(new EventData(
                        null, // No namespace
                        "AssetMoved",
                        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(movedEvent)),
                        null
                    ));
                    
                    currentLocation = newLocation;
                }
                
                await store.AppendToStream(Domain, asset.StreamId, currentVersion, events);
                currentVersion = new StreamVersion(currentVersion.Value + movesInBatch);
                Console.Write(".");
            }
            
            Console.WriteLine($" {moveCount} moves complete");
        }
    }

    private static async Task CreateSnapshotsAsync(
        IEventStore store,
        ISnapshotStore snapshotStore,
        List<(string PartNumber, string SerialNumber, string StreamId)> assets)
    {
        foreach (var asset in assets)
        {
            // Read all events to build current state
            var events = await store.ReadStream(Domain, asset.StreamId, null, StreamVersion.New(), int.MaxValue);
            
            // Build snapshot from events
            string? currentLocation = null;
            int moveCount = 0;
            DateTime? lastUpdated = null;
            
            foreach (var evt in events)
            {
                if (evt.EventType == "AssetCreated")
                {
                    var created = JsonSerializer.Deserialize<AssetCreatedEvent>(evt.Data.AsSpan());
                    currentLocation = created!.InitialLocation;
                    lastUpdated = created.CreatedAt;
                }
                else if (evt.EventType == "AssetMoved")
                {
                    var moved = JsonSerializer.Deserialize<AssetMovedEvent>(evt.Data.AsSpan());
                    currentLocation = moved!.ToLocation;
                    lastUpdated = moved.MovedAt;
                    moveCount++;
                }
            }
            
            var snapshot = new AssetSnapshot(
                asset.PartNumber,
                asset.SerialNumber,
                currentLocation!,
                moveCount,
                lastUpdated!.Value
            );
            
            var snapshotData = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            var lastEvent = events.Last();
            
            // Create and save snapshot
            var snapshotObj = new Snapshot(
                asset.StreamId,
                lastEvent.StreamVersion,
                snapshotData,
                DateTime.UtcNow
            );
            
            await snapshotStore.Save(snapshotObj);
            
            Console.WriteLine($"  ✓ Snapshot saved for {asset.StreamId}: {moveCount} moves, currently at {currentLocation}");
        }
    }

    private static async Task ArchiveEventsAsync(IArchiveCoordinator<AssetStoreSqliteOptions> archiveCoordinator)
    {
        Console.WriteLine("  Running archive process...");
        await archiveCoordinator.Archive(CancellationToken.None);
        Console.WriteLine("  ✓ Archive process complete");
    }

    private static async Task DemonstrateReadingAsync(
        IEventStore store,
        ISnapshotStore snapshotStore,
        (string PartNumber, string SerialNumber, string StreamId) asset)
    {
        Console.WriteLine($"Reading history for: {asset.StreamId}");
        
        // Load from snapshot
        var snapshot = await snapshotStore.GetLatest(asset.StreamId);
        
        if (snapshot != null)
        {
            var snapshotData = JsonSerializer.Deserialize<AssetSnapshot>(new ReadOnlySpan<byte>(snapshot.Data));
            Console.WriteLine($"  Snapshot (v{snapshot.StreamVersion}):");
            Console.WriteLine($"    - Part: {snapshotData!.PartNumber}, Serial: {snapshotData.SerialNumber}");
            Console.WriteLine($"    - Current Location: {snapshotData.CurrentLocation}");
            Console.WriteLine($"    - Total Moves: {snapshotData.MoveCount}");
            Console.WriteLine($"    - Last Updated: {snapshotData.LastUpdated:yyyy-MM-dd HH:mm:ss}");
        }
        
        // Show event count
        var allEvents = await store.ReadStream(Domain, asset.StreamId, null, StreamVersion.New(), int.MaxValue);
        Console.WriteLine($"\n  Total events in stream: {allEvents.Count}");
        Console.WriteLine($"  First 5 events:");
        
        foreach (var evt in allEvents.Take(5))
        {
            Console.WriteLine($"    - GP:{evt.GlobalPosition.Value} v{evt.StreamVersion.Value} {evt.EventType}");
        }
        
        if (allEvents.Count > 5)
        {
            Console.WriteLine($"    ... ({allEvents.Count - 5} more events)");
        }
    }

    private static async Task ShowStatisticsAsync(
        ServiceProvider services,
        IEventStore store,
        List<(string PartNumber, string SerialNumber, string StreamId)> assets)
    {
        // Read all events
        var allEventsList = new List<EventEnvelope>();
        await foreach (var evt in store.ReadAllForwards(Domain, null, null, 1000))
        {
            allEventsList.Add(evt);
        }
        var totalEvents = allEventsList.Count;
        
        // Check archive directory
        var opts = services.GetRequiredService<AssetStoreSqliteOptions>();
        var archiveFiles = Directory.Exists(opts.ArchiveDirectory) 
            ? Directory.GetFiles(opts.ArchiveDirectory, "events-*.ndjson") 
            : Array.Empty<string>();
        
        // Count snapshot records
        var snapshotStore = services.GetRequiredService<ISnapshotStore>();
        var snapshotCount = 0;
        foreach (var asset in assets)
        {
            var snapshot = await snapshotStore.GetLatest(asset.StreamId);
            if (snapshot != null) snapshotCount++;
        }
        
        Console.WriteLine($"Assets Created:        {assets.Count}");
        Console.WriteLine($"Total Events:          {totalEvents}");
        Console.WriteLine($"Snapshots Created:     {snapshotCount}");
        Console.WriteLine($"Archive Files:         {archiveFiles.Length}");
        Console.WriteLine($"Archive Directory:     {opts.ArchiveDirectory}");
        
        if (archiveFiles.Length > 0)
        {
            Console.WriteLine("\nArchive Files:");
            foreach (var file in archiveFiles)
            {
                var fileInfo = new FileInfo(file);
                Console.WriteLine($"  - {Path.GetFileName(file)} ({fileInfo.Length / 1024.0:F2} KB)");
            }
        }
        
        // Show per-asset statistics
        Console.WriteLine("\nPer-Asset Summary:");
        foreach (var asset in assets)
        {
            var header = await store.GetStreamHeader(Domain, asset.StreamId);
            var snapshot = await snapshotStore.GetLatest(asset.StreamId);
            var snapshotData = snapshot != null 
                ? JsonSerializer.Deserialize<AssetSnapshot>(new ReadOnlySpan<byte>(snapshot.Data))
                : null;
            
            Console.WriteLine($"  {asset.StreamId}:");
            Console.WriteLine($"    Events: {header?.LastVersion ?? 0}, " +
                            $"Location: {snapshotData?.CurrentLocation ?? "Unknown"}");
        }
    }
}

