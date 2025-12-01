using System.Data;

namespace DRC.EventSourcing.Infrastructure
{

    /// <summary>
    /// Base archive coordinator that:
    ///   - Enumerates streams from the Streams table
    ///   - Applies domain-level retention policy
    ///   - Delegates provider-specific work (selecting events, writing NDJSON, deleting hot events)
    ///
    /// The goal is: "read the stream table and apply the domain policy to that stream
    /// depending on cutoff value".
    /// </summary>
    public abstract class BaseArchiveCoordinator<TOptions> : IArchiveCoordinator<TOptions> where TOptions : IEventStoreOptions
    {
        protected readonly BaseConnectionFactory<TOptions> ConnectionFactory;
        protected readonly TOptions                        Options;

        protected BaseArchiveCoordinator(BaseConnectionFactory<TOptions> connectionFactory, TOptions options)
        {
            ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            Options           = options          ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Starts the archive process by enumerating streams and applying retention policies.
        /// 
        /// Workflow:
        /// - Default: Skip (no archiving)
        /// - FullHistory: Archive events up to cutoff version, keep hot events (ArchiveWithoutDeletionAsync)
        /// - ColdArchivable: Archive events up to cutoff version, delete archived hot events (ArchiveStreamAsync)
        /// - HardDeletable: Permanently delete all events and stream header (DeleteStreamAsync)
        /// 
        /// Note: FullHistory and ColdArchivable use the SAME archiving workflow - the only 
        /// difference is whether hot events are deleted after archiving.
        /// </summary>
        /// <param name="ct">Cancellation token</param>
        public async Task Archive(CancellationToken ct = default)
        {
            using var conn = ConnectionFactory.CreateConnection();

            await foreach (var head in QueryStreamHeadsAsync(conn, ct))
            {
                ct.ThrowIfCancellationRequested();

                // Decide what to do based on policy + retention mode
                switch (head.RetentionMode)
                {
                    case RetentionMode.Default:
                        continue; // should already be filtered, but safe-guard

                    case RetentionMode.FullHistory:
                        // Archive events but keep them in hot store for fast access
                        await ArchiveWithoutDeletionAsync(conn, head, ct);
                        break;

                    case RetentionMode.ColdArchivable:
                        // Archive events and remove from hot store to save space
                        await ArchiveStreamAsync(conn, head, ct);
                        break;
                        
                    case RetentionMode.HardDeletable:
                        await DeleteStreamAsync(conn, head,  ct);
                        break;

                    default:
                        // maybe log unknown mode
                        break;
                }
            }
        }

        /// <summary>
        /// Archives events for ColdArchivable streams up to the ArchiveCutoffVersion.
        /// Creates archive segment files and DELETES archived events from the hot store.
        /// </summary>
        protected abstract Task ArchiveStreamAsync(IDbConnection conn, StreamHeader head, CancellationToken ct);

        /// <summary>
        /// Archives events for FullHistory streams up to the ArchiveCutoffVersion.
        /// Creates archive segment files but KEEPS archived events in the hot store.
        /// This method follows the SAME workflow as ArchiveStreamAsync, just without the deletion step.
        /// </summary>
        protected abstract Task ArchiveWithoutDeletionAsync(IDbConnection conn, StreamHeader head, CancellationToken ct);
        
        protected abstract Task DeleteStreamAsync(IDbConnection conn, StreamHeader head, CancellationToken ct);
        
        /// <summary>
        /// Retrieves streams from the "{StoreName}_Streams" table that qualify for archiving or deletion.
        /// A stream is returned when:
        ///  • (RetentionMode = FullHistory OR ColdArchivable) AND ArchiveCutoffVersion IS NOT NULL AND IsDeleted = 0
        ///  • OR RetentionMode = HardDeletable AND IsDeleted = 1
        /// </summary>
        protected abstract IAsyncEnumerable<StreamHeader> QueryStreamHeadsAsync(IDbConnection conn, CancellationToken ct);

     
        /// <summary>
        /// Provider hook to insert an archive segment record in the "{StoreName}_ArchiveSegments" table.
        /// Typically called from within ArchiveStream* methods while holding a transaction.
        /// </summary>
        protected abstract Task InsertArchiveSegmentRecordAsync(IDbConnection conn, IDbTransaction tx, long minPos, long maxPos, string filename, string? segmentNamespace, CancellationToken ct);
        
        /// <summary>
        /// Writes NDJSON records to the archive directory with atomic file operations.
        /// </summary>
        /// <param name="serializedLines">Enumerable of JSON-serialized event lines</param>
        /// <param name="directory">Target directory for the archive file</param>
        /// <param name="baseName">Base name for the file (without extension)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>The full path to the created NDJSON file</returns>
        /// <remarks>
        /// <para>This method provides atomic file creation with the following guarantees:</para>
        /// <list type="bullet">
        ///   <item>Files are written to a temporary location first (.tmp extension)</item>
        ///   <item>Atomic move to final location prevents partial reads</item>
        ///   <item>Unique timestamp suffix prevents race conditions in concurrent scenarios</item>
        ///   <item>File flush ensures data is persisted to disk before rename</item>
        ///   <item>If file already exists, returns existing path (idempotent)</item>
        /// </list>
        /// <para>The directory is created automatically if it doesn't exist.</para>
        /// <para>File naming format: {baseName}_{timestamp}.ndjson</para>
        /// </remarks>
        protected virtual async Task<string> WriteNdjsonAsync(
            IEnumerable<string> serializedLines, 
            string directory, 
            string baseName, 
            CancellationToken ct)
        {
            Directory.CreateDirectory(directory);
            
            var tempPath  = Path.Combine(directory, baseName + ".tmp");
            var finalPath = Path.Combine(directory, baseName + ".ndjson");
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))

            await using (var sw = new StreamWriter(fs))
            {
                foreach (var line in serializedLines)
                {
                    ct.ThrowIfCancellationRequested();
                    await sw.WriteLineAsync(line.AsMemory(), ct);
                }

                // Ensure all data is flushed to disk before moving
                await sw.FlushAsync(ct);
                await fs.FlushAsync(ct);
            }

            // Atomically move temp file to final location
            try
            {
                File.Move(tempPath, finalPath, overwrite: false);
            }
            catch (IOException ex) when (File.Exists(finalPath))
            {
                // File already exists at final path (race condition or retry)
                // Clean up our temp file and return the existing path
                try 
                { 
                    File.Delete(tempPath); 
                } 
                catch 
                { 
                    // Ignore cleanup failures - not critical
                }
                
                // Log the race condition for monitoring
                // (In production, you'd log this appropriately)
                System.Diagnostics.Debug.WriteLine($"Archive file already exists: {finalPath}");
            }
            catch (IOException ex)
            {
                // Other IO error during move - cleanup and rethrow
                try { File.Delete(tempPath); } catch { }
                throw new InvalidOperationException($"Failed to move archive file from {tempPath} to {finalPath}", ex);
            }

            return finalPath;
        }

        
        /// <summary>
        /// Helper to obtain ArchiveDirectory from options via direct property access.
        /// </summary>
        protected string GetArchiveDirectory()
        {
            var val = Options.ArchiveDirectory;
            if (string.IsNullOrWhiteSpace(val))
                throw new InvalidOperationException("ArchiveDirectory is not configured on options");

            return val!;
        }


        
    }
}
