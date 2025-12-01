namespace DRC.EventSourcing;

/// <summary>
/// Represents the metadata header for an event stream.
/// </summary>
/// <remarks>
/// The stream header contains key metadata about a stream including its current state,
/// version, position, and retention policy settings.
/// </remarks>
/// <param name="Domain">The domain this stream belongs to.</param>
/// <param name="StreamId">The unique identifier of the stream within the domain.</param>
/// <param name="LastVersion">The current version of the stream (number of events).</param>
/// <param name="LastPosition">The global position of the last event in this stream.</param>
/// <param name="RetentionMode">The retention mode for this stream.</param>
/// <param name="IsDeleted">Whether the stream has been marked as deleted.</param>
/// <param name="ArchivedAtUtc">The UTC timestamp when the stream was archived, or null if not archived.</param>
/// <param name="ArchiveCutoffVersion">The version up to which events have been archived, or null if no archival has occurred.</param>
public sealed record StreamHeader(
    string Domain,
    string StreamId,
    int LastVersion,
    long LastPosition,
    RetentionMode RetentionMode,
    bool IsDeleted,
    DateTime? ArchivedAtUtc,
    int? ArchiveCutoffVersion);

