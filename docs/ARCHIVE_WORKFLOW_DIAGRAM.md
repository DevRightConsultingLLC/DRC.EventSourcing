# Archive Workflow Diagrams

## Unified Archive Process

### High-Level Flow

```
┌─────────────────────────────────────────────────────────────┐
│           Archive Coordinator.Archive()                      │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  1. Query streams with ArchiveCutoffVersion set             │
│     WHERE RetentionMode IN (FullHistory, ColdArchivable)    │
│     AND ArchiveCutoffVersion IS NOT NULL                    │
│                                                              │
│  2. For each stream:                                        │
│     ├─ Check RetentionMode                                  │
│     ├─ FullHistory → ArchiveWithoutDeletionAsync()          │
│     ├─ ColdArchivable → ArchiveStreamAsync()                │
│     └─ HardDeletable → DeleteStreamAsync()                  │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

## Retention Mode Decision Tree

```
                    Archive Process Started
                            │
                            ▼
                  ┌─────────────────────┐
                  │ Query Stream Headers │
                  └─────────────────────┘
                            │
                            ▼
              ┌─────────────────────────┐
              │ For Each Stream Header   │
              └─────────────────────────┘
                            │
                            ▼
                ┌───────────────────────┐
                │  What RetentionMode?  │
                └───────────────────────┘
                            │
        ┌───────────────────┼───────────────────┐
        │                   │                   │
        ▼                   ▼                   ▼
   ┌─────────┐      ┌──────────────┐    ┌──────────────┐
   │ Default │      │ FullHistory  │    │ColdArchivable│
   │  Skip   │      │   Archive    │    │   Archive    │
   └─────────┘      │  Keep Hot ✓  │    │  Delete Hot✂️ │
                    └──────────────┘    └──────────────┘
                                                │
                                                ▼
                                        ┌──────────────┐
                                        │HardDeletable │
                                        │ Delete All ✂️✂️│
                                        └──────────────┘
```

## Detailed Archiving Workflow

### Common Path (FullHistory & ColdArchivable)

```
┌─────────────────────────────────────────────────────────────┐
│              Step 1: Validate & Prepare                      │
├─────────────────────────────────────────────────────────────┤
│  ✓ Check ArchiveCutoffVersion exists                        │
│  ✓ ArchiveCutoffVersion > 0                                 │
│  ✓ Open database connection                                 │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│              Step 2: Query Events to Archive                 │
├─────────────────────────────────────────────────────────────┤
│  SELECT * FROM Events                                        │
│  WHERE StreamDomain = @Domain                                │
│    AND StreamId = @StreamId                                  │
│    AND StreamVersion <= @CutoffVersion                       │
│  ORDER BY GlobalPosition                                     │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│              Step 3: Begin Transaction                       │
├─────────────────────────────────────────────────────────────┤
│  BEGIN TRANSACTION                                           │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│              Step 4: Check for Overlaps                      │
├─────────────────────────────────────────────────────────────┤
│  SELECT COUNT(*) FROM ArchiveSegments                        │
│  WHERE MinPosition <= @MaxPos                                │
│    AND MaxPosition >= @MinPos                                │
│                                                              │
│  If overlap exists → ROLLBACK and SKIP                       │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│              Step 5: Write NDJSON File                       │
├─────────────────────────────────────────────────────────────┤
│  Create: events-{minPos:D16}-{maxPos:D16}.ndjson           │
│  Write each event as JSON line                               │
│  Atomic rename (temp → final)                                │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│              Step 6: Record Segment Metadata                 │
├─────────────────────────────────────────────────────────────┤
│  INSERT INTO ArchiveSegments                                 │
│    (MinPosition, MaxPosition, FileName, Status)              │
│  VALUES (@MinPos, @MaxPos, @FileName, 1)                     │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
              ┌─────────────────────────┐
              │    Decision Point!       │
              │  Delete Hot Events?      │
              └─────────────────────────┘
                            │
                ┌───────────┴───────────┐
                │                       │
                ▼                       ▼
        ┌──────────────┐        ┌──────────────┐
        │ FullHistory  │        │ColdArchivable│
        │  Keep Events │        │Delete Events │
        │     ✓        │        │   DELETE ✂️   │
        └──────────────┘        └──────────────┘
                │                       │
                └───────────┬───────────┘
                            ▼
┌─────────────────────────────────────────────────────────────┐
│              Step 7: Commit Transaction                      │
├─────────────────────────────────────────────────────────────┤
│  COMMIT                                                      │
└─────────────────────────────────────────────────────────────┘
```

## Data Flow Visualizations

### FullHistory: Keep Everything

```
Before Archive:
┌────────────────────────────────────────────────────────────┐
│ Hot Storage (Database)                                      │
├────────────────────────────────────────────────────────────┤
│ [E1][E2][E3][E4][E5][E6][E7][E8][E9][E10]                  │
│  ^                  ^                                       │
│  └── ArchiveCutoffVersion = 5 ──┘                          │
└────────────────────────────────────────────────────────────┘

After Archive:
┌────────────────────────────────────────────────────────────┐
│ Cold Storage (NDJSON File)                                  │
├────────────────────────────────────────────────────────────┤
│ events-0000000000000001-0000000000000005.ndjson            │
│ [E1][E2][E3][E4][E5]                                       │
└────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────┐
│ Hot Storage (Database) - UNCHANGED                          │
├────────────────────────────────────────────────────────────┤
│ [E1][E2][E3][E4][E5][E6][E7][E8][E9][E10]                  │
│           ✓ Still available for fast access                 │
└────────────────────────────────────────────────────────────┘

Result: Events 1-5 readable from BOTH hot and cold
        Events 6-10 readable from hot only
```

### ColdArchivable: Save Space

```
Before Archive:
┌────────────────────────────────────────────────────────────┐
│ Hot Storage (Database)                                      │
├────────────────────────────────────────────────────────────┤
│ [E1][E2][E3][E4][E5][E6][E7][E8][E9][E10]                  │
│  ^                  ^                                       │
│  └── ArchiveCutoffVersion = 5 ──┘                          │
└────────────────────────────────────────────────────────────┘

After Archive:
┌────────────────────────────────────────────────────────────┐
│ Cold Storage (NDJSON File)                                  │
├────────────────────────────────────────────────────────────┤
│ events-0000000000000001-0000000000000005.ndjson            │
│ [E1][E2][E3][E4][E5]                                       │
└────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────┐
│ Hot Storage (Database) - CLEANED                            │
├────────────────────────────────────────────────────────────┤
│ [  ][  ][  ][  ][  ][E6][E7][E8][E9][E10]                  │
│  ✂️ Deleted to save space                                   │
└────────────────────────────────────────────────────────────┘

Result: Events 1-5 readable from cold only
        Events 6-10 readable from hot only
        Database storage reduced by ~50%
```

### HardDeletable: Permanent Deletion

```
Before Deletion:
┌────────────────────────────────────────────────────────────┐
│ Hot Storage (Database)                                      │
├────────────────────────────────────────────────────────────┤
│ Events:  [E1][E2][E3][E4][E5][E6][E7][E8][E9][E10]         │
│ Stream:  {StreamHeader with metadata}                       │
└────────────────────────────────────────────────────────────┘

After Deletion:
┌────────────────────────────────────────────────────────────┐
│ Cold Storage - NONE (No archiving performed)                │
├────────────────────────────────────────────────────────────┤
│ (empty)                                                     │
└────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────┐
│ Hot Storage (Database) - COMPLETELY REMOVED                 │
├────────────────────────────────────────────────────────────┤
│ Events:  [  ][  ][  ][  ][  ][  ][  ][  ][  ][  ]          │
│ Stream:  (removed from Streams table)                       │
│           ✂️✂️ PERMANENT DELETION                            │
└────────────────────────────────────────────────────────────┘

⚠️  Result: Stream and ALL events are GONE
    NO RECOVERY POSSIBLE
    Use for GDPR compliance / sensitive data removal
```

## Combined Event Feed

### Reading from Both Hot and Cold Storage

```
┌─────────────────────────────────────────────────────────────┐
│              ICombinedEventFeed.ReadAllForwards()            │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
              ┌─────────────────────────┐
              │ Get Archive Segments     │
              │ from ArchiveSegments     │
              │ ORDER BY MinPosition     │
              └─────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                    Merge Process                             │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌──────────────────┐         ┌──────────────────┐         │
│  │  Cold Archive    │         │   Hot Database   │         │
│  │  NDJSON Files    │         │   Events Table   │         │
│  └──────────────────┘         └──────────────────┘         │
│           │                             │                   │
│           ▼                             ▼                   │
│  ┌──────────────────┐         ┌──────────────────┐         │
│  │ Read line by line│         │  Query WHERE     │         │
│  │ Parse JSON       │         │  GlobalPosition  │         │
│  │ Deserialize      │         │  > maxArchived   │         │
│  └──────────────────┘         └──────────────────┘         │
│           │                             │                   │
│           └──────────┬──────────────────┘                   │
│                      ▼                                      │
│            ┌──────────────────┐                             │
│            │ Merge by Position│                             │
│            │ ORDER BY ASC     │                             │
│            └──────────────────┘                             │
│                      │                                      │
│                      ▼                                      │
│            ┌──────────────────┐                             │
│            │ Yield Events     │                             │
│            │ In Correct Order │                             │
│            └──────────────────┘                             │
└─────────────────────────────────────────────────────────────┘

Example Result:
  Event GP:1  ← From Cold (events-0000000000000001-0000000000000005.ndjson)
  Event GP:2  ← From Cold
  Event GP:3  ← From Cold
  Event GP:4  ← From Cold
  Event GP:5  ← From Cold
  Event GP:6  ← From Hot (Database)
  Event GP:7  ← From Hot
  Event GP:8  ← From Hot
  Event GP:9  ← From Hot
  Event GP:10 ← From Hot
```

## Snapshot & Archive Integration

```
┌─────────────────────────────────────────────────────────────┐
│          Application: Aggregate Rehydration                  │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│              1. Load Latest Snapshot                         │
├─────────────────────────────────────────────────────────────┤
│  var snapshot = await snapshotStore.GetSnapshot(             │
│      streamId, StreamVersion.Any());                         │
│                                                              │
│  // Snapshot at version 50                                  │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│              2. Read Events After Snapshot                   │
├─────────────────────────────────────────────────────────────┤
│  await foreach (var evt in combinedFeed.ReadAllForwards(     │
│      fromExclusive: snapshot.GlobalPosition))                │
│  {                                                           │
│      aggregate.Apply(evt); // Events 51-100                  │
│  }                                                           │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│              3. Save New Snapshot                            │
├─────────────────────────────────────────────────────────────┤
│  await snapshotCoordinator.SaveSnapshotAndAdvanceCutoff(     │
│      domain, streamId,                                       │
│      atVersion: 100,                                         │
│      snapshotData);                                          │
│                                                              │
│  // Sets ArchiveCutoffVersion = 100                          │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│              4. Archive Process Runs                         │
├─────────────────────────────────────────────────────────────┤
│  await archiveCoordinator.Archive();                         │
│                                                              │
│  // Archives events 1-100 (based on cutoff)                  │
│  // Creates: events-0000000000000001-0000000000000100.ndjson│
│  // ColdArchivable: Deletes events 1-100 from hot           │
│  // FullHistory: Keeps events 1-100 in hot                   │
└─────────────────────────────────────────────────────────────┘
```

## Error Handling & Edge Cases

```
┌─────────────────────────────────────────────────────────────┐
│              Archive Process Error Handling                  │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  No CutoffVersion Set?                                       │
│  └─> Skip (return early)                                    │
│                                                              │
│  CutoffVersion <= 0?                                         │
│  └─> Skip (return early)                                    │
│                                                              │
│  No Events Found?                                            │
│  └─> Skip (return early)                                    │
│                                                              │
│  Overlapping Segment Exists?                                │
│  └─> ROLLBACK transaction and skip                          │
│      (prevents duplicate archives)                           │
│                                                              │
│  File Write Fails?                                           │
│  └─> ROLLBACK transaction and throw                         │
│      (ensures atomicity)                                     │
│                                                              │
│  Database Error?                                             │
│  └─> ROLLBACK transaction and throw                         │
│      (maintains consistency)                                 │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

## Performance Characteristics

```
┌────────────────────────────────────┬──────────────┬──────────────┐
│ Operation                          │ FullHistory  │ColdArchivable│
├────────────────────────────────────┼──────────────┼──────────────┤
│ Archive Speed                      │    Fast      │    Fast      │
│ (Write NDJSON + Update DB)         │              │              │
├────────────────────────────────────┼──────────────┼──────────────┤
│ Hot Read Performance               │   Fastest    │    Fast      │
│ (Recent events)                    │   (all hot)  │  (some hot)  │
├────────────────────────────────────┼──────────────┼──────────────┤
│ Cold Read Performance              │    Fast      │    Fast      │
│ (Archived events)                  │   (+ hot)    │  (cold only) │
├────────────────────────────────────┼──────────────┼──────────────┤
│ Database Size                      │    Large     │    Small     │
│                                    │ (keeps all)  │  (archives)  │
├────────────────────────────────────┼──────────────┼──────────────┤
│ Archive Storage                    │   Growing    │   Growing    │
│                                    │(duplicates)  │  (primary)   │
└────────────────────────────────────┴──────────────┴──────────────┘

Recommendations:
- FullHistory: When read performance matters most
- ColdArchivable: When database size matters most
- Both: Use snapshots to minimize event replay
```

## Summary

The unified archiving workflow provides:

- ✅ **Consistent Process**: Same workflow for both retention modes
- ✅ **Flexible Storage**: Choose based on your needs
- ✅ **Atomic Operations**: Transactions ensure consistency
- ✅ **Duplicate Prevention**: Overlap checking prevents issues
- ✅ **Seamless Reading**: Combined feed merges hot and cold
- ✅ **Incremental**: Archive grows with snapshots
- ✅ **Performant**: Optimized for both read and write operations

The single decision point (delete hot events or not) makes the system easy to understand while supporting diverse retention requirements.

