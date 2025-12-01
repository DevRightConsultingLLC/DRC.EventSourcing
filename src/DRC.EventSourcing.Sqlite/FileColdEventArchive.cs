using System.Text;
using System.Text.Json;

namespace DRC.EventSourcing.Sqlite;

public sealed class FileColdEventArchive : IColdEventArchive
{
    private readonly string _rootDirectory;
    private readonly JsonSerializerOptions _jsonOptions;

    public FileColdEventArchive(string rootDirectory)
    {
        _rootDirectory = rootDirectory;
        Directory.CreateDirectory(_rootDirectory);
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<(GlobalPosition Min, GlobalPosition Max)?> GetRange(CancellationToken ct = default)
    {
        var files = Directory
            .EnumerateFiles(_rootDirectory, "events-*-*.ndjson")
            .ToList();

        if (files.Count == 0)
            return null;

        long? min = null;
        long? max = null;

        foreach (var path in files)
        {
            var name = Path.GetFileNameWithoutExtension(path); // events-000..-000..
            var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 3) continue;

            if (!long.TryParse(parts[1], out var fMin)) continue;
            if (!long.TryParse(parts[2], out var fMax)) continue;

            if (min is null || fMin < min) min = fMin;
            if (max is null || fMax > max) max = fMax;
        }

        if (min is null || max is null)
            return null;

        return await Task.FromResult((new GlobalPosition(min.Value), new GlobalPosition(max.Value)));
    }

    public async IAsyncEnumerable<EventEnvelope> ReadAllForwards(
        GlobalPosition? fromExclusive = null,
        int batchSize = 512,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var from = fromExclusive?.Value ?? 0L;

        // Step 1: Get all archive files matching the pattern
        var allArchiveFiles = Directory
            .EnumerateFiles(_rootDirectory, "events-*-*.ndjson")
            .ToList();

        // Step 2: Parse each file name to extract min/max position ranges
        var parsedFiles = new List<(string Path, long? Min, long? Max)>();
        foreach (var filePath in allArchiveFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath); // e.g., "events-0000000000000001-0000000000000010"
            var parts = fileName.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            
            // Expected format: ["events", "minPosition", "maxPosition"]
            if (parts.Length != 3)
            {
                parsedFiles.Add((filePath, null, null));
                continue;
            }

            var minParsed = long.TryParse(parts[1], out var minPosition) ? (long?)minPosition : null;
            var maxParsed = long.TryParse(parts[2], out var maxPosition) ? (long?)maxPosition : null;
            
            parsedFiles.Add((filePath, minParsed, maxParsed));
        }

        // Step 3: Filter to only valid files that have events after our starting position
        var relevantFiles = parsedFiles
            .Where(f => f.Min.HasValue && f.Max.HasValue && f.Max.Value > from)
            .ToList();

        // Step 4: Sort files by their minimum position to read in order
        var files = relevantFiles
            .OrderBy(f => f.Min!.Value)
            .ToList();

        foreach (var f in files)
        {
            // stream file line by line
            await using var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(fs, Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                ct.ThrowIfCancellationRequested();

                var dto = JsonSerializer.Deserialize<ArchivedEventJson>(line, _jsonOptions);
                if (dto is null) continue;

                if (dto.GlobalPosition <= from)
                    continue;

                var data = dto.Data is null ? Array.Empty<byte>() : Convert.FromBase64String(dto.Data);
                byte[]? meta = dto.Metadata is null ? null : Convert.FromBase64String(dto.Metadata);

                var env = new EventEnvelope(
                    dto.StreamId,
                    new StreamVersion(dto.StreamVersion),
                    new GlobalPosition(dto.GlobalPosition),
                    dto.EventType,
                    data,
                    meta,
                    DateTime.Parse(dto.CreatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind)
                );

                yield return env;
            }
        }
    }

    private sealed class ArchivedEventJson
    {
        public long GlobalPosition { get; set; }
        public string StreamId { get; set; } = default!;
        public int StreamVersion { get; set; }
        public string EventType { get; set; } = default!;
        public string CreatedUtc { get; set; } = default!;
        public string? Data { get; set; }
        public string? Metadata { get; set; }
    }
}