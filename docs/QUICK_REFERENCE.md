# Quick Reference Guide

## Installation

```bash
# SQLite Provider
dotnet add package DRC.EventSourcing.Sqlite

# SQL Server Provider
dotnet add package DRC.EventSourcing.SqlServer
```

## Basic Setup

### SQLite Configuration

```csharp
using DRC.EventSourcing.Sqlite;

services.AddSqliteEventStore<MyStore>(options =>
{
    options.ConnectionString = "Data Source=events.db";
    options.StoreName = "MyStore";
    options.ArchiveDirectory = "./archive";
});
```

### SQL Server Configuration

```csharp
using DRC.EventSourcing.SqlServer;

services.AddSqlServerEventStore<MyStore>(options =>
{
    options.ConnectionString = "Server=.;Database=EventStore;Trusted_Connection=true";
    options.StoreName = "MyStore";
    options.Schema = "dbo";
    options.ArchiveDirectory = "./archive";
});
```

## Core Operations

### Append Events

```csharp
// Create events
var events = new[]
{
    new EventData("OrderCreated", eventData, metadata),
    new EventData("ItemAdded", eventData, metadata)
};

// Append to stream
var newVersion = await eventStore.AppendToStream(
    domain: "orders",
    streamId: "order-123",
    expectedVersion: StreamVersion.New(), // or specific version
    events: events
);
```

### Read Stream

```csharp
// Read all events from a stream
await foreach (var envelope in eventStore.ReadStream(
    domain: "orders",
    streamId: "order-123",
    namespace: null,
    fromVersionExclusive: StreamVersion.Start,
    maxCount: 100))
{
    Console.WriteLine($"{envelope.EventType} at v{envelope.StreamVersion.Value}");
}
```

### Read All Events

```csharp
// Read all events across all streams
await foreach (var envelope in eventStore.ReadAll(
    fromPositionExclusive: GlobalPosition.Start,
    maxCount: 100))
{
    Console.WriteLine($"GP:{envelope.GlobalPosition.Value} - {envelope.EventType}");
}
```

## Retention Policies

### Configure Policies

```csharp
services.AddSingleton<IRetentionPolicyProvider>(sp =>
{
    var provider = new InMemoryRetentionPolicyProvider();
    
    // Keep everything in hot storage (default)
    provider.AddOrReplace(new DomainRetentionPolicy
    {
        Domain = "critical",
        RetentionMode = RetentionMode.Default
    });
    
    // Archive old, keep hot copies
    provider.AddOrReplace(new DomainRetentionPolicy
    {
        Domain = "orders",
        RetentionMode = RetentionMode.FullHistory
    });
    
    // Archive old, delete from hot
    provider.AddOrReplace(new DomainRetentionPolicy
    {
        Domain = "logs",
        RetentionMode = RetentionMode.ColdArchivable
    });
    
    // Permanent deletion
    provider.AddOrReplace(new DomainRetentionPolicy
    {
        Domain = "temp",
        RetentionMode = RetentionMode.HardDeletable
    });
    
    return provider;
});
```

### Retention Mode Quick Reference

| Mode | Archives? | Deletes Hot? | Deletes Stream? | Use Case |
|------|-----------|--------------|-----------------|----------|
| `Default` | ❌ | ❌ | ❌ | Keep everything hot |
| `FullHistory` | ✅ | ❌ | ❌ | Compliance + Speed |
| `ColdArchivable` | ✅ | ✅ | ❌ | Save database space |
| `HardDeletable` | ❌ | ✅ | ✅ | GDPR / Cleanup |

## Snapshots

### Save Snapshot

```csharp
var snapshotData = JsonSerializer.SerializeToUtf8Bytes(aggregate);

await snapshotStore.SaveSnapshot(
    streamId: "order-123",
    streamVersion: new StreamVersion(50),
    data: snapshotData
);
```

### Load Snapshot

```csharp
var snapshot = await snapshotStore.GetSnapshot(
    streamId: "order-123",
    atVersion: StreamVersion.Any() // Latest
);

if (snapshot != null)
{
    var aggregate = JsonSerializer.Deserialize<OrderAggregate>(snapshot.Data);
    // Apply events after snapshot
}
```

### Save Snapshot & Advance Archive Cutoff

```csharp
// Saves snapshot AND sets ArchiveCutoffVersion
await snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(
    domain: "orders",
    streamId: "order-123",
    atVersion: 50,
    snapshotData: data
);

// Next archive run will archive events up to version 50
```

## Archiving

### Manual Archive

```csharp
var archiveCoordinator = serviceProvider.GetRequiredService<IArchiveCoordinator>();
await archiveCoordinator.Archive();
```

### Background Archiving

```csharp
public class ArchiveBackgroundService : BackgroundService
{
    private readonly IArchiveCoordinator _archiveCoordinator;
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _archiveCoordinator.Archive(stoppingToken);
            }
            catch (Exception ex)
            {
                // Log error
            }
            
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}

// Register
services.AddHostedService<ArchiveBackgroundService>();
```

## Combined Event Feed

### Read from Hot and Cold

```csharp
var combinedFeed = serviceProvider.GetRequiredService<ICombinedEventFeed>();

// Reads from BOTH archived files AND hot database
await foreach (var envelope in combinedFeed.ReadAllForwards(
    fromExclusive: null,
    maxCount: 1000))
{
    // Events merged in correct order by GlobalPosition
    Console.WriteLine($"GP:{envelope.GlobalPosition.Value}");
}
```

### Read with Starting Position

```csharp
// Start reading after position 100
await foreach (var envelope in combinedFeed.ReadAllForwards(
    fromExclusive: new GlobalPosition(100),
    maxCount: 100))
{
    // Only events with GlobalPosition > 100
}
```

## Schema Initialization

### Initialize Database Schema

```csharp
var schemaInitializer = serviceProvider
    .GetRequiredService<IEventStoreSchemaInitializer<MyStore>>();

await schemaInitializer.InitializeAsync();
```

### Drop and Recreate

```csharp
// ⚠️ WARNING: This deletes all data!
await schemaInitializer.DropAndRecreateAsync();
```

## Optimistic Concurrency

### Expected Version Handling

```csharp
try
{
    // Expect stream to be at version 5
    await eventStore.AppendToStream(
        "orders",
        "order-123",
        expectedVersion: new StreamVersion(5),
        events
    );
}
catch (ConcurrencyException ex)
{
    // Stream was modified by another process
    // Actual version: ex.ActualVersion
    // Expected version: ex.ExpectedVersion
    
    // Reload aggregate and retry
}
```

### Version Types

```csharp
// New stream (must not exist)
StreamVersion.New()

// Any version (skip concurrency check)
StreamVersion.Any()

// Start of stream
StreamVersion.Start

// Specific version
new StreamVersion(5)
```

## Common Patterns

### Event Store with Aggregate

```csharp
public class OrderAggregate
{
    public string OrderId { get; private set; }
    public List<object> Changes { get; } = new();
    
    public void Create(string orderId)
    {
        Apply(new OrderCreated(orderId));
    }
    
    private void Apply(object @event)
    {
        // Update state
        switch (@event)
        {
            case OrderCreated e:
                OrderId = e.OrderId;
                break;
        }
        Changes.Add(@event);
    }
    
    public async Task SaveAsync(IEventStore eventStore, StreamVersion expectedVersion)
    {
        var events = Changes.Select(e => new EventData(
            e.GetType().Name,
            JsonSerializer.SerializeToUtf8Bytes(e),
            null
        ));
        
        await eventStore.AppendToStream(
            "orders",
            OrderId,
            expectedVersion,
            events
        );
        
        Changes.Clear();
    }
    
    public static async Task<OrderAggregate> LoadAsync(
        IEventStore eventStore,
        string orderId)
    {
        var aggregate = new OrderAggregate();
        
        await foreach (var envelope in eventStore.ReadStream(
            "orders", orderId, null, StreamVersion.Start, int.MaxValue))
        {
            var @event = JsonSerializer.Deserialize(
                envelope.Data,
                Type.GetType(envelope.EventType)
            );
            aggregate.Apply(@event);
        }
        
        return aggregate;
    }
}
```

### Event Store with Snapshots

```csharp
public static async Task<OrderAggregate> LoadWithSnapshotAsync(
    IEventStore eventStore,
    ISnapshotStore snapshotStore,
    string orderId)
{
    OrderAggregate aggregate;
    GlobalPosition? startPosition = null;
    
    // Try to load from snapshot
    var snapshot = await snapshotStore.GetSnapshot(orderId, StreamVersion.Any());
    if (snapshot != null)
    {
        aggregate = JsonSerializer.Deserialize<OrderAggregate>(snapshot.Data);
        startPosition = snapshot.GlobalPosition;
    }
    else
    {
        aggregate = new OrderAggregate();
    }
    
    // Load events after snapshot
    var combinedFeed = // ... get from DI
    await foreach (var envelope in combinedFeed.ReadAllForwards(
        fromExclusive: startPosition,
        maxCount: int.MaxValue))
    {
        if (envelope.StreamId == orderId)
        {
            var @event = JsonSerializer.Deserialize(
                envelope.Data,
                Type.GetType(envelope.EventType)
            );
            aggregate.Apply(@event);
        }
    }
    
    return aggregate;
}
```

## Stream Headers

### Get Stream Metadata

```csharp
var header = await eventStore.GetStreamHeader("orders", "order-123");

if (header != null)
{
    Console.WriteLine($"Last Version: {header.LastVersion}");
    Console.WriteLine($"Last Position: {header.LastPosition}");
    Console.WriteLine($"Retention Mode: {header.RetentionMode}");
    Console.WriteLine($"Archive Cutoff: {header.ArchiveCutoffVersion}");
    Console.WriteLine($"Is Deleted: {header.IsDeleted}");
}
```

### Mark Stream for Deletion

```csharp
// Mark stream as deleted (for HardDeletable mode)
await eventStore.MarkStreamAsDeleted("temp", "temp-123");

// Next archive run will permanently delete it
```

## Testing

### In-Memory Testing Setup

```csharp
// Use unique store name per test
services.AddSqliteEventStore<TestStore>(options =>
{
    options.ConnectionString = $"Data Source=test_{Guid.NewGuid()}.db";
    options.StoreName = "TestStore";
    options.ArchiveDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
});

// Initialize schema
var schemaInitializer = serviceProvider
    .GetRequiredService<IEventStoreSchemaInitializer<TestStore>>();
await schemaInitializer.InitializeAsync();
```

### Cleanup After Tests

```csharp
public async ValueTask DisposeAsync()
{
    // Close connections
    await serviceProvider.DisposeAsync();
    
    // Delete test database
    if (File.Exists(dbPath))
        File.Delete(dbPath);
    
    // Delete archive directory
    if (Directory.Exists(archiveDir))
        Directory.Delete(archiveDir, true);
}
```

## Troubleshooting

### Common Issues

**Problem**: "no such column" error
```
Solution: Check column names match schema (PascalCase)
- MinPosition (not min_position)
- MaxPosition (not max_position)
```

**Problem**: Archive not running
```
Solution: Ensure ArchiveCutoffVersion is set
- Use SnapshotCoordinator.SaveSnapshotAndAdvanceCutoff()
- Or manually set via SQL/repository
```

**Problem**: Events not in combined feed
```
Solution: Check archive segment records exist
- Query ArchiveSegments table
- Verify archive files exist in ArchiveDirectory
```

**Problem**: Concurrency exceptions
```
Solution: Reload aggregate and retry with correct version
- catch (ConcurrencyException)
- Load fresh version
- Reapply changes
```

## Performance Tips

### Batch Operations

```csharp
// Better: Append multiple events at once
var events = new[]
{
    new EventData("Event1", data1, null),
    new EventData("Event2", data2, null),
    new EventData("Event3", data3, null)
};
await eventStore.AppendToStream(domain, streamId, version, events);

// Avoid: Multiple individual appends
// (causes multiple DB round trips)
```

### Use Snapshots

```csharp
// Good: Load from snapshot + recent events
var snapshot = await snapshotStore.GetSnapshot(streamId, StreamVersion.Any());
// Then apply only events after snapshot

// Avoid: Loading all events every time
// (slow for long-lived aggregates)
```

### Configure Connection Pooling

```csharp
// SQLite
options.ConnectionString = "Data Source=events.db;Pooling=true;Max Pool Size=100";

// SQL Server
options.ConnectionString = "Server=.;Database=EventStore;Max Pool Size=100;Min Pool Size=10";
```

### Tune Archive Schedule

```csharp
// Archive less frequently for write-heavy systems
await Task.Delay(TimeSpan.FromHours(6), stoppingToken);

// Archive more frequently for read-heavy systems needing clean hot storage
await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
```

## Configuration Examples

### Development

```csharp
services.AddSqliteEventStore<DevStore>(options =>
{
    options.ConnectionString = "Data Source=dev_events.db";
    options.StoreName = "DevStore";
    options.ArchiveDirectory = "./dev_archive";
});
```

### Production

```csharp
services.AddSqlServerEventStore<ProdStore>(options =>
{
    options.ConnectionString = configuration.GetConnectionString("EventStore");
    options.StoreName = "ProdStore";
    options.Schema = "eventsourcing";
    options.ArchiveDirectory = "/mnt/cold-storage/archives";
});
```

### Testing

```csharp
services.AddSqliteEventStore<TestStore>(options =>
{
    options.ConnectionString = "Data Source=:memory:";
    options.StoreName = "TestStore";
    options.ArchiveDirectory = Path.GetTempPath();
});
```

## Quick Command Reference

```bash
# Build
dotnet build

# Test
dotnet test

# Run demo
dotnet run --project samples/DRC.EventSourcing.Demo

# Pack for NuGet
dotnet pack src/ -c Release -o ./artifacts

# Clean
dotnet clean
```

## Need More Help?

- 📖 [Archive Unification Summary](ARCHIVE_UNIFICATION_SUMMARY.md) - Detailed archiving docs
- 📊 [Archive Workflow Diagram](ARCHIVE_WORKFLOW_DIAGRAM.md) - Visual guides
- 🏗️ [Solution Structure](SOLUTION_STRUCTURE.md) - Project organization
- 🐛 [GitHub Issues](https://github.com/yourusername/DRC.EventSourcing/issues) - Report bugs
- 💬 [Discussions](https://github.com/yourusername/DRC.EventSourcing/discussions) - Ask questions

