using System.Diagnostics;
using System.Text.Json;

namespace QuickBooksProbe;

internal sealed record BatchMeasurement(int PageSize, int Records, int Calls, long InitialQueryMilliseconds,
    long ContinuationQueryMilliseconds, long TotalMilliseconds, bool MatchesFirstRead, int? LargestResponseCharacters);

internal static class BatchBenchmark
{
    internal static int Command(string[] args)
    {
        if (args.Length is not (4 or 6) || args[0] != "--company" || args[2] != "--entity")
        { Console.Error.WriteLine("Usage: QuickBooksProbe benchmark --company <absolute .qbw path> --entity <iterator dataset> [--max-minutes <1..1440>]"); return 2; }
        var spec = QueryCatalog.All.SingleOrDefault(s => s.Name == args[3] && s.Iterator);
        int minutes = 20;
        if (spec == null || !Path.IsPathFullyQualified(args[1]) || !File.Exists(args[1]) ||
            !string.Equals(Path.GetExtension(args[1]), ".qbw", StringComparison.OrdinalIgnoreCase) ||
            (args.Length == 6 && (args[4] != "--max-minutes" || !int.TryParse(args[5], out minutes) || minutes is < 1 or > 1440)))
        { Console.Error.WriteLine("Provide an existing company, an iterator dataset, and a valid runtime limit."); return 2; }
        using var mutex = new Mutex(false, @"Local\QuickBooksProbe.ReadOnlyProof");
        bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { Console.Error.WriteLine("Another QuickBooks run is active."); return 6; }
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += handler;
            var measurements = new List<BatchMeasurement>();
            RunSummary result;
            try
            {
                result = Proof.Run(() => new CancellationProcessor(new ComProcessor(), cancellation.Token), Path.GetFullPath(args[1]),
                    (rp, ticket, summary) => measurements.AddRange(Measure(rp, ticket, summary, spec)), newest: true);
            }
            finally { Console.CancelKeyPress -= handler; }
            Console.WriteLine(JsonSerializer.Serialize(new { Result = result, Entity = spec.Name, Measurements = measurements,
                Note = "Sequential full reads; cache/order effects remain possible. No snapshot or record values are written. Runtime limit is cooperative." }, Snapshot.JsonOptions));
            return result.ExitCode;
        }
        finally { mutex.ReleaseMutex(); }
    }

    internal static IEnumerable<BatchMeasurement> Measure(IProcessor rp, string ticket, RunSummary summary, QuerySpec spec)
    {
        if (!spec.Iterator) throw new ArgumentException();
        string? firstDigest = null;
        int? firstCount = null;
        int pass = 0;
        foreach (int pageSize in new[] { 100, 1000, 5000 })
        {
            Console.Error.WriteLine($"Benchmark {spec.Name}: full read with {pageSize} records per call...");
            var timer = Stopwatch.StartNew();
            var read = FullExport.Read(rp, ticket, summary, spec, ++pass, _ => { }, pageSize: pageSize);
            timer.Stop();
            if (read.Digest.StartsWith("unavailable:", StringComparison.Ordinal)) throw new InvalidDataException();
            firstDigest ??= read.Digest;
            firstCount ??= read.Count;
            bool matches = firstDigest == read.Digest && firstCount == read.Count;
            yield return new(pageSize, read.Count, read.Pages.Count, read.Pages[0].QueryMilliseconds ?? 0,
                read.Pages.Skip(1).Sum(p => p.QueryMilliseconds ?? 0), timer.ElapsedMilliseconds, matches, read.Pages.Max(p => p.ResponseCharacters));
            if (!matches)
            {
                summary.ValidationFailure = "benchmark-content-mismatch";
                throw new InvalidDataException();
            }
        }
    }
}
