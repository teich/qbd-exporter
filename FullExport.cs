using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace QuickBooksProbe;

internal sealed record PageEvidence(int Page, int StatusCode, int Records, int? ReportedCount, int? Remaining, bool NoRequest = false, long? QueryMilliseconds = null, int? ResponseCharacters = null);
internal sealed record EntityEvidence(string Entity, int? Records, string? RecordDigest, string Method,
    List<PageEvidence> FirstRead, List<PageEvidence> SecondRead, string Availability = "exported", int? UnavailableStatusCode = null, bool DateWindowApplied = false,
    int IdenticalDuplicateRecords = 0, int? ReferenceCount = null, string? AsOfDate = null, long? ReadAndWriteMilliseconds = null, ReportRequestOptions? ReportOptions = null);

internal static class FullExport
{
    internal const int PageSize = 100;
    // Application tuning bound, not a claimed QuickBooks SDK maximum.
    internal const int MaximumPageSize = 5000;
    internal const int QueryCharacterLimit = 8 * 1024 * 1024;
    internal const int LargePageQueryCharacterLimit = 64 * 1024 * 1024;
    internal static int QueryLimit(int pageSize) => pageSize > PageSize ? LargePageQueryCharacterLimit : QueryCharacterLimit;
    internal const int ReportCharacterLimit = 256 * 1024 * 1024;
    internal static List<EntityEvidence> Extract(IProcessor rp, string ticket, RunSummary summary,
        string directory, List<SnapshotFile> files, IReadOnlyList<QuerySpec>? catalog = null, DateWindow? window = null, int pageSize = PageSize)
    {
        ValidatePageSize(pageSize);
        var evidence = new List<EntityEvidence>();
        foreach (var configured in catalog ?? QueryCatalog.All)
        {
            var spec = configured.Kind == "ItemAssembliesCanBuild"
                ? configured with { AsOfDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
                : configured;
            var references = spec.Kind == "BillToPay" ? ReferenceIds(directory, "Vendor", "VendorRet") : spec.Kind == "ItemAssembliesCanBuild" ? ReferenceIds(directory, "Item", "ItemInventoryAssemblyRet") : spec.RequiresReferenceBatch ? BatchReferences(directory, spec) : null;
            Console.Error.WriteLine($"Reading {spec.Name}...");
            var entityTimer = Stopwatch.StartNew();
            string xmlName = spec.Name + ".xml", jsonName = spec.Name + ".jsonl";
            summary.Stage = "snapshot-storage";
            using (var xmlFile = new FileStream(Path.Combine(directory, xmlName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var jsonFile = new FileStream(Path.Combine(directory, jsonName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var xml = XmlWriter.Create(xmlFile, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
                using var json = new StreamWriter(jsonFile, new UTF8Encoding(false), leaveOpen: true);
                xml.WriteStartDocument();
                xml.WriteStartElement("Records");
                xml.WriteAttributeString("entity", spec.Name);
                var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
                var first = Read(rp, ticket, summary, spec, 1, record =>
                {
                    summary.Stage = "snapshot-storage";
                    record.WriteTo(xml);
                    string primary = Identity(spec, record);
                    int occurrence = occurrences.GetValueOrDefault(primary) + 1;
                    occurrences[primary] = occurrence;
                    json.WriteLine(JsonRecord(spec, record, occurrence));
                }, window, references, pageSize);
                summary.Stage = "snapshot-storage";
                xml.WriteEndElement(); xml.WriteEndDocument(); xml.Flush(); json.Flush();
                xmlFile.Flush(true); jsonFile.Flush(true);
                bool unavailable = first.Digest.StartsWith("unavailable:", StringComparison.Ordinal);
                evidence.Add(new(spec.Name, unavailable ? null : first.Count, unavailable ? null : first.Digest,
                    Method(spec, 1), first.Pages, new(), unavailable ? UnavailableReason(spec, first.Pages[0].StatusCode)! : "exported",
                    unavailable ? first.Pages[0].StatusCode : null, window != null && spec.SupportsDateWindow, first.Duplicates,
                    references?.Length, spec.AsOfDate, entityTimer.ElapsedMilliseconds, ReportOptions.For(spec, window != null)));
                summary.Queries.Add(new QuerySummary(spec.Kind + "QueryRs", unavailable ? first.Pages[0].StatusCode : 0,
                    unavailable ? "Error" : "Info", first.Count, first.Paths));
            }
            if (evidence[^1].UnavailableStatusCode != null)
            {
                // These two exact files were created by this entity above. An unavailable
                // query must not masquerade as a successfully exported empty dataset.
                File.Delete(Path.Combine(directory, xmlName));
                File.Delete(Path.Combine(directory, jsonName));
                Console.Error.WriteLine($"Unavailable {spec.Kind}: {evidence[^1].Availability} ({evidence[^1].UnavailableStatusCode}).");
                continue;
            }
            files.Add(Describe(directory, xmlName, evidence[^1].Records!.Value));
            files.Add(Describe(directory, jsonName, evidence[^1].Records!.Value));
            long queryMilliseconds = evidence[^1].FirstRead.Sum(p => p.QueryMilliseconds ?? 0);
            Console.Error.WriteLine($"Validated {spec.Name}: {evidence[^1].Records} records; SDK calls {queryMilliseconds} ms, reads/write/flush {evidence[^1].ReadAndWriteMilliseconds} ms.");
        }
        return evidence;
    }

    internal static string Method(QuerySpec spec, int readPasses)
    {
        string method = spec.RequiresReferenceBatch ? "reference-batch-from-exported-list-scope" :
            spec.Kind is "BillToPay" or "ItemAssembliesCanBuild" ? "reference-fanout-from-exported-list-scope" :
            spec.Iterator ? "iterator-exhausted-counts-checked" : "unlimited-query";
        return readPasses == 1 ? method + "-single-read" : method + "-two-identical-reads";
    }

    internal static (int Count, string Digest, List<PageEvidence> Pages, string[] Paths, int Duplicates) Read(IProcessor rp, string ticket,
        RunSummary summary, QuerySpec spec, int pass, Action<XElement> consume, DateWindow? window = null, string[]? references = null, int pageSize = PageSize)
    {
        ValidatePageSize(pageSize);
        if (spec.RequiresReferenceBatch)
        {
            if (references == null) throw new InvalidDataException();
            if (references.Length == 0) return (0, SnapshotVerifier.RecordDigest(new(StringComparer.Ordinal)), new() { new(0, 0, 0, null, null, NoRequest: true) }, Array.Empty<string>(), 0);
            return ReadCore(rp, ticket, summary, spec with { References = references }, pass, consume, window, pageSize: pageSize);
        }
        if (spec.Kind is not ("BillToPay" or "ItemAssembliesCanBuild")) return ReadCore(rp, ticket, summary, spec, pass, consume, window, pageSize: pageSize);
        if (references == null) throw new InvalidDataException();
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var pages = new List<PageEvidence>();
        string? unavailable = null;
        int successfulPartitions = 0;
        for (int i = 0; i < references.Length; i++)
        {
            var part = ReadCore(rp, ticket, summary, spec with { ReferenceId = references[i] }, pass, record =>
            {
                if (!hashes.TryAdd(Identity(spec, record), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.ToString(SaveOptions.DisableFormatting))))))
                    throw new InvalidDataException();
                consume(record);
            }, window, i + 1, pageSize);
            if (part.Digest.StartsWith("unavailable:", StringComparison.Ordinal))
            {
                if (successfulPartitions > 0 || (unavailable != null && unavailable != part.Digest)) throw new InvalidDataException();
                unavailable = part.Digest;
            }
            else
            {
                if (unavailable != null) throw new InvalidDataException();
                successfulPartitions++;
            }
            pages.AddRange(part.Pages.Select(p => p with { Page = pages.Count + p.Page }));
            paths.UnionWith(part.Paths);
            if ((i + 1) % 100 == 0) Console.Error.WriteLine($"{spec.Kind}: read {pass}, {i + 1}/{references.Length} references queried.");
        }
        // A zero-vendor test scope legitimately has no requests/records. Keep explicit evidence.
        if (references.Length == 0) pages.Add(new(0, 0, 0, null, null, NoRequest: true));
        return (hashes.Count, unavailable ?? SnapshotVerifier.RecordDigest(hashes), pages, paths.ToArray(), 0);
    }

    internal static string[] BatchReferences(string directory, QuerySpec spec) => spec.Kind == "SalesTaxReturnLine"
        ? ReferenceIds(directory, "Vendor", "VendorRet", taxAgenciesOnly: true)
        : spec.Variant == "MissingChecks" ? ReferenceIds(directory, "Account", "AccountRet", bankOnly: true)
        : ReferenceIds(directory, "Customer", "CustomerRet");

    internal static string[] ReferenceIds(string directory, string dataset, string recordType, bool taxAgenciesOnly = false, bool bankOnly = false)
    {
        using var reader = XmlReader.Create(Path.Combine(directory, dataset + ".xml"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        reader.MoveToContent(); reader.ReadStartElement("Records");
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        while (reader.MoveToContent() == XmlNodeType.Element)
        {
            var vendor = (XElement)XNode.ReadFrom(reader);
            if (vendor.Name.LocalName != recordType) continue;
            if (bankOnly && (string?)vendor.Element("AccountType") != "Bank") continue;
            if (taxAgenciesOnly && (bool?)vendor.Element("IsTaxAgency") != true && (bool?)vendor.Element("IsSalesTaxAgency") != true) continue;
            string? id = (string?)vendor.Element("ListID");
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) throw new InvalidDataException();
        }
        return ids.ToArray();
    }

    internal static void ValidatePageSize(int pageSize)
    {
        if (pageSize is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
    }

    private static (int Count, string Digest, List<PageEvidence> Pages, string[] Paths, int Duplicates) ReadCore(IProcessor rp, string ticket,
        RunSummary summary, QuerySpec spec, int pass, Action<XElement> consume, DateWindow? window, int partition = 0, int pageSize = PageSize)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        int duplicates = 0;
        var pages = new List<PageEvidence>();
        string? iterator = null;
        int? previousRemaining = null, expectedTotal = null;
        for (int page = 1; page <= 1_000_000; page++)
        {
            summary.Stage = spec.Kind + "Query";
            string id = $"{spec.Name}-{pass}-{partition}-{page}";
            string request = spec.Request(id, iterator, pageSize, summary.QbXmlVersion ?? "17.0", window);
            var queryTimer = Stopwatch.StartNew();
            string responseXml = rp.Query(ticket, request);
            queryTimer.Stop();
            int characterLimit = spec.IsReport ? ReportCharacterLimit : QueryLimit(pageSize);
            if (responseXml.Length > characterLimit)
            {
                summary.ValidationFailure = "response-size-limit";
                summary.LastPage = new { Entity = spec.Name, Pass = pass, Page = page, PageSize = pageSize, ResponseCharacters = responseXml.Length, CharacterLimit = characterLimit };
                throw new XmlException("Response exceeds configured character limit.");
            }
            var response = Envelope(responseXml, spec.Kind, id, characterLimit);
            int code = int.Parse(response.Attribute("statusCode")!.Value, CultureInfo.InvariantCulture);
            summary.LastPage = new { Entity = spec.Name, StatusCode = code, Pass = pass, Page = page };
            if (page == 1 && UnavailableReason(spec, code) != null && response.Attribute("statusSeverity")!.Value == "Error" &&
                !response.HasElements && response.Attribute("iteratorID") == null && response.Attribute("iteratorRemainingCount") == null &&
                OptionalCount(response, "retCount") is null or 0)
                return (0, "unavailable:" + code, new List<PageEvidence> { new(1, code, 0, OptionalCount(response, "retCount"), null, QueryMilliseconds: queryTimer.ElapsedMilliseconds) }, Array.Empty<string>(), 0);
            if (response.Attribute("statusSeverity")!.Value != "Info" || code is not (0 or 1)) throw new ResponseStatusException(code);
            var records = response.Elements().ToArray();
            if (records.Any(x => x.Name.Namespace != XNamespace.None || !spec.Accepts(x.Name.LocalName))) throw new InvalidDataException();
            int? count = OptionalCount(response, "retCount");
            int? remaining = OptionalCount(response, "iteratorRemainingCount");
            string? returnedIterator = (string?)response.Attribute("iteratorID");
            summary.LastPage = new { Entity = spec.Kind, StatusCode = code, Pass = pass, Page = page, Records = records.Length, ReportedCount = count,
                Remaining = remaining, HasIterator = !string.IsNullOrWhiteSpace(returnedIterator), IteratorChanged = iterator != null && iterator != returnedIterator };
            // Code 1 is Intuit's explicit empty result. It must never conceal records or unfinished iteration.
            if (code == 1 && (records.Length != 0 || count is > 0 || remaining is > 0 || page != 1)) throw new InvalidDataException();
            if (spec.Singleton && records.Length != 1) throw new InvalidDataException();
            if (spec.Metadata && code == 0 && count == null) throw new InvalidDataException();
            if (spec.Iterator && (code == 0 || remaining != null))
            {
                if (records.Length > pageSize || remaining == null ||
                    (remaining > 0 && (records.Length == 0 || string.IsNullOrWhiteSpace(returnedIterator))) ||
                    (iterator != null && returnedIterator != iterator) ||
                    (previousRemaining.HasValue && previousRemaining.Value != records.Length + remaining.Value))
                    throw new InvalidDataException();
                if (page == 1)
                {
                    expectedTotal = checked(records.Length + remaining.Value);
                }
                // Live QB2024 and the SDK response contract report this page's Ret
                // count, not the size of the whole iterator, in retCount.
                if (count.HasValue && count != records.Length) throw new InvalidDataException();
                iterator = returnedIterator;
                previousRemaining = remaining;
            }
            else if (remaining != null || returnedIterator != null || (count != null && count != records.Length)) throw new InvalidDataException();
            foreach (var record in records)
            {
                summary.Stage = spec.Kind + "Query";
                if (spec.IsReport) ReportValidation.Validate(record);
                string key = Identity(spec, record);
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.ToString(SaveOptions.DisableFormatting))));
                if (!hashes.TryAdd(key, hash))
                {
                    if (!spec.PreserveIdenticalDuplicates || hashes[key] != hash)
                    {
                        summary.ValidationFailure = "duplicate-record-identity";
                        summary.LastPage = new { Entity = spec.Kind, Pass = pass, Page = page, Records = records.Length,
                            RecordsProcessed = hashes.Count, DuplicateHasSameContent = hashes[key] == hash };
                        throw new InvalidDataException();
                    }
                    int occurrence = occurrences.GetValueOrDefault(key, 1) + 1;
                    occurrences[key] = occurrence;
                    hashes.Add(Identity(spec, record, occurrence), hash);
                    duplicates++;
                }
                foreach (var element in record.DescendantsAndSelf())
                    paths.Add(string.Join("/", element.AncestorsAndSelf().TakeWhile(a => a != response).Reverse().Select(a => a.Name.LocalName)));
                consume(record);
            }
            pages.Add(new(page, code, records.Length, count, remaining, QueryMilliseconds: queryTimer.ElapsedMilliseconds, ResponseCharacters: responseXml.Length));
            if (page % 25 == 0) Console.Error.WriteLine($"{spec.Kind}: read {pass}, page {page}, {hashes.Count} records, {remaining ?? 0} remaining.");
            if (!spec.Iterator || remaining is null or 0)
            {
                if (expectedTotal != null && expectedTotal != hashes.Count) throw new InvalidDataException();
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var pair in hashes)
                {
                    // JSON string encoding avoids ambiguous key separators.
                    digest.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pair.Key) + ":" + pair.Value + "\n"));
                }
                return (hashes.Count, Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(), pages, paths.ToArray(), duplicates);
            }
        }
        throw new InvalidDataException();
    }

    internal static XElement Envelope(string xml, string kind, string id, int characterLimit = QueryCharacterLimit)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = characterLimit });
        var doc = XDocument.Load(reader);
        if (doc.Root?.Name != "QBXML" || doc.Root.Elements().Count() != 1) throw new InvalidDataException();
        var messages = doc.Root.Element("QBXMLMsgsRs");
        if (messages == null || messages.Elements().Count() != 1) throw new InvalidDataException();
        var response = messages.Elements().Single();
        if (response.Name != kind + "QueryRs" || (string?)response.Attribute("requestID") != id ||
            response.Attribute("statusMessage") == null || response.Attribute("statusSeverity") == null ||
            !int.TryParse((string?)response.Attribute("statusCode"), NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw new InvalidDataException();
        return response;
    }

    private static int? OptionalCount(XElement response, string name)
    {
        var attribute = response.Attribute(name);
        if (attribute == null) return null;
        if (!int.TryParse(attribute.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count)) throw new InvalidDataException();
        return count;
    }

    internal static string? UnavailableReason(QuerySpec spec, int code) => code switch
    {
        3231 when spec.Kind is "Lead" or "SalesTaxReturn" or "PayrollLastPeriod" => "request-unprocessed-data-not-captured",
        3250 => "feature-not-enabled-or-not-supported",
        3220 => "not-authorized-or-feature-disabled",
        3260 => "insufficient-permissions",
        3261 => "sensitive-data-permission-required",
        3262 => "payroll-subscription-required",
        _ => null
    };

    internal static string Identity(QuerySpec spec, XElement record, int occurrence = 1)
    {
        if (spec.Singleton) return spec.Name;
        if (spec.Kind == "BillToPay")
        {
            var variants = record.Elements().Where(x => x.Name == "BillToPay" || x.Name == "CreditToApply").ToArray();
            if (variants.Length != 1) throw new InvalidDataException();
            var ids = variants[0].Elements("TxnID").ToArray();
            if (ids.Length != 1 || string.IsNullOrWhiteSpace(ids[0].Value)) throw new InvalidDataException();
            return variants[0].Name.LocalName + ":" + ids[0].Value;
        }
        IEnumerable<XElement> keyNodes = new[] { record };
        foreach (string segment in spec.Key.Split('/')) keyNodes = keyNodes.SelectMany(x => x.Elements(segment));
        var keys = keyNodes.ToArray();
        if (keys.Length != 1 || string.IsNullOrWhiteSpace(keys[0].Value) || keys[0].HasElements) throw new InvalidDataException();
        if (spec.Kind is "ListDeleted" or "TxnDeleted")
        {
            string typeField = spec.Kind == "ListDeleted" ? "ListDelType" : "TxnDelType";
            string? type = (string?)record.Element(typeField), deleted = (string?)record.Element("TimeDeleted");
            if (record.Elements(typeField).Count() != 1 || record.Elements("TimeDeleted").Count() != 1 || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(deleted)) throw new InvalidDataException();
            return JsonSerializer.Serialize(new[] { record.Name.LocalName, keys[0].Value, type, deleted });
        }
        if (spec.Kind is "Transaction" or "ReceivePaymentToDeposit")
        {
            var lineIds = record.Elements("TxnLineID").ToArray();
            if (lineIds.Length > 1 || (lineIds.Length == 1 && string.IsNullOrWhiteSpace(lineIds[0].Value))) throw new InvalidDataException();
            if (spec.Kind == "Transaction")
            {
                // The live index can repeat TxnID/TxnLineID with different accounting rows.
                // Content plus occurrence preserves all rows without inventing a stable line ID.
                string content = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.ToString(SaveOptions.DisableFormatting))));
                return JsonSerializer.Serialize(new object?[] { record.Name.LocalName, keys[0].Value, lineIds.SingleOrDefault()?.Value, content, occurrence });
            }
            return JsonSerializer.Serialize(new string?[] { record.Name.LocalName, keys[0].Value, lineIds.SingleOrDefault()?.Value });
        }
        string primary = record.Name.LocalName + ":" + keys[0].Value;
        return spec.PreserveIdenticalDuplicates ? JsonSerializer.Serialize(new object[] { primary, occurrence }) : primary;
    }

    // Every field is an array, even when it occurs once, so JSONB paths stay stable
    // as new repeating line items/custom fields appear. Scalars remain exact strings.
    internal static Dictionary<string, object> JsonFields(XElement element)
    {
        var data = element.Elements().GroupBy(x => x.Name.ToString()).ToDictionary(g => g.Key,
            g => (object)g.Select(x => x.HasElements || x.HasAttributes ? (object)JsonFields(x) : x.Value).ToArray());
        if (element.HasAttributes) data["@attributes"] = element.Attributes().ToDictionary(x => x.Name.ToString(), x => x.Value);
        if (!element.HasElements) data["#text"] = element.Value;
        return data;
    }

    internal static string JsonRecord(QuerySpec spec, XElement record, int occurrence = 1) => JsonSerializer.Serialize(new
        { Entity = spec.Name, RecordType = record.Name.LocalName, RecordId = Identity(spec, record, occurrence), Data = JsonFields(record) });

    private static SnapshotFile Describe(string directory, string name, int count)
    {
        using var stream = File.OpenRead(Path.Combine(directory, name));
        return new(name, count, stream.Length, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }
}
