using System.Text.Json;

namespace QuickBooksProbe;

internal static class PostgresLoaderTests
{
    internal static void Run(Action<string, bool> check, string published)
    {
        var parsed = PostgresLoader.LoadOptions.Parse(["--dry-run", "--snapshot", published, "--allow-unavailable", "Currency,Lead"]);
        check("postgres options preserve snapshot", parsed.Snapshot == published);
        check("postgres options parse unavailable policy", parsed.AllowedUnavailable.SetEquals(["Currency", "Lead"]));
        check("postgres options parse dry run", parsed.DryRun);

        bool duplicateRejected = false;
        try { PostgresLoader.LoadOptions.Parse(["--snapshot", published, "--snapshot", published]); }
        catch (ArgumentException) { duplicateRejected = true; }
        check("postgres options reject duplicates", duplicateRejected);

        bool implicitUnavailableRejected = false;
        try { SnapshotPlan.Read(published, new(StringComparer.Ordinal)); }
        catch (PostgresLoader.UnavailablePolicyException) { implicitUnavailableRejected = true; }
        check("postgres plan requires explicit unavailable policy", implicitUnavailableRejected);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(published, "manifest.json")));
        var allowed = manifest.RootElement.GetProperty("Entities").EnumerateArray()
            .Where(x => x.GetProperty("UnavailableStatusCode").ValueKind != JsonValueKind.Null)
            .Select(x => x.GetProperty("Entity").GetString()!).ToHashSet(StringComparer.Ordinal);
        var plan = SnapshotPlan.Read(published, allowed);
        check("postgres plan accepts exact unavailable policy", plan.Entities.Length == QueryCatalog.All.Length);
        check("postgres plan count matches rows", plan.Entities.Sum(x => x.Records ?? 0) == plan.RecordCount);
        check("postgres row stream preserves complete snapshot", plan.Entities.Sum(x => plan.ReadRows(x).LongCount()) == plan.RecordCount);
        if (Environment.GetEnvironmentVariable("QBD_POSTGRES_TEST_CONNECTION_STRING") is { Length: > 0 } testConnection)
            PostgresVersioningTests.Run(check, testConnection, plan).GetAwaiter().GetResult();

        allowed.Add("NotAnEntity");
        bool extraUnavailableRejected = false;
        try { SnapshotPlan.Read(published, allowed); }
        catch (PostgresLoader.UnavailablePolicyException ex) { extraUnavailableRejected = !ex.Unavailable.Contains("NotAnEntity"); }
        check("postgres plan rejects stale unavailable policy", extraUnavailableRejected);
    }
}
