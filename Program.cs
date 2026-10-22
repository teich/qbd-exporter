using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace QuickBooksProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--self-test" })) return SelfTests.Run();
        if (args.SequenceEqual(new[] { "--catalog" })) return CatalogValidation.Run();
        if (args.Length > 0 && args[0] == "snapshot") return Snapshot.Command(args[1..]);
        if (args.Length > 0 && args[0] == "verify") return SnapshotVerifier.Command(args[1..]);
        if (args.Length > 0 && args[0] == "postgres") return PostgresLoader.Command(args[1..]).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == "benchmark") return BatchBenchmark.Command(args[1..]);
        if (args.Length > 0 && args[0] == "costs") return CostReport.Command(args[1..]);
        if (args.SequenceEqual(new[] { "--help" }))
        {
            Console.WriteLine("""
                QuickBooksProbe --company <absolute .qbw path>
                QuickBooksProbe snapshot --company <absolute .qbw path> --output <absolute private directory outside Git> [--test-days 1..3650] [--page-size 1..5000] [--max-minutes 1..1440]
                QuickBooksProbe verify --snapshot <absolute published snapshot directory>
                QuickBooksProbe postgres probe
                QuickBooksProbe postgres init
                QuickBooksProbe postgres status
                QuickBooksProbe postgres load --snapshot <absolute published snapshot directory> [--allow-unavailable <comma-separated entities>] [--dry-run]
                QuickBooksProbe benchmark --company <absolute .qbw path> --entity <iterator dataset> [--max-minutes 1..1440]
                QuickBooksProbe costs --snapshot <snapshot directory>
                QuickBooksProbe --self-test | --catalog | --help

                Without snapshot: read-only five-account proof using one company and one account read.
                Snapshot: full source-data export, excluding all report queries; private XML, JSONL and manifest.
                Each dataset is read once; iterator counts, record identities and file integrity are validated.
                No transaction isolation is provided, so a snapshot is not a point-in-time backup.
                --test-days produces a filtered integration test, not a full snapshot.
                --page-size sets iterator batch size (default 5000; maximum 5000).
                --max-minutes requests cooperative cancellation after the specified runtime.
                An in-flight COM call must return before cancellation and cleanup can finish.
                Verify checks an existing snapshot without connecting to QuickBooks.
                Postgres commands read QBD_POSTGRES_CONNECTION_STRING and optional QBD_POSTGRES_SCHEMA.
                Postgres load accepts only verified full snapshots and requires an exact unavailable-entity allowlist.
                --dry-run exercises all database writes and count checks, then rolls the transaction back.
                Benchmark compares full reads at 100, 1000 and 5000 records per call (default limit 20 minutes).
                It checks identical record content, outputs only timings/counts, and writes no snapshot.
                Costs ranks recorded dataset timings without opening QuickBooks or reading record values.
                Ctrl+C during snapshot requests cancellation after the active COM call returns;
                cleanup runs and the cancelled snapshot is not published (exit code 10).
                """);
            return 0;
        }
        string? company = args.Length == 0 ? Environment.GetEnvironmentVariable("QB_COMPANY_FILE") :
            args.Length == 2 && args[0] == "--company" ? args[1] : null;
        if (string.IsNullOrWhiteSpace(company) || !Path.IsPathFullyQualified(company) ||
            !string.Equals(Path.GetExtension(company), ".qbw", StringComparison.OrdinalIgnoreCase) || !File.Exists(company))
        {
            Console.Error.WriteLine("Provide an existing absolute .qbw path using --company or QB_COMPANY_FILE.");
            return 2;
        }
        using var mutex = new Mutex(false, @"Local\QuickBooksProbe.ReadOnlyProof");
        bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { Console.Error.WriteLine("Another proof run is active."); return 6; }
        try
        {
            var result = Proof.Run(() => new ComProcessor(), Path.GetFullPath(company));
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return result.ExitCode;
        }
        finally { mutex.ReleaseMutex(); }
    }
}

internal interface IProcessor : IDisposable
{
    string? ProcessorVersion => null;
    void Open();
    void SetReadOnly();
    string Begin(string company);
    string CurrentCompany(string ticket);
    string[] Versions(string ticket);
    string Query(string ticket, string xml);
    void End(string ticket);
    void Close();
}

[ComImport, Guid("4277578A-9687-4EBC-8586-1CA4759B17C3"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IAuthPreferences
{
    [DispId(3)] void PutIsReadOnly([MarshalAs(UnmanagedType.VariantBool)] bool readOnly);
}

internal sealed class ComProcessor : IProcessor
{
    public string? ProcessorVersion => $"{rp.MajorVersion}.{rp.MinorVersion}.{rp.ReleaseNumber}";
    private readonly dynamic rp = Activator.CreateInstance(Type.GetTypeFromProgID("QBXMLRP2.RequestProcessor", true)!)!;
    private object? preferences;
    // QuickBooks keys Integrated Applications authorization by this name; changing it requires re-authorization.
    internal const string ApplicationName = "qbd-exporter (read-only)";
    public void Open() => rp.OpenConnection2("", ApplicationName, 1);
    public void SetReadOnly()
    {
        preferences = rp.AuthPreferences;
        ((IAuthPreferences)preferences).PutIsReadOnly(true);
    }
    public string Begin(string company) => rp.BeginSession(company, 2);
    public string CurrentCompany(string ticket) => rp.GetCurrentCompanyFileName(ticket);
    public string[] Versions(string ticket) => ((Array)rp.QBXMLVersionsForSession[ticket]).Cast<string>().ToArray();
    public string Query(string ticket, string xml) => rp.ProcessRequest(ticket, xml);
    public void End(string ticket) => rp.EndSession(ticket);
    public void Close() => rp.CloseConnection();
    public void Dispose()
    {
        // AuthPreferences may share COM identity with the processor. Release it only once in that case.
        try { if (preferences != null && !ReferenceEquals(preferences, (object)rp)) Marshal.FinalReleaseComObject(preferences); }
        finally { Marshal.FinalReleaseComObject((object)rp); }
    }
}

internal sealed record QuerySummary(string Response, int StatusCode, string Severity, int Count, string[] ElementPaths);
internal sealed class RunSummary
{
    public int ExitCode { get; set; }
    public bool Success => ExitCode == 0;
    public string Stage { get; set; } = "activation";
    public string? ErrorType { get; set; }
    public string? HResult { get; set; }
    public int? RejectedStatusCode { get; set; }
    public bool CompanyPathMatched { get; set; }
    public string? QbXmlVersion { get; set; }
    public List<QuerySummary> Queries { get; } = new();
    public List<string> CleanupErrors { get; } = new();
    public bool SessionEnded { get; set; }
    public bool ConnectionClosed { get; set; }
    public string? SnapshotId { get; set; }
    public string? RequestProcessorVersion { get; set; }
    public object? LastPage { get; set; }
    public string? ValidationFailure { get; set; }
    public string? ExportMode { get; set; }
}

internal static class Proof
{
    public static RunSummary Run(Func<IProcessor> factory, string company, Action<IProcessor, string, RunSummary>? extract = null, bool newest = false)
    {
        var result = new RunSummary();
        IProcessor? rp = null;
        bool open = false;
        string? ticket = null;
        try
        {
            rp = factory();
            result.Stage = "open-connection";
            rp.Open(); open = true;
            result.Stage = "read-only-preferences";
            rp.SetReadOnly();
            result.Stage = "begin-session";
            ticket = rp.Begin(company);
            if (string.IsNullOrEmpty(ticket)) throw new InvalidDataException();
            result.Stage = "verify-company";
            if (!string.Equals(Path.GetFullPath(rp.CurrentCompany(ticket)), Path.GetFullPath(company), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
            result.CompanyPathMatched = true;
            result.Stage = "negotiate-version";
            // A fixed, deliberately small request schema; require explicit session support.
            var versions = rp.Versions(ticket);
            if (!versions.Contains("13.0")) throw new InvalidDataException();
            result.QbXmlVersion = newest ? versions.Where(v => Version.TryParse(v, out var version) && version >= new Version(13, 0) && version <= new Version(17, 0))
                .OrderByDescending(v => Version.Parse(v)).First() : "13.0";
            result.RequestProcessorVersion = rp.ProcessorVersion;
            if (extract != null) extract(rp, ticket, result);
            else foreach (string kind in new[] { "Company", "Account" })
            {
                result.Stage = kind + "Query";
                var body = kind == "Company" ? "<CompanyQueryRq requestID=\"1\"/>" :
                    "<AccountQueryRq requestID=\"2\"><MaxReturned>5</MaxReturned><ActiveStatus>All</ActiveStatus></AccountQueryRq>";
                string request = "<?xml version=\"1.0\"?><?qbxml version=\"13.0\"?><QBXML><QBXMLMsgsRq onError=\"stopOnError\">" + body + "</QBXMLMsgsRq></QBXML>";
                result.Queries.Add(Validate(rp.Query(ticket, request), kind, kind == "Company" ? "1" : "2"));
            }
            result.Stage = "complete";
        }
        catch (Exception ex)
        {
            result.ExitCode = ex is OperationCanceledException ? 10 : result.Stage == "snapshot-storage" ? 8 : result.Stage == "verify-company" ? 4 :
                result.Stage.EndsWith("Query", StringComparison.Ordinal) && ex is InvalidDataException or ResponseStatusException or XmlException ? 5 : 3;
            result.ErrorType = ex.GetType().Name;
            result.HResult = $"0x{ex.HResult:X8}";
            if (ex is ResponseStatusException status) result.RejectedStatusCode = status.StatusCode;
            // Never log exception messages: SDK messages can contain paths or record values.
        }
        finally
        {
            void Clean(string name, Action action)
            {
                try { action(); }
                catch (Exception ex) { result.CleanupErrors.Add(name + ":" + ex.GetType().Name + $":0x{ex.HResult:X8}"); }
            }
            if (rp != null)
            {
                if (!string.IsNullOrEmpty(ticket)) Clean("EndSession", () => { rp.End(ticket); result.SessionEnded = true; });
                if (open) Clean("CloseConnection", () => { rp.Close(); result.ConnectionClosed = true; });
                Clean("ReleaseCOM", rp.Dispose);
            }
            if (result.CleanupErrors.Count > 0 && result.ExitCode == 0) result.ExitCode = 7;
        }
        return result;
    }

    internal static QuerySummary Validate(string xml, string kind, string id)
        => Summarize(ReadResponse(xml, kind, id), kind);

    internal static XElement ReadResponse(string xml, string kind, string id, int maxAccounts = 5)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
        var doc = XDocument.Load(reader);
        if (doc.Root?.Name != "QBXML" || doc.Root.Elements().Count() != 1) throw new InvalidDataException();
        var messages = doc.Root.Element("QBXMLMsgsRs");
        if (messages == null || messages.Elements().Count() != 1) throw new InvalidDataException();
        var rs = messages.Elements().Single();
        if (rs.Name != kind + "QueryRs" || (string?)rs.Attribute("requestID") != id ||
            !int.TryParse((string?)rs.Attribute("statusCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ||
            rs.Attribute("statusMessage") == null) throw new InvalidDataException();
        var severity = (string?)rs.Attribute("statusSeverity");
        if (code != 0 || severity != "Info") throw new ResponseStatusException(code);
        var records = rs.Elements(kind + "Ret").ToArray();
        if (rs.Elements().Count() != records.Length || (kind == "Company" ? records.Length != 1 : records.Length > maxAccounts))
            throw new InvalidDataException();
        if (kind == "Account" && (records.Any(x => x.Elements("ListID").Count() != 1 || string.IsNullOrWhiteSpace((string?)x.Element("ListID"))) ||
            records.Select(x => (string?)x.Element("ListID")).Distinct().Count() != records.Length)) throw new InvalidDataException();
        return rs;
    }

    internal static QuerySummary Summarize(XElement rs, string kind)
    {
        var records = rs.Elements(kind + "Ret").ToArray();
        // Only XML element names, never text, attributes, or identifiers leave this method.
        var paths = records.SelectMany(r => r.DescendantsAndSelf()).Select(e =>
            string.Join("/", e.AncestorsAndSelf().TakeWhile(a => a != rs).Reverse().Select(a => a.Name.LocalName)))
            .Distinct().Order(StringComparer.Ordinal).ToArray();
        return new QuerySummary(kind + "QueryRs", 0, "Info", records.Length, paths);
    }
}

internal sealed class ResponseStatusException(int code) : IOException
{
    public int StatusCode { get; } = code;
}
