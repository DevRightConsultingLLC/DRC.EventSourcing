﻿# DRC.EventSourcing

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)

## Overview

**DRC.EventSourcing** is a production-ready, enterprise-grade event sourcing library for .NET that provides a complete solution for storing, retrieving, and archiving domain events. Built with flexibility and performance in mind, it supports multiple database providers and offers sophisticated retention policies to balance storage costs with access requirements.

## 🎯 Intent & Purpose

### What is Event Sourcing?

Event sourcing is a pattern where application state is determined by a sequence of events rather than just the current state. Instead of updating records in-place, every state change is captured as an immutable event. This provides:

- **Complete Audit Trail**: Every change is recorded with full context
- **Temporal Queries**: Reconstruct state at any point in time  
- **Event Replay**: Rebuild aggregates or projections from events
- **Debugging**: Understand exactly how the system arrived at its current state

### Why DRC.EventSourcing?

Most event sourcing implementations focus on the "happy path" of storing and reading events. **DRC.EventSourcing** goes further by solving real-world production challenges:

1. **Storage Management**: As event stores grow, storage costs become significant. Our tiered storage system (hot/cold) lets you balance cost and performance.

2. **Multiple Backends**: Different projects have different constraints. We support SQLite, SQL Server, PostgreSQL, and MySQL so you can choose what fits your infrastructure.

3. **Retention Policies**: Not all events need the same treatment. Configure per-domain policies for archiving, backup, or deletion to meet compliance and cost requirements.

4. **Operational Excellence**: Built-in logging, metrics, connection management, and error handling make it production-ready out of the box.

## 🏗️ Solution Architecture

### Core Library (`DRC.EventSourcing`)

The foundation package containing all abstractions and shared infrastructure:

- **Interfaces**: `IEventStore`, `ISnapshotStore`, `IArchiveCoordinator`, `ICombinedEventFeed`
- **Value Objects**: `EventData`, `EventEnvelope`, `StreamVersion`, `GlobalPosition`
- **Policies**: Configurable retention modes (Default, FullHistory, ColdArchivable, HardDeletable)
- **Infrastructure**: Base implementations, logging, metrics, concurrency handling

### Database Providers

Each provider implements the core interfaces for a specific database:

- **`DRC.EventSourcing.Sqlite`**: SQLite implementation, perfect for development, testing, or embedded scenarios
- **`DRC.EventSourcing.SqlServer`**: SQL Server implementation for enterprise Windows environments
- **`DRC.EventSourcing.PostgreSQL`**: PostgreSQL implementation for cross-platform production deployments
- **`DRC.EventSourcing.MySql`**: MySQL/MariaDB implementation for existing MySQL infrastructure

All providers share the same API and support the same features, allowing seamless migration between databases.

### Storage Architecture

```
┌─────────────────────────────────────────────────────────┐
│                   Your Application                       │
└──────────────────┬──────────────────────────────────────┘
                   │
                   ↓
┌─────────────────────────────────────────────────────────┐
│              DRC.EventSourcing (Core)                    │
│  IEventStore │ ISnapshotStore │ IArchiveCoordinator     │
└──────────────────┬──────────────────────────────────────┘
                   │
       ┌───────────┴───────────┐
       ↓                       ↓
┌──────────────┐      ┌──────────────────┐
│ Hot Storage  │      │  Cold Storage    │
│  (Database)  │←────→│  (NDJSON Files)  │
│              │      │                  │
│ • Recent     │      │ • Old Events     │
│ • Fast       │      │ • Cheap          │
│ • Indexed    │      │ • Compressed     │
└──────────────┘      └──────────────────┘
```

## ✨ Key Features

### 📦 Multiple Storage Providers
Support for SQLite, SQL Server, PostgreSQL, and MySQL. All providers implement the same interfaces, allowing you to switch databases without changing application code.

### 🗄️ Tiered Storage (Hot/Cold)
- **Hot Storage**: Recent events in the database for fast access
- **Cold Storage**: Old events archived to efficient NDJSON files
- **Transparent Access**: `ICombinedEventFeed` reads from both seamlessly

### 🎚️ Flexible Retention Policies
Configure per-domain policies to control event lifecycle:

- **Default**: Keep all events in hot storage indefinitely
- **FullHistory**: Archive to cold storage while keeping hot copies (backup)
- **ColdArchivable**: Archive to cold storage and remove from hot (cost optimization)
- **HardDeletable**: Permanently delete without archiving (GDPR compliance)

### 📸 Snapshot Support
Save and restore aggregate snapshots to avoid replaying thousands of events. Snapshots are used to determine safe archive cutoff points.

### 🔒 Optimistic Concurrency Control
Built-in concurrency handling prevents lost updates using expected version checks. Concurrent writes are detected and rejected with `ConcurrencyException`.

### 📈 Incremental Archiving
Archive events in segments based on snapshot versions. Only archive events that are older than the oldest snapshot, ensuring safe event replay.

### 🔍 Global Ordering
Every event gets a unique `GlobalPosition` for total ordering across all streams, enabling cross-stream projections and catch-up subscriptions.

### 📊 Production-Ready
- Structured logging with `ILogger`
- Diagnostic metrics via `DiagnosticSource`
- Proper connection management
- Transaction handling
- Error mapping and recovery

## 🚀 Quick Start

### Installation

```bash
# Choose your database provider:

# SQLite (development/testing)
dotnet add package DRC.EventSourcing.Sqlite

# SQL Server (Windows/Azure)
dotnet add package DRC.EventSourcing.SqlServer

# PostgreSQL (cross-platform)
dotnet add package DRC.EventSourcing.PostgreSQL

# MySQL/MariaDB
dotnet add package DRC.EventSourcing.MySql
```

### Basic Usage

```csharp
using DRC.EventSourcing;
using DRC.EventSourcing.Sqlite;

// 1. Configure services
services.AddSqliteEventStore<MyStore>(options =>
{
    options.ConnectionString = "Data Source=events.db";
    options.StoreName = "MyStore";
    options.ArchiveDirectory = "./archive";
});

// 2. Initialize schema
await services.InitializeEventStoreSchemasAsync();

// 3. Get the event store
var eventStore = services.GetRequiredService<IEventStore>();

// 4. Append events to a stream
var events = new[]
{
    new EventData("OrderCreated", orderData, metadata),
    new EventData("ItemAdded", itemData, metadata)
};

var newVersion = await eventStore.AppendToStream(
    domain: "orders",
    streamId: "order-123",
    expectedVersion: StreamVersion.New(),
    events: events
);

// 5. Read events from a stream
await foreach (var envelope in eventStore.ReadStream("orders", "order-123"))
{
    Console.WriteLine($"{envelope.EventType} at v{envelope.StreamVersion.Value}");
    // Process the event...
}
```

## 📚 Documentation

- **[Quick Reference](docs/QUICK_REFERENCE.md)**: Common operations and code examples
- **[Solution Structure](docs/SOLUTION_STRUCTURE.md)**: Detailed architecture and project layout
- **[Archive Unification](docs/ARCHIVE_UNIFICATION_SUMMARY.md)**: How archiving and retention works
- **[Contributing](CONTRIBUTING.md)**: Guidelines for contributors
- **[Demo Application](samples/DRC.EventSourcing.Demo)**: Complete working example

## 🎓 Use Cases

### Asset Management System
Track physical assets moving through locations over time. Archive old movements to reduce database size while keeping recent activity fast.

### Order Processing
Store all order lifecycle events (created, items added, shipped, delivered). Use snapshots for order state, archive completed orders to cold storage.

### Audit Logging
Capture every user action with full context. Use `FullHistory` mode to maintain both fast database access and long-term cold archive.

### GDPR Compliance
Use `HardDeletable` retention policy to permanently remove user data on request, fulfilling "right to be forgotten" requirements.

### Multi-Tenant SaaS
Separate tenants by domain, configure retention policies per tenant, scale storage independently.

## 🔧 Configuration Examples

### Retention Policies

```csharp
services.AddSingleton<IDomainRetentionPolicyProvider>(sp =>
{
    var provider = new InMemoryRetentionPolicyProvider();
    
    // Critical business data: backup to cold storage but keep hot
    provider.AddOrReplace(new DomainRetentionPolicy
    {
        Domain = "orders",
        RetentionMode = RetentionMode.FullHistory
    });
    
    // High-volume logs: archive and delete from hot storage
    provider.AddOrReplace(new DomainRetentionPolicy
    {
        Domain = "logs",
        RetentionMode = RetentionMode.ColdArchivable
    });
    
    // Sensitive data: enable permanent deletion
    provider.AddOrReplace(new DomainRetentionPolicy
    {
        Domain = "user_requests",
        RetentionMode = RetentionMode.HardDeletable
    });
    
    return provider;
});
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
            await _archiveCoordinator.Archive(stoppingToken);
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
```

## 🏢 Production Considerations

### Database Sizing
- Hot storage: Size for recent events (e.g., last 30-90 days)
- Cold storage: Use cheap disk or blob storage for archives
- Plan for ~100-500 bytes per event depending on payload size

### Performance
- Append: 1-5ms for small batches (1-10 events)
- Read Stream: Optimized with indexes, sub-millisecond for small streams
- Read All: Streaming with configurable batch sizes
- Archive: Background process, minimal impact on hot path

### Monitoring
- Track `GlobalPosition` progress for projections
- Monitor archive segment creation
- Alert on concurrency exception rates
- Watch database growth trends

### Disaster Recovery
- Database backups cover hot storage
- Cold archives are immutable files (easy to backup/replicate)
- `FullHistory` mode provides additional safety net

## 🤝 Contributing

Contributions are welcome! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙋 Support

- **Issues**: Report bugs or request features via [GitHub Issues](https://github.com/yourusername/DRC.EventSourcing/issues)
- **Discussions**: Ask questions in [GitHub Discussions](https://github.com/yourusername/DRC.EventSourcing/discussions)
- **Documentation**: See the [docs](docs/) folder for detailed guides

---

**Built with ❤️ for production event sourcing scenarios**

## Directory Structure

```
DRC.EventSourcing/
├── src/                                    # Source code projects
│   ├── DRC.EventSourcing/                 # Core library (interfaces, abstractions)
│   ├── DRC.EventSourcing.Sqlite/          # SQLite implementation
│   └── DRC.EventSourcing.SqlServer/       # SQL Server implementation
│
├── test/                                   # Test projects
│   └── DRC.EventSourcing.Tests/           # Unit and integration tests
│
├── samples/                                # Sample/demo applications
│   └── DRC.EventSourcing.Demo/            # Demo console application
│
├── docs/                                   # Documentation
│   ├── ARCHIVE_UNIFICATION_SUMMARY.md     # Archive process documentation
│   ├── ARCHIVE_WORKFLOW_DIAGRAM.md        # Visual workflow diagrams
│   ├── QUICK_REFERENCE.md                 # Quick reference guide
│   ├── SOLUTION_STRUCTURE.md              # Structure documentation
│
├── LICENSE                                 # MIT License
├── README.md                               # This file
└── DRC.EventSourcing.sln                  # Solution file
```

## Retention Policies

| Mode | Archives Events? | Deletes Hot Events? | Deletes Stream Header? | Use Case |
|------|-----------------|---------------------|------------------------|----------|
| **Default** | ❌ No | ❌ No | ❌ No | Keep everything in hot storage |
| **FullHistory** | ✅ Yes | ❌ No | ❌ No | Archive for compliance, keep hot for speed |
| **ColdArchivable** | ✅ Yes | ✅ Yes | ❌ No | Save space, archive old events |
| **HardDeletable** | ❌ No | ✅ Yes | ✅ Yes | GDPR compliance, permanent deletion |

## Building the Solution

```bash
# Build everything
dotnet build

# Build only source projects
dotnet build src/

# Run tests
dotnet test

# Run the demo
dotnet run --project samples/DRC.EventSourcing.Demo
```

## Documentation

Comprehensive documentation is available in the `docs/` directory:

- **[Archive Unification Summary](docs/ARCHIVE_UNIFICATION_SUMMARY.md)** - Details on the unified archive workflow
- **[Archive Workflow Diagram](docs/ARCHIVE_WORKFLOW_DIAGRAM.md)** - Visual representation of archive processes
- **[Quick Reference](docs/QUICK_REFERENCE.md)** - Quick lookup guide
- **[Solution Structure](docs/SOLUTION_STRUCTURE.md)** - Detailed structure explanation
- **[Migration Checklist](docs/MIGRATION_CHECKLIST.md)** - Migration and verification steps

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Support

For issues, questions, or contributions, please open an issue on GitHub.

---

**Built with ❤️ using .NET 9.0**

