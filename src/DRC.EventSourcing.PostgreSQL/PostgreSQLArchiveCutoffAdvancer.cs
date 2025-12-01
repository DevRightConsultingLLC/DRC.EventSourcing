using Dapper;
using DRC.EventSourcing.Infrastructure;
using System.Data;

namespace DRC.EventSourcing.PostgreSQL;

public sealed class PostgreSQLArchiveCutoffAdvancer<TStore> : BaseArchiveCutoffAdvancer<TStore> 
    where TStore : PostgreSQLEventStoreOptions
{
    private readonly TStore _options;

    public PostgreSQLArchiveCutoffAdvancer(PostgreSQLConnectionFactory<TStore> factory, TStore options) 
        : base(factory, options)
    {
        _options = options;
    }

    public override async Task<bool> TryAdvanceArchiveCutoff(
        string domain, 
        string streamId, 
        int newCutoffVersion, 
        CancellationToken ct = default)
    {
        using var conn = ConnectionFactory.CreateConnection();
        if (conn.State == ConnectionState.Closed) 
            conn.Open();

        var cmd = new CommandDefinition(
            $@"UPDATE {((IEventStoreOptions)_options).StreamsTableName}
               SET ArchiveCutoffVersion = @NewCutoff
               WHERE domain = @Domain 
                 AND stream_id = @StreamId 
                 AND (ArchiveCutoffVersion IS NULL OR ArchiveCutoffVersion < @NewCutoff)",
            new { NewCutoff = newCutoffVersion, Domain = domain, StreamId = streamId }, 
            cancellationToken: ct);

        var rowsAffected = await conn.ExecuteAsync(cmd);
        return rowsAffected > 0;
    }
}

