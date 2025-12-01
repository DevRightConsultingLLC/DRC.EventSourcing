﻿﻿﻿# Solution Structure Documentation

## Overview

DRC.EventSourcing follows the standard .NET solution layout with clear separation of concerns, making it easy to navigate, maintain, and extend.

## Directory Structure

```
DRC.EventSourcing/
├── src/                                # Production source code
│   ├── DRC.EventSourcing/             # Core library
│   ├── DRC.EventSourcing.Sqlite/      # SQLite provider
│   ├── DRC.EventSourcing.SqlServer/   # SQL Server provider
│   ├── DRC.EventSourcing.PostgreSQL/  # PostgreSQL provider
│   └── DRC.EventSourcing.MySql/       # MySQL/MariaDB provider
│
├── test/                               # Test projects
│   └── DRC.EventSourcing.Tests/       # Unit & integration tests
│
├── samples/                            # Demo applications
│   └── DRC.EventSourcing.Demo/        # Asset management demo
│
├── docs/                               # Documentation
│   ├── ARCHIVE_UNIFICATION_SUMMARY.md # Archive process details
│   ├── ARCHIVE_WORKFLOW_DIAGRAM.md    # Visual workflow diagrams
│   ├── QUICK_REFERENCE.md             # API quick reference
│   ├── SOLUTION_STRUCTURE.md          # This file
│   └── MIGRATION_CHECKLIST.md         # Deployment checklist
│
├── LICENSE                             # MIT License
├── README.md                           # Project documentation
├── CONTRIBUTING.md                     # Contribution guidelines
├── GITHUB_SETUP.md                     # GitHub publishing guide
├── .gitignore                          # Git ignore rules
├── .gitattributes                      # Git line endings
└── DRC.EventSourcing.sln              # Solution file
```

## Project Breakdown

### src/DRC.EventSourcing/ (Core Library)

The foundation library containing all interfaces, abstractions, and shared infrastructure.

**Key Components:**

```
DRC.EventSourcing/
├── IEventStore.cs                      # Main event store interface
├── IArchiveCoordinator.cs              # Archive coordinator interface
├── IArchiveSegmentStore.cs             # Archive segment store interface
├── IColdEventArchive.cs                # Cold event archive interface
├── ISnapshotStore.cs                   # Snapshot storage interface
├── ICombinedEventFeed.cs               # Hot+Cold reader interface
├── IEventStoreOptions.cs               # Event store options interface
├── IEventStoreSchemaInitializer.cs     # Schema initializer interface
├── EventData.cs                        # Event data value object
├── EventEnvelope.cs                    # Event envelope with metadata
├── StreamHeader.cs                     # Stream metadata
├── StreamVersion.cs                    # Stream version value object
├── GlobalPosition.cs                   # Global position value object
├── ArchiveSegment.cs                   # Archive segment value object
├── Snapshot.cs                         # Snapshot value object
├── RetentionPolicy.cs                  # Retention policy definitions
├── ConcurrencyException.cs             # Concurrency control exception
├── DbExceptionExtensions.cs            # Database exception mapping
├── EventStoreSchemaInitializationExtensions.cs # Schema init helpers
├── SnapshotCoordinator.cs              # Snapshot coordination logic
│
└── Infrastructure/                     # Base implementations
    ├── BaseEventStore.cs              # Shared event store logic
    ├── BaseArchiveCoordinator.cs      # Shared archive logic
    ├── BaseArchiveCutoffAdvancer.cs   # Archive cutoff advancement
    ├── BaseArchiveSegmentStore.cs     # Archive segment management
    ├── BaseSnapshotStore.cs           # Shared snapshot logic
    ├── BaseCombinedEventFeed.cs       # Shared feed logic
    ├── BaseSchemaInitializer.cs       # Shared schema logic
    ├── BaseConnectionFactory.cs       # Connection management
    ├── EventStoreLog.cs               # Structured logging
    ├── EventStoreMetrics.cs           # Metrics and diagnostics
    └── EventStoreServiceCollectionExtensions.cs # DI helpers
```

**Dependencies:**
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Logging.Abstractions`
- `System.Diagnostics.DiagnosticSource`

**Namespace:** `DRC.EventSourcing`

### src/DRC.EventSourcing.Sqlite/ (SQLite Provider)

Complete SQLite implementation of the event sourcing library.

**Key Components:**

```
DRC.EventSourcing.Sqlite/
├── SqliteEventStore.cs                    # SQLite event store
├── SqliteArchiveCoordinator.cs            # SQLite archiving
├── SqliteArchiveCutoffAdvancer.cs         # Cutoff advancement
├── SqliteArchiveSegmentStore.cs           # Segment metadata
├── SqliteSnapshotStore.cs                 # SQLite snapshots
├── SqliteCombinedEventFeed.cs             # SQLite combined feed
├── SqliteSchemaInitializer.cs             # Schema creation
├── SqliteConnectionFactory.cs             # Connection management
├── SqliteEventStoreOptions.cs             # Configuration
├── SqliteEventStoreServiceCollectionExtensions.cs  # DI setup
├── FileColdEventArchive.cs                # NDJSON file reader
└── INMEMORY_README.md                     # In-memory mode docs
```

**Dependencies:**
- `DRC.EventSourcing` (core library)
- `Microsoft.Data.Sqlite.Core`
- `SQLitePCLRaw.bundle_e_sqlite3`
- `Dapper`

**Namespace:** `DRC.EventSourcing.Sqlite`

### src/DRC.EventSourcing.SqlServer/ (SQL Server Provider)

Complete SQL Server implementation with identical feature set to SQLite.

**Key Components:**

```
DRC.EventSourcing.SqlServer/
├── SqlServerEventStore.cs                 # SQL Server event store
├── SqlServerArchiveCoordinator.cs         # SQL Server archiving
├── SqlServerArchiveCutoffAdvancer.cs      # Cutoff advancement
├── SqlServerArchiveSegmentStore.cs        # Segment metadata
├── SqlServerSnapshotStore.cs              # SQL Server snapshots
├── SqlServerCombinedEventFeed.cs          # SQL Server combined feed
├── SqlServerSchemaInitializer.cs          # Schema creation
├── SqlServerConnectionFactory.cs          # Connection management
├── SqlServerEventStoreOptions.cs          # Configuration
└── SqlServerEventStoreServiceCollectionExtensions.cs  # DI setup
```

**Dependencies:**
- `DRC.EventSourcing` (core library)
- `Microsoft.Data.SqlClient`
- `Dapper`

**Namespace:** `DRC.EventSourcing.SqlServer`

### src/DRC.EventSourcing.PostgreSQL/ (PostgreSQL Provider)

Complete PostgreSQL implementation following the same patterns as SQLite and SQL Server.

**Key Components:**

```
DRC.EventSourcing.PostgreSQL/
├── PostgreSQLEventStore.cs                # PostgreSQL event store
├── PostgreSQLArchiveCoordinator.cs        # PostgreSQL archiving
├── PostgreSQLArchiveCutoffAdvancer.cs     # Cutoff advancement
├── PostgreSQLArchiveSegmentStore.cs       # Segment metadata
├── PostgreSQLSnapshotStore.cs             # PostgreSQL snapshots
├── PostgreSQLCombinedEventFeed.cs         # PostgreSQL combined feed
├── PostgreSQLSchemaInitializer.cs         # Schema creation
├── PostgreSQLConnectionFactory.cs         # Connection management
├── PostgreSQLEventStoreOptions.cs         # Configuration
├── PostgreSQLEventStoreServiceCollectionExtensions.cs  # DI setup
└── README.md                              # PostgreSQL-specific docs
```

**Dependencies:**
- `DRC.EventSourcing` (core library)
- `Npgsql` (v9.0.2)
- `Dapper`

**Namespace:** `DRC.EventSourcing.PostgreSQL`

**PostgreSQL-Specific Features:**
- BIGSERIAL for auto-incrementing GlobalPosition
- ON CONFLICT DO UPDATE for native UPSERT
- FOR UPDATE for row-level locking
- Schema support for multi-tenancy
- RETURNING clause for insert operations

### src/DRC.EventSourcing.MySql/ (MySQL Provider)

Complete MySQL/MariaDB implementation following the same patterns as the other providers.

**Key Components:**

```
DRC.EventSourcing.MySql/
├── MySqlEventStore.cs                 # MySQL event store
├── MySqlArchiveCoordinator.cs         # MySQL archiving
├── MySqlSnapshotStore.cs              # MySQL snapshots
├── MySqlCombinedEventFeed.cs          # MySQL combined feed
├── MySqlArchiveSegmentStore.cs        # Segment metadata
├── MySqlArchiveCutoffAdvancer.cs      # Cutoff advancement
├── MySqlSchemaInitializer.cs          # Schema creation
├── MySqlConnectionFactory.cs          # Connection management
├── MySqlEventStoreOptions.cs          # Configuration
└── MySqlEventStoreServiceCollectionExtensions.cs  # DI setup
```

**Dependencies:**
- `DRC.EventSourcing` (core library)
- `MySqlConnector` (v2.4.0)
- `Dapper`
- `Microsoft.Extensions.DependencyInjection.Abstractions`

**Namespace:** `DRC.EventSourcing.MySql`

**MySQL-Specific Features:**
- AUTO_INCREMENT for GlobalPosition
- INSERT ... ON DUPLICATE KEY UPDATE for UPSERT
- Row-level locking with FOR UPDATE
- Compatible with MySQL 8.0+ and MariaDB 10.5+
- Connection pooling and retry logic

### test/DRC.EventSourcing.Tests/ (Test Project)

Comprehensive test suite covering all functionality.

**Test Structure:**

```
DRC.EventSourcing.Tests/
├── EventStoreTestBase.cs              # Shared test base class
├── ConcurrencyExceptionTests.cs       # Concurrency tests
├── DbExceptionExtensionsTests.cs      # Exception mapping tests
├── RetentionPolicyTests.cs            # Policy tests
├── TableNameValidationTests.cs        # Validation tests
├── EventStoreMetricsTests.cs          # Metrics tests
│
├── Sqlite/                            # SQLite-specific tests
│   ├── SqliteEventStoreTests.cs
│   ├── SqliteArchiveTests.cs
│   ├── SqliteSnapshotTests.cs
│   └── SqliteCombinedFeedTests.cs
│
├── SqlServer/                         # SQL Server-specific tests
│   ├── SqlServerEventStoreTests.cs
│   ├── SqlServerArchiveTests.cs
│   ├── SqlServerSnapshotTests.cs
│   └── SqlServerCombinedFeedTests.cs
│
├── PostgreSQL/                        # PostgreSQL-specific tests
│   ├── PostgreSQLEventStoreTests.cs
│   └── PostgreSQLArchiveTests.cs
│
└── MySql/                             # MySQL-specific tests
    ├── MySqlEventStoreTests.cs
    └── MySqlArchiveTests.cs
```

**Test Framework:**
- xUnit
- FluentAssertions
- In-memory SQLite for fast tests

### samples/DRC.EventSourcing.Demo/ (Demo Application)

Asset management demo application showing realistic event sourcing usage.

**Demo Features:**

```
DRC.EventSourcing.Demo/
├── Program.cs                         # Asset management demo
└── Demo Scenarios:
    ├── Create 5 assets (part number + serial number)
    ├── Simulate 100-200 location moves per asset
    ├── Create snapshots after movements
    ├── Archive old events to cold storage
    ├── Read from combined hot+cold feed
    └── Display comprehensive statistics
```

**Event Types:**
- `AssetCreatedEvent` - Asset registration with part/serial number
- `AssetMovedEvent` - Location changes with timestamps
- `AssetSnapshot` - Current state snapshots for fast rehydration

## Architecture Patterns

### Layered Architecture

```
┌─────────────────────────────────────────┐
│         Application Layer                │
│  (Your domain aggregates & handlers)     │
└─────────────────────────────────────────┘
                    ↓
┌─────────────────────────────────────────┐
│      DRC.EventSourcing (Core)            │
│  (Interfaces & Abstractions)             │
└─────────────────────────────────────────┘
                    ↓
┌─────────────────────────────────────────┐
│      Provider Implementation             │
│  (SQLite / SQL Server / PostgreSQL /    │
│   MySQL)                                 │
└─────────────────────────────────────────┘
                    ↓
┌─────────────────────────────────────────┐
│         Database / File System           │
│  (Physical storage)                      │
└─────────────────────────────────────────┘
```

### Infrastructure Pattern

Base classes in `Infrastructure/` provide common functionality:

- **BaseEventStore**: Transaction management, validation
- **BaseArchiveCoordinator**: Archive workflow orchestration
- **BaseArchiveCutoffAdvancer**: Cutoff position advancement logic
- **BaseArchiveSegmentStore**: Archive segment metadata management
- **BaseSnapshotStore**: Snapshot serialization
- **BaseCombinedEventFeed**: Hot/Cold merge logic
- **BaseSchemaInitializer**: Schema versioning
- **EventStoreLog**: Structured logging helpers
- **EventStoreMetrics**: Metrics and diagnostics recording
- **EventStoreServiceCollectionExtensions**: Common DI registration helpers

Provider implementations inherit and override database-specific operations.

### Factory Pattern

Each provider uses factories for connection management:

```csharp
public class SqliteConnectionFactory<TStore> : IConnectionFactory
{
    public IDbConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }
}
```

This enables:
- Multiple stores in one application
- Easy testing with different connection strings
- Connection pooling configuration

### Repository Pattern

The event store acts as a repository for events:

```csharp
public interface IEventStore
{
    // Append events
    Task<StreamVersion> AppendToStream(...);
    
    // Read events
    IAsyncEnumerable<EventEnvelope> ReadStream(...);
    IAsyncEnumerable<EventEnvelope> ReadAll(...);
    
    // Metadata
    Task<StreamHeader?> GetStreamHeader(...);
}
```

## Dependency Injection

### Service Registration

Each provider includes extension methods for DI:

```csharp
// SQLite
services.AddSqliteEventStore<MyStore>(options => { ... });

// SQL Server  
services.AddSqlServerEventStore<MyStore>(options => { ... });

// PostgreSQL
services.AddPostgreSQLEventStore<MyStore>(options => { ... });

// MySQL
services.AddMySqlEventStore<MyStore>(options => { ... });
```

### Registered Services

```csharp
// Core services (per store)
IEventStore<TStore>
IArchiveCoordinator<TStore>
ISnapshotStore<TStore>
ICombinedEventFeed<TStore>
IArchiveSegmentStore
IEventStoreSchemaInitializer<TStore>

// Configuration
TStore : EventStoreOptions

// Infrastructure
IConnectionFactory<TStore>
```

### Multi-Store Support

```csharp
// Register multiple stores
services.AddSqliteEventStore<OrderStore>(options => 
{
    options.StoreName = "Orders";
    // ...
});

services.AddSqliteEventStore<LogStore>(options => 
{
    options.StoreName = "Logs";
    // ...
});

// Resolve specific stores
var orderStore = serviceProvider.GetRequiredService<IEventStore<OrderStore>>();
var logStore = serviceProvider.GetRequiredService<IEventStore<LogStore>>();
```

## Build Configuration

### Target Framework

All projects target **.NET 9.0**:

```xml
<TargetFramework>net9.0</TargetFramework>
```

### NuGet Package Configuration

Each source project includes package metadata:

```xml
<PropertyGroup>
    <PackageId>DRC.EventSourcing.Sqlite</PackageId>
    <Version>1.0.0</Version>
    <Authors>DRC.EventSourcing Contributors</Authors>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <Description>...</Description>
    <PackageTags>eventsourcing;sqlite;cqrs</PackageTags>
</PropertyGroup>
```

### Project References

```
DRC.EventSourcing (Core)
    ↑
    ├── DRC.EventSourcing.Sqlite
    ├── DRC.EventSourcing.SqlServer
    ├── DRC.EventSourcing.PostgreSQL
    ├── DRC.EventSourcing.MySql
    │
    └── DRC.EventSourcing.Tests
            ↑
            └── (References all implementations)
```

## Extension Points

### Adding a New Storage Provider

The PostgreSQL provider demonstrates how to add a new database:

1. **Create new project**: `src/DRC.EventSourcing.{Provider}/`

2. **Implement core interfaces**:
   ```csharp
   public class PostgreSQLEventStore : BaseEventStore<PostgreSQLEventStoreOptions>
   {
       // Override abstract methods
   }
   ```

3. **Add service extensions**:
   ```csharp
   public static class {Provider}EventStoreServiceCollectionExtensions
   {
       public static IServiceCollection Add{Provider}EventStore<TStore>(...)
       {
           // Register services - see PostgreSQL implementation for reference
       }
   }
   ```

4. **Create tests**: `test/DRC.EventSourcing.Tests/{Provider}/`

5. **Update documentation**

**Reference Implementation:** See `DRC.EventSourcing.PostgreSQL` for a complete example of adding a new provider.

### Adding New Features

Features should be added to:
1. **Core library** (interface/abstraction)
2. **Each provider** (implementation - SQLite, SQL Server, PostgreSQL, MySQL)
3. **Tests** (coverage for each provider)
4. **Documentation** (usage examples)

## Testing Strategy

### Unit Tests

- Test business logic in isolation
- Mock dependencies
- Fast execution (<1s per test)

### Integration Tests

- Test against real databases
- Use in-memory SQLite for speed
- Clean up after each test

### Test Organization

```
Tests are organized by:
1. Feature (EventStore, Archive, Snapshot, etc.)
2. Provider (SQLite, SqlServer)
3. Scenario (Happy path, Edge cases, Errors)
```

### Running Tests

```bash
# All tests
dotnet test

# Specific provider
dotnet test --filter "FullyQualifiedName~Sqlite"

# Specific feature
dotnet test --filter "FullyQualifiedName~Archive"

# With coverage
dotnet test /p:CollectCoverage=true
```

## CI/CD Integration

### Build Pipeline

```yaml
- Restore dependencies
- Build solution
- Run tests
- Pack NuGet packages
- Publish artifacts
```

### Deployment Pipeline

```yaml
- Validate package metadata
- Push to NuGet.org
- Create GitHub release
- Update documentation
```

## Documentation Strategy

### Code Documentation

- XML comments for all public APIs
- Examples in comments where helpful
- Clear parameter descriptions

### Project Documentation

- **README.md**: Quick start and overview
- **docs/**: Detailed technical documentation
- **CONTRIBUTING.md**: Contribution guidelines
- **Inline comments**: Complex logic explained

### Documentation Files

| File | Purpose |
|------|---------|
| README.md | Project overview, quick start |
| ARCHIVE_UNIFICATION_SUMMARY.md | Archive process details |
| ARCHIVE_WORKFLOW_DIAGRAM.md | Visual workflows |
| QUICK_REFERENCE.md | API quick reference |
| SOLUTION_STRUCTURE.md | This file - solution organization |
| MIGRATION_CHECKLIST.md | Upgrade and deployment guide |
| CONTRIBUTING.md | Contribution guidelines |
| GITHUB_SETUP.md | GitHub publishing instructions |
| src/DRC.EventSourcing.PostgreSQL/README.md | PostgreSQL provider documentation |
| src/DRC.EventSourcing.Sqlite/INMEMORY_README.md | SQLite in-memory mode documentation |

## Best Practices

### Code Style

- Follow C# naming conventions
- Use meaningful names
- Keep methods focused
- Prefer composition over inheritance
- Use async/await consistently

### Error Handling

- Specific exceptions for business rules
- Generic exceptions for unexpected errors
- Rollback transactions on failure
- Log errors with context

### Performance

- Use connection pooling
- Batch operations where possible
- Implement snapshots for large streams
- Archive old events to save space
- Use indexes on frequently queried columns

### Security

- Parameterized queries (prevent SQL injection)
- Validate input parameters
- No sensitive data in logs
- Secure connection strings

## Versioning

### Semantic Versioning

```
MAJOR.MINOR.PATCH

1.0.0 - Initial release
1.1.0 - New features (backward compatible)
1.1.1 - Bug fixes
2.0.0 - Breaking changes
```

### Breaking Changes

Avoid breaking changes in minor/patch releases:
- Add new features with opt-in
- Deprecate old features before removing
- Provide migration guides
- Version APIs if needed

## Summary

The DRC.EventSourcing solution structure provides:

- ✅ **Clear Organization**: Well-organized src/test/samples/docs structure
- ✅ **Industry Standard**: Follows .NET conventions and best practices
- ✅ **Maintainable**: Separated concerns with Infrastructure base classes
- ✅ **Extensible**: Easy to add providers (PostgreSQL added as reference)
- ✅ **Testable**: Comprehensive test coverage across all providers
- ✅ **Professional**: Production-ready with MIT license

### Current State (November 2025)

**Core Library:** ✅ Complete
- Event sourcing interfaces and abstractions
- Archive and snapshot support
- Retention policy system
- Infrastructure base classes

**Database Providers:** ✅ Four Complete Implementations
1. **SQLite** - Embedded/small applications
2. **SQL Server** - Enterprise Windows deployments
3. **PostgreSQL** - Enterprise cross-platform deployments
4. **MySQL** - Enterprise cross-platform deployments (compatible with MariaDB)

**Demo Application:** ✅ Complete
- Realistic asset management scenario
- 5 assets with part numbers and serial numbers
- 100-200 movements per asset
- Snapshot and archive demonstration

**Documentation:** ✅ Comprehensive
- API documentation
- Architecture diagrams
- Quick reference guide
- Provider-specific guides
- Migration checklists

This structure supports the library's growth while maintaining code quality and developer experience.



