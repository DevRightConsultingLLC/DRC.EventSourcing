namespace DRC.EventSourcing;

public sealed record Snapshot(
    string StreamId,
    StreamVersion StreamVersion,
    byte[] Data,
    DateTime CreatedUtc
);