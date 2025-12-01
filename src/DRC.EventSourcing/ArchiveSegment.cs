namespace DRC.EventSourcing;

public sealed record ArchiveSegment(
    GlobalPosition MinPosition,
    GlobalPosition MaxPosition,
    string FileName
);