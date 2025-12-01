# Archive Unification Summary
This makes the system easier to understand, maintain, and extend while providing flexible retention policies for different use cases.

- **One difference**: Delete hot events or not
- **Same metadata**: ArchiveSegments table
- **Same validation**: Overlap checking, transactions
- **Same file format**: `events-{min}-{max}.ndjson`

The unified archiving workflow treats **FullHistory** and **ColdArchivable** as variations of the same process:

## Summary

3. **No Breaking Changes**: Existing code and data structures remain compatible
2. **Cutoff Required**: FullHistory now requires `ArchiveCutoffVersion` to be set
1. **File Naming**: Old `backup-*` files won't be read - rename to `events-*` format

If upgrading from a system where FullHistory and ColdArchivable were separate:

## Migration from Old Approach

- Combined feed optimizes reads
- Cold storage in efficient file format
- Hot database stays lean (with ColdArchivable)
### ✅ Performance

- Audit trail with archive segment metadata
- HardDeletable: GDPR "right to be forgotten"
- FullHistory: Meet regulatory archive requirements
### ✅ Compliance Ready

- Archive files are compact NDJSON format
- FullHistory provides fast access to recent events
- ColdArchivable saves database space
### ✅ Storage Efficiency

- Incremental archiving based on snapshots
- Policy-driven retention per domain
- Choose between keeping or deleting hot events
### ✅ Flexibility

- Easier to understand and maintain
- Consistent behavior and validation
- One archiving process for both retention modes
### ✅ Unified Workflow

## Benefits

```
}
    }
        }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            await _archiveCoordinator.Archive(stoppingToken);
        {
        while (!stoppingToken.IsCancellationRequested)
    {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
public class ArchiveBackgroundService : BackgroundService
// Or schedule it (e.g., with BackgroundService)

await archiveCoordinator.Archive();
var archiveCoordinator = serviceProvider.GetRequiredService<IArchiveCoordinator>();
// Manual archiving
```csharp

### Running Archive Process

```
});
    return provider;
    
    });
        RetentionMode = RetentionMode.HardDeletable
        Domain = "temp",
    {
    provider.AddOrReplace(new DomainRetentionPolicy
    // Permanent deletion
    
    });
        RetentionMode = RetentionMode.ColdArchivable
        Domain = "logs",
    {
    provider.AddOrReplace(new DomainRetentionPolicy
    // Archive old events, delete from hot storage
    
    });
        RetentionMode = RetentionMode.FullHistory
        Domain = "orders",
    {
    provider.AddOrReplace(new DomainRetentionPolicy
    // Archive old events, keep hot copies
    
    var provider = new InMemoryRetentionPolicyProvider();
{
services.AddSingleton<IRetentionPolicyProvider>(sp =>
// Define retention policies per domain

});
    options.ArchiveDirectory = "./archive"; // Cold storage location
    options.StoreName = "MyStore";
    options.ConnectionString = "Data Source=events.db";
{
services.AddSqliteEventStore<MyStore>(options =>
// Configure with archive directory
```csharp

### Setting Up Archiving

## Configuration

```
WHERE domain = @Domain AND stream_id = @StreamId;
DELETE FROM Streams
-- Delete stream header

WHERE StreamDomain = @Domain AND StreamId = @StreamId;
DELETE FROM Events
-- Delete all events
```sql

### Hard Delete (HardDeletable)

```
  AND GlobalPosition BETWEEN @MinPos AND @MaxPos
  AND StreamId = @StreamId
WHERE StreamDomain = @Domain
DELETE FROM Events
```sql

### Delete Archived Events (ColdArchivable Only)

```
ORDER BY GlobalPosition
  AND StreamVersion <= @CutoffVersion
  AND StreamId = @StreamId
WHERE StreamDomain = @Domain
FROM Events
       EventType, Data, Metadata, CreatedUtc
SELECT GlobalPosition, StreamId, StreamVersion, StreamNamespace,
```sql

### Archive Events Query

```
)
    AND IsDeleted = 1
    RetentionMode = @HardDeletable
    -- HardDeletable marked for deletion
OR (
)
    AND IsDeleted = 0
    AND ArchiveCutoffVersion IS NOT NULL
    RetentionMode IN (@FullHistory, @ColdArchivable)
    -- FullHistory OR ColdArchivable with cutoff set
WHERE (
SELECT * FROM Streams
```sql

Both SQLite and SQL Server query for streams that need archiving:

### Query for Archivable Streams

## Implementation Details

```
}
    // Merged in correct order by GlobalPosition
    // 2. Hot database (recent events)
    // 1. Cold archive files (older events)
    // Transparently reads from:
{
await foreach (var evt in combinedFeed.ReadAllForwards())
```csharp

The `CombinedEventFeed` seamlessly reads from both hot and cold storage:

## Combined Event Feed

```
// Next archive run will archive events up to version 50
// This sets ArchiveCutoffVersion = 50 for the stream

);
    snapshotData: serializedSnapshot
    atVersion: 50,
    streamId: "order-123",
    domain: "orders",
await snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(
// Save snapshot and advance cutoff
```csharp

Snapshots work with the `SnapshotCoordinator` to automatically advance the archive cutoff:

## Snapshot Integration

```
// Result: Events 51-75 archived (new segment created)

await archiveCoordinator.Archive();
// Next run with cutoff at 75
// Result: Events 1-50 archived

await archiveCoordinator.Archive();
// Stream has 100 events, cutoff set to version 50
```csharp

Archiving is **incremental** based on the `ArchiveCutoffVersion`:

## Incremental Archiving

| StreamNamespace | TEXT | Optional namespace grouping |
| Status | INTEGER | 1 = Active |
| FileName | TEXT | NDJSON file name |
| MaxPosition | BIGINT | Last global position in file |
| MinPosition | BIGINT | First global position in file |
| SegmentId | INTEGER/BIGINT | Auto-incrementing primary key |
|--------|------|-------------|
| Column | Type | Description |

Metadata about each archive file is stored in the `ArchiveSegments` table:

### Archive Segments Table

```
{"globalPosition":2,"streamId":"order-123","streamVersion":2,"streamNamespace":"orders","eventType":"ItemAdded","createdUtc":"2025-11-30T10:01:00Z","data":"eyJpdGVtSWQiOiI0NTYifQ==","metadata":null}
{"globalPosition":1,"streamId":"order-123","streamVersion":1,"streamNamespace":"orders","eventType":"OrderCreated","createdUtc":"2025-11-30T10:00:00Z","data":"eyJvcmRlcklkIjoiMTIzIn0=","metadata":null}
```json

Each line is a complete JSON object:

### File Format

Example: `events-0000000000000001-0000000000000010.ndjson`

```
events-{MinPosition:D16}-{MaxPosition:D16}.ndjson
```

### File Naming Convention

All archived events are stored in NDJSON (Newline Delimited JSON) files:

## Archive Segment Structure

```
// Use case: GDPR compliance, sensitive data removal
// Result: PERMANENT DELETION - no recovery possible

Streams Table: (stream header removed)
Hot Store:    (empty - all deleted)
// Both events AND stream header are removed

// NO archiving - events are permanently deleted
```csharp

### HardDeletable Behavior

```
// Result: Old events only in cold, recent events in hot

Hot Store:  [  ][  ][  ][  ][  ][E6][E7][E8][E9][E10]
// Hot events are deleted to save space

Cold Store: [E1][E2][E3][E4][E5]
// Events are archived to cold storage
```csharp

### ColdArchivable Behavior

```
// Result: Events readable from BOTH hot and cold

Hot Store:  [E1][E2][E3][E4][E5][E6][E7][E8][E9][E10]
// Hot events remain in place

Cold Store: [E1][E2][E3][E4][E5]
// Events are archived to cold storage
```csharp

### FullHistory Behavior

8. **Commit Transaction** - Finalize changes
7. **Decision Point** - Delete hot events (ColdArchivable) or keep them (FullHistory)
6. **Record Segment** - Insert metadata into ArchiveSegments table
5. **Write NDJSON File** - Save events to `events-{min}-{max}.ndjson`
4. **Check for Overlaps** - Prevent duplicate archive segments
3. **Begin Transaction** - Ensure atomicity
2. **Read Events** - Query events up to the cutoff version
1. **Check ArchiveCutoffVersion** - Only archive if a cutoff version is set

Both **FullHistory** and **ColdArchivable** follow these steps:

## Unified Archiving Workflow

| **HardDeletable** | ❌ No | ✅ Yes (all) | ✅ Yes | ❌ No |
| **ColdArchivable** | ✅ Yes | ✅ Yes | ❌ No | ✅ Yes |
| **FullHistory** | ✅ Yes | ❌ No | ❌ No | ✅ Yes |
| **Default** | ❌ No | N/A | ❌ No | ❌ No |
|------|-----------------|---------------------|------------------------|------------------|
| Mode | Archives Events? | Deletes Hot Events? | Deletes Stream Header? | Requires Cutoff? |

## Retention Mode Behaviors

> **FullHistory and ColdArchivable use the EXACT SAME archiving workflow. The ONLY difference is whether hot events are deleted after archiving.**

## Core Principle

The DRC.EventSourcing library implements a unified archiving workflow where **FullHistory** and **ColdArchivable** retention modes follow the same process, with only one difference: whether hot events are deleted after archiving.

## Overview


