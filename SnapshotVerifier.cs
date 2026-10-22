using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace QuickBooksProbe;

// Does not contact QuickBooks. Verifies version-2 snapshots before publication and
// can be used by the future Postgres loader as its input gate.
internal static class SnapshotVerifier
{
    internal static int Command(string[] args)
    {
        if (args.Length != 2 || args[0] != "--snapshot" || !Path.IsPathFullyQualified(args[1]))
        { Console.Error.WriteLine("Usage: QuickBooksProbe verify --snapshot <published snapshot directory>"); return 2; }
        try
        {
            string mode = Verify(args[1], published: true);
            Console.WriteLine(JsonSerializer.Serialize(new { Success = true, Stage = "verified", ExportMode = mode }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Success = false, Stage = "verify", ErrorType = ex.GetType().Name }));
            return 9;
        }
    }

    internal static string Verify(string directory, bool published = false)
    {
        directory = Snapshot.ValidateOutput(directory);
        using var manifest = JsonDocument.Parse(File.ReadAllText(SafeFile(directory, "manifest.json")));
        var root = manifest.RootElement;
        var completeness = root.GetProperty("Completeness");
        int pageSize = completeness.TryGetProperty("PageSize", out var ps) ? ps.GetInt32() : FullExport.PageSize;
        FullExport.ValidatePageSize(pageSize);
        int readPasses = 2;
        if (completeness.TryGetProperty("ReadPasses", out var rp) &&
            (rp.ValueKind != JsonValueKind.Number || !rp.TryGetInt32(out readPasses))) throw new InvalidDataException();
        string expectedCompletenessMethod = readPasses == 1 ? "single-read-with-entity-specific-count-validation" : "two-identical-reads-with-entity-specific-count-validation";
        var declaredMethod = completeness.GetProperty("Method");
        if (readPasses is not (1 or 2) || declaredMethod.ValueKind != JsonValueKind.String || declaredMethod.GetString() != expectedCompletenessMethod) throw new InvalidDataException();
        string[] readNames = readPasses == 1 ? ["FirstRead"] : ["FirstRead", "SecondRead"];
        int catalogVersion = root.TryGetProperty("CatalogVersion", out var cv) ? cv.GetInt32() : 1;
        var catalog = catalogVersion switch { 1 => QueryCatalog.Version1, 2 => QueryCatalog.Version2, 3 => QueryCatalog.Version3, 4 => QueryCatalog.Version4, 5 => QueryCatalog.Version5, 6 => QueryCatalog.Version6, 7 => QueryCatalog.Version7, 8 => QueryCatalog.Version8, 9 => QueryCatalog.Version9, 10 => QueryCatalog.Version10, 11 => QueryCatalog.Version11, 12 => QueryCatalog.Version12, QueryCatalog.Version => QueryCatalog.All, _ => throw new InvalidDataException() };
        if (catalogVersion >= 13 && (root.GetProperty("ExportProfile").GetString() != "source-data-no-reports" ||
            !root.GetProperty("ExcludedQueryKinds").EnumerateArray().Select(x => x.GetString()).SequenceEqual(
                QueryCatalog.Version12.Where(x => x.IsReport).Select(x => x.Kind).Distinct().OrderBy(x => x, StringComparer.Ordinal)))) throw new InvalidDataException();
        string mode = root.GetProperty("ExportMode").GetString()!;
        if (mode is not ("full" or "date-window-test") ||
            (mode == "full") != (root.GetProperty("QueryScope").GetProperty("DateFilter").ValueKind == JsonValueKind.Null)) throw new InvalidDataException();
        string id = root.GetProperty("SnapshotId").GetString()!;
        if (!Guid.TryParseExact(id, "N", out _) || root.GetProperty("FormatVersion").GetInt32() != 2 ||
            root.GetProperty("Scope").GetString() != Snapshot.Scope ||
            (published && Path.GetFileName(directory) != id) ||
            !root.GetProperty("CompanyPathMatched").GetBoolean() || !root.GetProperty("SessionEnded").GetBoolean() ||
            !root.GetProperty("ConnectionClosed").GetBoolean()) throw new InvalidDataException();
        var descriptors = root.GetProperty("Files").EnumerateArray().ToDictionary(x => x.GetProperty("Name").GetString()!, x => x);
        var entityNames = new HashSet<string>(StringComparer.Ordinal);
        var expectedFiles = new HashSet<string>(StringComparer.Ordinal);
        bool hasUnavailable = false;
        foreach (var entity in root.GetProperty("Entities").EnumerateArray())
        {
            string name = entity.GetProperty("Entity").GetString()!;
            var spec = catalog.SingleOrDefault(x => x.Name == name) ?? throw new InvalidDataException();
            if (!entityNames.Add(name)) throw new InvalidDataException();
            if (!entity.TryGetProperty("FirstRead", out var firstEvidence) || !entity.TryGetProperty("SecondRead", out var secondEvidence) ||
                !entity.TryGetProperty("Method", out var entityMethod)) throw new InvalidDataException();
            if (firstEvidence.ValueKind != JsonValueKind.Array || firstEvidence.GetArrayLength() == 0 ||
                secondEvidence.ValueKind != JsonValueKind.Array ||
                (readPasses == 1 ? secondEvidence.GetArrayLength() != 0 : secondEvidence.GetArrayLength() == 0) ||
                entityMethod.ValueKind != JsonValueKind.String || entityMethod.GetString() != FullExport.Method(spec, readPasses)) throw new InvalidDataException();
            if (catalogVersion >= 11 && entity.GetProperty("ReportOptions").Deserialize<ReportRequestOptions>() != ReportOptions.For(spec, mode == "date-window-test")) throw new InvalidDataException();
            if (catalogVersion >= 6 && (spec.Kind is "BillToPay" or "ItemAssembliesCanBuild" || spec.RequiresReferenceBatch))
            {
                int references = spec.Kind == "BillToPay"
                    ? FullExport.ReferenceIds(directory, "Vendor", "VendorRet").Length
                    : spec.RequiresReferenceBatch ? FullExport.BatchReferences(directory, spec).Length : FullExport.ReferenceIds(directory, "Item", "ItemInventoryAssemblyRet").Length;
                if (entity.GetProperty("ReferenceCount").GetInt32() != references) throw new InvalidDataException();
                foreach (string pass in readNames)
                {
                    var pages = entity.GetProperty(pass).EnumerateArray().ToArray();
                    if (pages.Length != (spec.RequiresReferenceBatch ? 1 : Math.Max(1, references)) ||
                        pages.Any(p => p.GetProperty("NoRequest").GetBoolean() != (references == 0))) throw new InvalidDataException();
                }
                if (spec.Kind == "ItemAssembliesCanBuild" && !DateOnly.TryParseExact(entity.GetProperty("AsOfDate").GetString(),
                    "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) throw new InvalidDataException();
            }
            if (entity.GetProperty("DateWindowApplied").GetBoolean() != (mode == "date-window-test" && spec.SupportsDateWindow)) throw new InvalidDataException();
            if (entity.GetProperty("UnavailableStatusCode").ValueKind != JsonValueKind.Null)
            {
                hasUnavailable = true;
                int unavailableCode = entity.GetProperty("UnavailableStatusCode").GetInt32();
                if (FullExport.UnavailableReason(spec, unavailableCode) == null ||
                    entity.GetProperty("Records").ValueKind != JsonValueKind.Null || entity.GetProperty("Availability").GetString() != FullExport.UnavailableReason(spec, unavailableCode))
                    throw new InvalidDataException();
                foreach (string pass in readNames)
                {
                    var pages = entity.GetProperty(pass);
                    if (pages.GetArrayLength() < 1 || (!spec.RequiresReferenceBatch && spec.Kind is not ("BillToPay" or "ItemAssembliesCanBuild") && pages.GetArrayLength() != 1) ||
                        pages.EnumerateArray().Any(p => p.GetProperty("StatusCode").GetInt32() != unavailableCode || p.GetProperty("Records").GetInt32() != 0)) throw new InvalidDataException();
                }
                continue;
            }
            if (entity.GetProperty("Availability").GetString() != "exported") throw new InvalidDataException();
            int count = entity.GetProperty("Records").GetInt32();
            string xmlName = name + ".xml", jsonName = name + ".jsonl";
            foreach (string fileName in new[] { xmlName, jsonName })
            {
                expectedFiles.Add(fileName);
                var desc = descriptors[fileName];
                using var file = File.OpenRead(SafeFile(directory, fileName));
                if (file.Length != desc.GetProperty("Bytes").GetInt64() || desc.GetProperty("Records").GetInt32() != count ||
                    Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant() != desc.GetProperty("Sha256").GetString()) throw new InvalidDataException();
            }
            var xmlHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var jsonHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            int duplicates = 0;
            using (var reader = XmlReader.Create(SafeFile(directory, xmlName), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                reader.MoveToContent();
                if (reader.Name != "Records" || reader.NamespaceURI != "" || reader.GetAttribute("entity") != name) throw new InvalidDataException();
                reader.ReadStartElement("Records");
                while (reader.MoveToContent() == XmlNodeType.Element)
                {
                    var record = (XElement)XNode.ReadFrom(reader);
                    if (!spec.Accepts(record.Name.LocalName)) throw new InvalidDataException();
                    if (spec.IsReport) ReportValidation.Validate(record);
                    string primary = FullExport.Identity(spec, record);
                    int occurrence = occurrences.GetValueOrDefault(primary) + 1;
                    occurrences[primary] = occurrence;
                    if (occurrence > 1)
                    {
                        if (!spec.PreserveIdenticalDuplicates || xmlHashes[primary] != Hash(record.ToString(SaveOptions.DisableFormatting))) throw new InvalidDataException();
                        duplicates++;
                    }
                    string key = FullExport.Identity(spec, record, occurrence);
                    if (!xmlHashes.TryAdd(key, Hash(record.ToString(SaveOptions.DisableFormatting))) ||
                        !jsonHashes.TryAdd(key, Hash(FullExport.JsonRecord(spec, record, occurrence)))) throw new InvalidDataException();
                }
                if (reader.NodeType == XmlNodeType.EndElement) reader.ReadEndElement();
                if (reader.MoveToContent() != XmlNodeType.None) throw new InvalidDataException();
            }
            if (xmlHashes.Count != count || duplicates != entity.GetProperty("IdenticalDuplicateRecords").GetInt32() || RecordDigest(xmlHashes) != entity.GetProperty("RecordDigest").GetString()) throw new InvalidDataException();
            int lines = 0;
            foreach (string line in File.ReadLines(SafeFile(directory, jsonName)))
            {
                using var record = JsonDocument.Parse(line);
                string key = record.RootElement.GetProperty("RecordId").GetString()!;
                if (!jsonHashes.Remove(key, out var expected) || Hash(line) != expected) throw new InvalidDataException();
                lines++;
            }
            if (lines != count || jsonHashes.Count != 0) throw new InvalidDataException();
            foreach (string pass in readNames)
            {
                var pages = entity.GetProperty(pass).EnumerateArray().ToArray();
                if (pages.Length == 0 || pages.Sum(x => x.GetProperty("Records").GetInt32()) != count ||
                    pages.Any(x => x.GetProperty("StatusCode").GetInt32() is not (0 or 1))) throw new InvalidDataException();
                int? previousRemaining = null;
                for (int i = 0; i < pages.Length; i++)
                {
                    var page = pages[i];
                    int records = page.GetProperty("Records").GetInt32();
                    bool noRequest = page.TryGetProperty("NoRequest", out var skipped) && skipped.GetBoolean();
                    int? reported = page.GetProperty("ReportedCount").ValueKind == JsonValueKind.Null ? null : page.GetProperty("ReportedCount").GetInt32();
                    int? remaining = page.GetProperty("Remaining").ValueKind == JsonValueKind.Null ? null : page.GetProperty("Remaining").GetInt32();
                    int status = page.GetProperty("StatusCode").GetInt32();
                    if (records < 0 || remaining < 0 || (reported != null && reported != records) ||
                        page.GetProperty("Page").GetInt32() != (noRequest ? 0 : i + 1) ||
                        (noRequest && (pages.Length != 1 || count != 0 || reported != null || remaining != null ||
                            !spec.RequiresReferenceBatch && spec.Kind is not ("BillToPay" or "ItemAssembliesCanBuild"))) ||
                        (status == 1 && (records != 0 || remaining > 0)) ||
                        (spec.Metadata && status == 0 && !noRequest && reported == null)) throw new InvalidDataException();
                    if (spec.Iterator && (status == 0 || remaining != null))
                    {
                        if (remaining == null || records > pageSize ||
                            (i == 0 && records + (long)remaining != count) ||
                            (previousRemaining != null && records + (long)remaining != previousRemaining) ||
                            (remaining > 0 && records == 0)) throw new InvalidDataException();
                        previousRemaining = remaining;
                    }
                    else if (remaining != null) throw new InvalidDataException();
                }
                if (spec.Iterator && count > 0 && pages[^1].GetProperty("Remaining").GetInt32() != 0) throw new InvalidDataException();
            }
        }
        if (!entityNames.SetEquals(catalog.Select(x => x.Name)) || !expectedFiles.SetEquals(descriptors.Keys)) throw new InvalidDataException();
        if (root.GetProperty("HasUnavailableEntities").GetBoolean() != hasUnavailable) throw new InvalidDataException();
        if (catalogVersion >= 2 && JsonSerializer.Serialize(TransactionAudit.Analyze(directory)) !=
            JsonSerializer.Serialize(root.GetProperty("TransactionCoverage").Deserialize<TransactionCoverage[]>())) throw new InvalidDataException();
        if (catalogVersion >= 4 && JsonSerializer.Serialize(ListCoverageAudit.Analyze(directory, includeEntity: catalogVersion >= 7)) !=
            JsonSerializer.Serialize(root.GetProperty("ListCoverage").Deserialize<ListCoverage[]>())) throw new InvalidDataException();
        return mode;
    }

    internal static string RecordDigest(SortedDictionary<string, string> hashes)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in hashes) digest.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pair.Key) + ":" + pair.Value + "\n"));
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string SafeFile(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name is "." or "..") throw new InvalidDataException();
        string path = Path.Combine(directory, name);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        return path;
    }
}
