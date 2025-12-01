Usage: In-memory SQLite store

The existing `SqliteConnectionFactory<TStore>` now supports keeping an in-memory SQLite database alive by detecting in-memory connection strings.

How to use:

- Configure your store options (TStore) with:
  - `ConnectionString = ":memory:"` or any connection string containing `mode=memory`.
  - `StoreName` should be set and unique per logical store (e.g., "Asset"). The factory uses StoreName to create a stable shared in-memory database name.

Example service registration in Startup/Program.cs:

```csharp
services.AddSqliteEventStore<MyStoreOptions>(opts =>
{
    opts.ConnectionString = ":memory:";
    opts.StoreName = "MyInMemoryStore";
    opts.ArchiveDirectory = Path.Combine("./archives", "MyInMemoryStore");
});
```

Notes:
- The factory keeps one connection open internally for the lifetime of the factory to ensure the in-memory DB remains alive.
- Connections returned by the factory share the same named in-memory database (via `Data Source=file:{StoreName}?mode=memory&cache=shared`).
- This approach works well for tests and ephemeral scenarios. For multi-process sharing, use a file-backed DB.

