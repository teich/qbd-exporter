using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace QuickBooksProbe;

internal static class Snapshot
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal const string Scope = "sdk-catalog-v1";

    internal static int Command(string[] args)
    {
        if (args.Length < 4 || args[0] != "--company" || args[2] != "--output")
        {
            Console.Error.WriteLine("Usage: QuickBooksProbe snapshot --company <absolute .qbw path> --output <absolute directory outside Git> [--test-days <1..3650>] [--page-size <1..5000>] [--max-minutes <1..1440>]");
            return 2;
        }
        string company = args[1];
        SnapshotOptions options;
        try { options = SnapshotOptions.Parse(args[4..]); }
        catch (ArgumentException)
        { Console.Error.WriteLine("Invalid snapshot option, duplicate option, or value outside the supported range."); return 2; }
        DateWindow? window = null;
        if (options.TestDays is int days)
        {
            var now = DateTime.Now;
            window = new(now.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        }
        if (!Path.IsPathFullyQualified(company) || !File.Exists(company) ||
            !string.Equals(Path.GetExtension(company), ".qbw", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("An existing absolute .qbw path is required.");
            return 2;
        }
        string output;
        try { output = ValidateOutput(args[3]); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine("Output must be a local fixed-drive directory outside Git, without reparse points.");
            return 2;
        }
        using var mutex = new Mutex(false, @"Local\QuickBooksProbe.ReadOnlyProof");
        bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { Console.Error.WriteLine("Another QuickBooks run is active."); return 6; }
        try
        {
            using var cancellation = new CancellationTokenSource();
            if (options.MaxMinutes is int minutes) cancellation.CancelAfter(TimeSpan.FromMinutes(minutes));
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += handler;
            RunSummary result;
            try { result = Run(() => new ComProcessor(), Path.GetFullPath(company), output, window: window, cancellationToken: cancellation.Token, pageSize: options.PageSize); }
            finally { Console.CancelKeyPress -= handler; }
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return result.ExitCode;
        }
        finally { mutex.ReleaseMutex(); }
    }

    internal static string ValidateOutput(string value)
    {
        if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException();
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed) throw new ArgumentException();
        for (DirectoryInfo? d = new(path); d != null; d = d.Parent)
        {
            if (File.Exists(d.FullName) || File.Exists(Path.Combine(d.FullName, ".git")) || Directory.Exists(Path.Combine(d.FullName, ".git")))
                throw new ArgumentException();
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException();
        }
        return path;
    }

    internal static RunSummary Run(Func<IProcessor> factory, string company, string output, bool full = true, DateWindow? window = null, CancellationToken cancellationToken = default, int pageSize = FullExport.PageSize)
    {
        var result = new RunSummary { Stage = "snapshot-storage" };
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            FullExport.ValidatePageSize(pageSize);
            output = ValidateOutput(output);
            if (!Directory.Exists(output)) CreatePrivateDirectory(output);
            // Shared output lock also prevents overlapping writers in different Windows sessions.
            // Use OpenOrCreate: a stale file does not block a run once its process has exited.
            string lockPath = Path.Combine(output, ".export.lock");
            if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException();
            using var outputLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string staging = Path.Combine(output, ".staging-" + id);
            string final = Path.Combine(output, id);
            CreatePrivateDirectory(staging);
            var files = new List<SnapshotFile>();
            List<EntityEvidence>? entities = null;
            result = Proof.Run(() => { cancellationToken.ThrowIfCancellationRequested(); return new CancellationProcessor(factory(), cancellationToken); }, company, (processor, ticket, summary) =>
            {
                summary.ExportMode = window == null ? "full" : "date-window-test";
                if (full)
                {
                    entities = FullExport.Extract(processor, ticket, summary, staging, files, window: window, pageSize: pageSize);
                    return;
                }
                var first = ReadPass(processor, ticket, summary, 1);
                summary.Stage = "snapshot-storage";
                files.Add(WriteFile(staging, "company.xml", first.Company, 1));
                files.Add(WriteFile(staging, "accounts.xml", first.Accounts, first.Count));
            }, newest: full);
            // Failure leaves a private staging directory, never a consumable snapshot.
            if (!result.Success) return result;
            result.Stage = "snapshot-publish";
            var manifest = new
            {
                FormatVersion = full ? 2 : 1, SnapshotId = id, Scope = full ? Scope : "company-and-accounts", StartedUtc = started, CompletedUtc = DateTimeOffset.UtcNow,
                CatalogVersion = full ? QueryCatalog.Version : 0,
                ExportProfile = full ? "source-data-no-reports" : "company-and-accounts",
                ExcludedQueryKinds = full ? QueryCatalog.Version12.Where(x => x.IsReport).Select(x => x.Kind).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray() : Array.Empty<string>(),
                ExportMode = window == null ? "full" : "date-window-test",
                QbXmlVersion = result.QbXmlVersion, Transport = "QBXMLRP2.RequestProcessor",
                RequestProcessorVersion = result.RequestProcessorVersion,
                ExporterVersion = typeof(Snapshot).Assembly.GetName().Version?.ToString(), RuntimeVersion = Environment.Version.ToString(),
                CompanyPathMatched = result.CompanyPathMatched,
                QueryScope = new { ActiveStatus = "All where supported", DateFilter = window, DateSemantics = "Inclusive QuickBooks local modified timestamps for ordinary queries; inclusive calendar report dates for reports; entities without a date filter are read in full", IncludeRetElement = (string?)null,
                    PublicCustomFieldOwner = full ? "0" : null, IncludesLinesAndLinkedTransactionsWhereSupported = full },
                Completeness = new { Method = full ? "single-read-with-entity-specific-count-validation" : "single-read-company-account-proof", ReadPasses = 1,
                    ResponseCharacterLimit = FullExport.QueryLimit(pageSize), ReportResponseCharacterLimit = FullExport.ReportCharacterLimit, PageSize = pageSize, Pagination = full ? $"iterators where supported; {pageSize} records per page" : "none", CoversAllEntityTypes = false },
                Consistency = full
                    ? "One read per entity with iterator counts, record identities, and file integrity checked. No transaction isolation; records may change during the export, so this is not a point-in-time backup. Only SDK-accessible fields are captured. Other applications' private DataExt owners are not queried."
                    : "One read of company and account data with response counts and file integrity checked. No transaction isolation; records may change during the read, so this is not a point-in-time backup.",
                Entities = entities,
                HasUnavailableEntities = entities?.Any(x => x.UnavailableStatusCode != null) ?? false,
                DeletionHistory = full ? new { ListDeletedRetention = "SDK retains only approximately the preceding three months", TxnDeletedScope = "All records returned by the SDK with no date restriction", IncludedDeletedListTypes = QuerySpec.DeletedListTypes, IncludedDeletedTransactionTypes = QuerySpec.DeletedTransactionTypes } : null,
                ListCoverage = full ? ListCoverageAudit.Analyze(staging) : null,
                TransactionCoverage = full ? TransactionAudit.Analyze(staging) : null,
                TransactionIndexLimitations = "Generic TransactionQuery excludes TimeTracking and only searches transaction types permitted to the current QuickBooks user, including sensitive-data and payroll-subscription restrictions. Zero coverage gaps are not proof of unrestricted company-wide access.",
                SessionEnded = result.SessionEnded, ConnectionClosed = result.ConnectionClosed,
                Files = files
            };
            WriteDurable(Path.Combine(staging, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
            result.Stage = "snapshot-verify";
            if (full) SnapshotVerifier.Verify(staging);
            result.Stage = "snapshot-publish";
            // Sibling rename publishes only after all writes and SDK cleanup succeed.
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, final);
            result.SnapshotId = id;
            result.Stage = "complete";
        }
        catch (Exception ex)
        {
            result.ExitCode = ex is OperationCanceledException ? 10 : 8;
            result.ErrorType = ex.GetType().Name;
            result.HResult = $"0x{ex.HResult:X8}";
        }
        return result;
    }

    private static (byte[] Company, byte[] Accounts, int Count) ReadPass(IProcessor rp, string ticket, RunSummary summary, int pass)
    {
        summary.Stage = "CompanyQuery";
        string companyId = $"company-{pass}";
        var company = Proof.ReadResponse(rp.Query(ticket, Request("Company", companyId)), "Company", companyId);
        summary.Stage = "AccountQuery";
        string accountId = $"accounts-{pass}";
        var accounts = Proof.ReadResponse(rp.Query(ticket, Request("Account", accountId)), "Account", accountId, int.MaxValue);
        int count = accounts.Elements().Count();
        if (!int.TryParse((string?)accounts.Attribute("retCount"), NumberStyles.None, CultureInfo.InvariantCulture, out int reported) || reported != count ||
            accounts.Attribute("iteratorID") != null || accounts.Attribute("iteratorRemainingCount") != null)
            throw new InvalidDataException();
        if (pass == 1)
        {
            summary.Queries.Add(Proof.Summarize(company, "Company"));
            summary.Queries.Add(Proof.Summarize(accounts, "Account"));
        }
        return (SerializeRecords("Company", company.Elements()),
            SerializeRecords("Account", accounts.Elements().OrderBy(x => (string?)x.Element("ListID"), StringComparer.Ordinal)), count);
    }

    internal static string Request(string kind, string id)
    {
        var query = new XElement(kind + "QueryRq", new XAttribute("requestID", id));
        if (kind == "Account")
        {
            query.Add(new XAttribute("metaData", "MetaDataAndResponseData"));
            query.Add(new XElement("ActiveStatus", "All"));
        }
        return "<?xml version=\"1.0\"?><?qbxml version=\"13.0\"?>" +
            new XElement("QBXML", new XElement("QBXMLMsgsRq", new XAttribute("onError", "stopOnError"), query)).ToString(SaveOptions.DisableFormatting);
    }

    private static byte[] SerializeRecords(string kind, IEnumerable<XElement> records) =>
        Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            new XElement(kind + "Records", records.Select(x => new XElement(x))).ToString(SaveOptions.DisableFormatting) + "\n");

    private static SnapshotFile WriteFile(string directory, string name, byte[] bytes, int count)
    {
        string path = Path.Combine(directory, name);
        WriteDurable(path, bytes);
        using var stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (stream.Length != bytes.Length || hash != Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()) throw new IOException();
        return new SnapshotFile(name, count, bytes.Length, hash);
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void CreatePrivateDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException();
        security.SetOwner(user);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }
}

internal sealed record SnapshotFile(string Name, int Records, long Bytes, string Sha256);
