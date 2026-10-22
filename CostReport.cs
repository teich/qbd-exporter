using System.Text.Json;

namespace QuickBooksProbe;

internal sealed record DatasetCost(string Entity, string Category, int? Records, int Calls, long? TotalMilliseconds,
    long? SdkMilliseconds, long? FirstReadSdkMilliseconds, long? SecondReadSdkMilliseconds,
    long? InitialPageSdkMilliseconds, long? ContinuationSdkMilliseconds);

internal static class CostReport
{
    internal static int Command(string[] args)
    {
        if (args.Length != 2 || args[0] != "--snapshot" || !Path.IsPathFullyQualified(args[1]))
        { Console.Error.WriteLine("Usage: QuickBooksProbe costs --snapshot <snapshot directory>"); return 2; }
        try
        {
            string directory = Snapshot.ValidateOutput(args[1]);
            string path = Path.Combine(directory, "manifest.json");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            using var manifest = JsonDocument.Parse(File.ReadAllText(path));
            var costs = Analyze(manifest.RootElement);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ExportMode = manifest.RootElement.GetProperty("ExportMode").GetString(),
                Note = "Manifest timing analysis only; no QuickBooks connection and no data-file verification. Entity totals cover the declared SDK reads, parsing, hashing, serialization and flush; exclude subsequent file hashing and snapshot verification. Null means timing unavailable.",
                Categories = costs.GroupBy(x => x.Category).Select(g => new { Category = g.Key, Datasets = g.Count(),
                    TimedDatasets = g.Count(x => x.TotalMilliseconds.HasValue), MeasuredMilliseconds = g.Sum(x => x.TotalMilliseconds ?? 0), Calls = g.Sum(x => x.Calls) }),
                Datasets = costs.OrderByDescending(x => x.TotalMilliseconds)
            }, Snapshot.JsonOptions));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { Console.WriteLine(JsonSerializer.Serialize(new { Success = false, Stage = "costs", ErrorType = ex.GetType().Name })); return 9; }
    }

    internal static DatasetCost[] Analyze(JsonElement root)
    {
        static long? Time(JsonElement value, string key) => value.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetInt64() : null;
        static long? Sum(JsonElement[] pages)
        {
            var times = pages.Select(p => Time(p, "QueryMilliseconds")).ToArray();
            return times.Any(t => !t.HasValue) ? null : times.Sum(t => t!.Value);
        }
        return root.GetProperty("Entities").EnumerateArray().Select(e =>
        {
            string name = e.GetProperty("Entity").GetString()!;
            var first = e.GetProperty("FirstRead").EnumerateArray().Where(p => !p.TryGetProperty("NoRequest", out var n) || !n.GetBoolean()).ToArray();
            var second = e.GetProperty("SecondRead").EnumerateArray().Where(p => !p.TryGetProperty("NoRequest", out var n) || !n.GetBoolean()).ToArray();
            var all = first.Concat(second).ToArray();
            bool report = name.Contains("Report-", StringComparison.Ordinal);
            // Reference fan-out requests each initialize independently; only iterator pages
            // have Remaining populated. Do not label later fan-out calls as continuations.
            var continuations = first.Skip(1).Concat(second.Skip(1)).Where(p => p.TryGetProperty("Remaining", out var r) && r.ValueKind != JsonValueKind.Null).ToArray();
            var initial = first.Take(1).Concat(second.Take(1)).ToArray();
            return new DatasetCost(name, report ? "reports" : "source-data-and-auxiliary", e.GetProperty("Records").ValueKind == JsonValueKind.Null ? null : e.GetProperty("Records").GetInt32(),
                all.Length, Time(e, "ReadAndWriteMilliseconds"), Sum(all), Sum(first), Sum(second), Sum(initial), Sum(continuations));
        }).ToArray();
    }
}
