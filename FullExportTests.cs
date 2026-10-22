using System.Text.Json;
using System.Xml.Linq;

namespace QuickBooksProbe;

internal static class FullExportTests
{
    internal static void Run(Action<string, bool> check)
    {
        var spec = new QuerySpec("Customer", Iterator: true, Active: true, Owner: true);
        foreach (int pageSize in new[] { 1, 1000, 5000 })
        {
            var batched = FullExport.Read(new Paged(""), "ticket", new RunSummary(), spec, 1, _ => { }, pageSize: pageSize);
            check("configured pagination complete " + pageSize, batched.Count == pageSize + 1 && batched.Pages.Count == 2 && batched.Pages[0].Records == pageSize);
        }
        foreach (int pageSize in new[] { 0, -1, 5001 })
        {
            bool invalid = false;
            try { FullExport.Read(new Paged(""), "ticket", new RunSummary(), spec, 1, _ => { }, pageSize: pageSize); }
            catch (ArgumentOutOfRangeException) { invalid = true; }
            check("invalid page size rejected " + pageSize, invalid);
        }
        var tuning = SnapshotOptions.Parse(["--max-minutes", "30", "--page-size", "5000", "--test-days", "10"]);
        check("snapshot tuning parsed independently", tuning == new SnapshotOptions(10, 5000, 30));
        check("snapshot CLI defaults to 5000 records per page", SnapshotOptions.Parse([]) == new SnapshotOptions(null, 5000, null));
        check("snapshot CLI accepts historical 100-record page size", SnapshotOptions.Parse(["--page-size", "100"]) == new SnapshotOptions(null, 100, null));
        check("large page response allowance remains bounded", FullExport.QueryLimit(100) == 8 * 1024 * 1024 && FullExport.QueryLimit(1000) == 64 * 1024 * 1024 && FullExport.QueryLimit(5000) == 64 * 1024 * 1024);
        var benchmark = BatchBenchmark.Measure(new CatalogProcessor(), "ticket", new RunSummary(), spec).ToArray();
        check("benchmark compares identical full data across page sizes", benchmark.Length == 3 && benchmark.All(x => x.MatchesFirstRead && x.Records == 1));
        bool changedBenchmark = false;
        try { BatchBenchmark.Measure(new Paged(""), "ticket", new RunSummary(), spec).ToArray(); }
        catch (InvalidDataException) { changedBenchmark = true; }
        check("benchmark rejects changed population", changedBenchmark);
        foreach (string[] invalid in new[] { new[] { "--page-size", "0" }, new[] { "--page-size", "5001" }, new[] { "--max-minutes", "0" }, new[] { "--max-minutes", "1441" }, new[] { "--page-size" }, new[] { "--page-size", "1000", "--page-size", "5000" }, new[] { "--typo", "10" } })
        {
            bool rejected = false;
            try { SnapshotOptions.Parse(invalid); } catch (ArgumentException) { rejected = true; }
            check("invalid tuning rejected " + string.Join(' ', invalid), rejected);
        }
        foreach (string failure in new[] { "", "empty", "empty-iterator", "unavailable", "duplicate", "missing-remaining", "remaining-stalled", "wrong-cursor", "count-mismatch", "wrong-id", "wrong-type", "warning", "false-empty", "unprocessed" })
        {
            bool rejected = false;
            int consumed = 0;
            try
            {
                var result = FullExport.Read(new Paged(failure), "ticket", new RunSummary(), spec, 1, _ => consumed++);
                check("full reader total " + failure, result.Count == (failure is "empty" or "empty-iterator" or "unavailable" ? 0 : 101));
                check("full reader pages " + failure, result.Pages.Count == (failure is "empty" or "empty-iterator" or "unavailable" ? 1 : 2));
                if (failure == "unavailable") check("unavailable distinguished from empty", result.Digest == "unavailable:3250");
            }
            catch (Exception ex) when (ex is InvalidDataException or ResponseStatusException) { rejected = true; }
            check("full reader rejection " + failure, rejected == (failure is not ("" or "empty" or "empty-iterator" or "unavailable")));
            if (!rejected) check("full reader consumption " + failure, consumed == (failure is "empty" or "empty-iterator" or "unavailable" ? 0 : 101));
        }
        foreach (string malformed in new[] { "<ReportRet/>", "<ReportRet><NumRows>1</NumRows><NumColumns>0</NumColumns></ReportRet>", "<ReportRet><NumRows>0</NumRows><NumColumns>1</NumColumns></ReportRet>" })
        {
            bool rejected = false;
            try { ReportValidation.Validate(XElement.Parse(malformed)); } catch (InvalidDataException) { rejected = true; }
            check("report counts require corresponding data", rejected);
        }
        string largeReport = SelfTests.Response("GeneralDetailReport", "size", "<ReportRet><ReportTitle>" + new string('x', FullExport.QueryCharacterLimit) + "</ReportTitle></ReportRet>");
        bool normalLimitRejected = false;
        try { FullExport.Envelope(largeReport, "GeneralDetailReport", "size"); } catch (System.Xml.XmlException) { normalLimitRejected = true; }
        check("ordinary query size limit stays bounded", normalLimitRejected);
        check("report parser accepts larger complete payload", FullExport.Envelope(largeReport, "GeneralDetailReport", "size", FullExport.ReportCharacterLimit).Element("ReportRet")!.Element("ReportTitle")!.Value.Length == FullExport.QueryCharacterLimit);
        var nested = XElement.Parse("<InvoiceRet><TxnID>secret</TxnID><Amount>001.2300</Amount><InvoiceLineRet><ItemRef><ListID>A</ListID></ItemRef></InvoiceLineRet><InvoiceLineRet><Desc>x &amp; y</Desc></InvoiceLineRet><Extra flag='yes'>value</Extra></InvoiceRet>");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(FullExport.JsonFields(nested)));
        check("JSONL preserves decimal lexical value", json.RootElement.GetProperty("Amount")[0].GetString() == "001.2300");
        check("JSONL repeated lines", json.RootElement.GetProperty("InvoiceLineRet").GetArrayLength() == 2);
        check("JSONL nested references", json.RootElement.GetProperty("InvoiceLineRet")[0].GetProperty("ItemRef")[0].GetProperty("ListID")[0].GetString() == "A");
        check("JSONL preserves attributes", json.RootElement.GetProperty("Extra")[0].GetProperty("@attributes").GetProperty("flag").GetString() == "yes");
        check("JSONL attribute text", json.RootElement.GetProperty("Extra")[0].GetProperty("#text").GetString() == "value");
        check("catalog no duplicate datasets", QueryCatalog.All.Select(x => x.Name).Distinct().Count() == QueryCatalog.All.Length);
        check("operational catalog excludes every report", QueryCatalog.All.Length == 89 && QueryCatalog.All.All(x => !x.IsReport));
        check("historical report catalog remains available for verification", QueryCatalog.Version12.Length == 177 && QueryCatalog.Version12.Count(x => x.IsReport) == 88);
        var expandedSummary = XDocument.Parse(QueryCatalog.Version12.Single(x => x.Variant == "BalanceSheetStandard").Request("options"));
        check("summary requests all rows and columns", expandedSummary.Descendants("ReturnRows").Single().Value == "All" && expandedSummary.Descendants("ReturnColumns").Single().Value == "All");
        check("summary requests supported subcolumns", expandedSummary.Descendants("IncludeSubcolumns").Single().Value == "true");
        check("legacy catalog retains default report options", !XDocument.Parse(QueryCatalog.Version10.Single(x => x.Variant == "BalanceSheetStandard").Request("options")).Descendants("ReturnRows").Any());
        check("inventory report omits unsupported options", ReportOptions.For(QueryCatalog.Version12.Single(x => x.Variant == "PhysicalInventoryWorksheet")) == null);
        check("trial balance omits unsupported subcolumns", ReportOptions.For(QueryCatalog.Version12.Single(x => x.Variant == "TrialBalance")) is { IncludeSubcolumns: null, ReturnRows: "All" });
        var wideReport = QueryCatalog.Version12.Single(x => x.Variant == "ProfitAndLossByJob");
        check("full wide report keeps expanded subcolumns", ReportOptions.For(wideReport)!.IncludeSubcolumns == true);
        check("test wide report explicitly disables comparison columns", ReportOptions.For(wideReport, dateWindowTest: true)!.IncludeSubcolumns == false);
        check("time job detail omits unsupported expansion fields", ReportOptions.For(QueryCatalog.Version12.Single(x => x.Variant == "TimeByJobDetail")) == null);
        var transactionSpec = QueryCatalog.All.Single(x => x.Kind == "Transaction");
        var summaryRecord = XElement.Parse("<TransactionRet><TxnID>A</TxnID></TransactionRet>");
        var lineRecord = XElement.Parse("<TransactionRet><TxnID>A</TxnID><TxnLineID>B</TxnLineID></TransactionRet>");
        check("transaction summary and line identities differ", FullExport.Identity(transactionSpec, summaryRecord) != FullExport.Identity(transactionSpec, lineRecord));
        var accountingRow = new XElement(lineRecord.Name, lineRecord.Elements(), new XElement("Amount", "2.00"));
        check("index rows with same transaction and line keep distinct content", FullExport.Identity(transactionSpec, lineRecord) != FullExport.Identity(transactionSpec, accountingRow));
        check("identical index row occurrences remain distinct", FullExport.Identity(transactionSpec, accountingRow) != FullExport.Identity(transactionSpec, accountingRow, 2));
        var assemblySpec = QueryCatalog.All.Single(x => x.Kind == "ItemAssembliesCanBuild") with { AsOfDate = "2026-01-01" };
        var capacities = FullExport.Read(new CatalogProcessor(), "ticket", new RunSummary(), assemblySpec, 1, _ => { }, references: new[] { "assembly-a", "assembly-b" });
        check("capacity queries retain every referenced assembly", capacities.Count == 2 && capacities.Pages.Count == 2);
        check("capacity request fixes as-of date", XDocument.Parse((assemblySpec with { ReferenceId = "assembly-a" }).Request("test")).Descendants("TxnDate").Single().Value == "2026-01-01");
        var taxSpec = QueryCatalog.All.Single(x => x.Kind == "SalesTaxReturnLine");
        var taxRequest = XDocument.Parse((taxSpec with { References = new[] { "agency-a", "agency-b" } }).Request("test"));
        check("tax request includes every agency reference", taxRequest.Descendants("TaxAgencyVendorRef").Count() == 2);
        var noAgencies = FullExport.Read(new CatalogProcessor(), "ticket", new RunSummary(), taxSpec, 1, _ => { }, references: Array.Empty<string>());
        check("empty tax agency scope is explicit", noAgencies.Count == 0 && noAgencies.Pages.Single().NoRequest);
        var billSpec = QueryCatalog.All.Single(x => x.Kind == "BillToPay");
        var payableProcessor = new PayablesProcessor();
        var payableRows = FullExport.Read(payableProcessor, "ticket", new RunSummary(), billSpec, 1, _ => { }, references: new[] { "vendor-a", "vendor-b" });
        check("payables queries every vendor", payableRows.Count == 2 && payableProcessor.Calls == 2);
        check("payables has per-vendor evidence", payableRows.Pages.Count == 2);
        var noVendors = FullExport.Read(new PayablesProcessor(), "ticket", new RunSummary(), billSpec, 1, _ => { }, references: Array.Empty<string>());
        check("empty vendor scope is explicit rather than fabricated SDK response", noVendors.Count == 0 && noVendors.Pages.Single().NoRequest);
        foreach (var query in QueryCatalog.All.Select(x => x.Kind is "BillToPay" or "ItemAssembliesCanBuild" ? x with { ReferenceId = "synthetic-vendor" } : x.RequiresReferenceBatch ? x with { References = new[] { "synthetic-agency" } } : x))
        {
            var doc = XDocument.Parse(query.Request("synthetic"));
            check("catalog read only " + query.Kind, doc.Descendants("QBXMLMsgsRq").Single().Elements().Single().Name == query.Kind + "QueryRq");
            check("full default has no date filter " + query.Kind, !doc.Descendants("FromModifiedDate").Any());
            var filtered = XDocument.Parse(query.Request("synthetic", window: new DateWindow("2026-01-01T00:00:00", "2026-01-10T23:59:59")));
            check("date filter only where supported " + query.Kind, (filtered.Descendants("FromModifiedDate").Any() || filtered.Descendants("FromReportDate").Any()) == query.SupportsDateWindow);
        }
        string output = Path.Combine(Path.GetTempPath(), "qbd-full-tests-" + Guid.NewGuid().ToString("N"));
        foreach (bool duringBegin in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var cancelled = Snapshot.Run(() => new CatalogProcessor(
                onQuery: duringBegin ? null : () => cancellation.Cancel(),
                onBegin: duringBegin ? () => cancellation.Cancel() : null),
                @"C:\synthetic.qbw", output, cancellationToken: cancellation.Token);
            check("cancelled export returns cancellation exit", cancelled.ExitCode == 10);
            check("cancelled export cleans established session", cancelled.SessionEnded && cancelled.ConnectionClosed && cancelled.CleanupErrors.Count == 0);
            check("cancelled export does not publish", cancelled.SnapshotId == null);
        }
        var catalogProcessor = new CatalogProcessor();
        var snapshot = Snapshot.Run(() => catalogProcessor, @"C:\synthetic.qbw", output, pageSize: 5000);
        check("full snapshot published after verification", snapshot.Success && snapshot.SnapshotId != null);
        string published = Path.Combine(output, snapshot.SnapshotId!);
        SnapshotVerifier.Verify(published, published: true);
        check("latest supported version negotiated", snapshot.QbXmlVersion == "17.0");
        check("unavailable has no misleading empty files", !File.Exists(Path.Combine(published, "Currency.xml")) && !File.Exists(Path.Combine(published, "Currency.jsonl")));
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(published, "manifest.json"))))
        {
            check("manifest preserves selected page size", manifest.RootElement.GetProperty("Completeness").GetProperty("PageSize").GetInt32() == 5000);
            check("manifest declares a single read", manifest.RootElement.GetProperty("Completeness").GetProperty("ReadPasses").GetInt32() == 1 && manifest.RootElement.GetProperty("Completeness").GetProperty("Method").GetString() == "single-read-with-entity-specific-count-validation");
            var entities = manifest.RootElement.GetProperty("Entities").EnumerateArray().ToArray();
            check("every dataset request is represented by first-read evidence", catalogProcessor.Calls == entities.Sum(x => x.GetProperty("FirstRead").EnumerateArray().Count(p => !p.TryGetProperty("NoRequest", out var noRequest) || !noRequest.GetBoolean())));
            check("single-read snapshots have empty second-read evidence", entities.All(x => x.GetProperty("SecondRead").GetArrayLength() == 0));
            check("manifest explicitly declares report exclusion", manifest.RootElement.GetProperty("ExportProfile").GetString() == "source-data-no-reports" && manifest.RootElement.GetProperty("ExcludedQueryKinds").GetArrayLength() == 9);
            var costs = CostReport.Analyze(manifest.RootElement);
            check("cost analysis covers entire catalog", costs.Length == QueryCatalog.All.Length);
            check("cost analysis separates report requests", costs.Count(x => x.Category == "reports") == QueryCatalog.All.Count(x => x.IsReport));
            check("cost analysis counts one source read", costs.Single(x => x.Entity == "Customer").Calls == 1);
            var lead = manifest.RootElement.GetProperty("Entities").EnumerateArray().Single(x => x.GetProperty("Entity").GetString() == "Lead");
            check("unprocessed Lead is not empty", lead.GetProperty("Records").ValueKind == JsonValueKind.Null && lead.GetProperty("Availability").GetString() == "request-unprocessed-data-not-captured" && !File.Exists(Path.Combine(published, "Lead.xml")));
            check("manifest flags unavailable", manifest.RootElement.GetProperty("HasUnavailableEntities").GetBoolean());
            check("manifest full catalog inventory", manifest.RootElement.GetProperty("Entities").GetArrayLength() == QueryCatalog.All.Length);
            var assembly = manifest.RootElement.GetProperty("Entities").EnumerateArray().Single(x => x.GetProperty("Entity").GetString() == "BuildAssembly");
            check("assembly repeated rows preserved", assembly.GetProperty("Records").GetInt32() == 2 && assembly.GetProperty("IdenticalDuplicateRecords").GetInt32() == 1);
            var lists = manifest.RootElement.GetProperty("ListCoverage").EnumerateArray().ToArray();
            var customerList = lists.Single(x => x.GetProperty("GenericQuery").GetString() == "Entity" && x.GetProperty("DedicatedQuery").GetString() == "Customer");
            check("entity index reconciles customer IDs and content", customerList.GetProperty("MissingFromGeneric").GetInt32() == 0 && customerList.GetProperty("MissingFromDedicated").GetInt32() == 0 && customerList.GetProperty("DifferentRecords").GetInt32() == 0);
            var service = lists.Single(x => x.GetProperty("DedicatedQuery").GetString() == "ItemService");
            check("list audit matches generic and dedicated records", service.GetProperty("MissingFromGeneric").GetInt32() == 0 && service.GetProperty("DifferentRecords").GetInt32() == 0);
            check("list audit exposes missing generic type", lists.Single(x => x.GetProperty("DedicatedQuery").GetString() == "ItemGroup").GetProperty("MissingFromGeneric").GetInt32() == 1);
            var coverage = manifest.RootElement.GetProperty("TransactionCoverage").EnumerateArray().ToArray();
            var invoiceCoverage = coverage.Single(x => x.GetProperty("TransactionType").GetString() == "Invoice");
            check("transaction index reconciles matching details", invoiceCoverage.GetProperty("MissingFromDetail").GetInt32() == 0 && invoiceCoverage.GetProperty("MissingFromIndex").GetInt32() == 0);
            check("transaction index exposes unmatched details", coverage.Single(x => x.GetProperty("TransactionType").GetString() == "Check").GetProperty("MissingFromIndex").GetInt32() == 1);
        }
        PostgresLoaderTests.Run(check, published);
        string manifestPath = Path.Combine(published, "manifest.json");
        string savedManifest = File.ReadAllText(manifestPath);
        var wrongProfile = System.Text.Json.Nodes.JsonNode.Parse(savedManifest)!;
        wrongProfile["ExportProfile"] = "all-data";
        File.WriteAllText(manifestPath, wrongProfile.ToJsonString());
        bool wrongProfileRejected = false;
        try { SnapshotVerifier.Verify(published, published: true); } catch (InvalidDataException) { wrongProfileRejected = true; }
        check("verifier rejects misleading export profile", wrongProfileRejected);
        File.WriteAllText(manifestPath, savedManifest);
        var legacyTwoRead = System.Text.Json.Nodes.JsonNode.Parse(savedManifest)!;
        legacyTwoRead["Completeness"]!["ReadPasses"] = 2;
        legacyTwoRead["Completeness"]!["Method"] = "two-identical-reads-with-entity-specific-count-validation";
        foreach (var entity in legacyTwoRead["Entities"]!.AsArray())
        {
            var evidence = entity!;
            var legacySpec = QueryCatalog.All.Single(x => x.Name == evidence["Entity"]!.GetValue<string>());
            evidence["Method"] = FullExport.Method(legacySpec, 2);
            foreach (var page in evidence["FirstRead"]!.AsArray()) evidence["SecondRead"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(page!.ToJsonString()));
        }
        File.WriteAllText(manifestPath, legacyTwoRead.ToJsonString());
        SnapshotVerifier.Verify(published, published: true);
        check("verifier still accepts explicit historical two-read evidence", true);
        legacyTwoRead["Completeness"]!.AsObject().Remove("ReadPasses");
        File.WriteAllText(manifestPath, legacyTwoRead.ToJsonString());
        SnapshotVerifier.Verify(published, published: true);
        check("missing read-pass declaration retains strict historical two-read contract", true);
        var missingLegacyPass = System.Text.Json.Nodes.JsonNode.Parse(legacyTwoRead.ToJsonString())!;
        missingLegacyPass["Entities"]!.AsArray().Single(x => x!["Entity"]!.GetValue<string>() == "Customer")!["SecondRead"]!.AsArray().Clear();
        File.WriteAllText(manifestPath, missingLegacyPass.ToJsonString());
        bool missingLegacyPassRejected = false;
        try { SnapshotVerifier.Verify(published, published: true); } catch (InvalidDataException) { missingLegacyPassRejected = true; }
        check("missing pass declaration still requires historical second-read evidence", missingLegacyPassRejected);
        File.WriteAllText(manifestPath, savedManifest);

        var invalidPasses = System.Text.Json.Nodes.JsonNode.Parse(savedManifest)!;
        invalidPasses["Completeness"]!["ReadPasses"] = 3;
        File.WriteAllText(manifestPath, invalidPasses.ToJsonString());
        bool badReadPassesRejected = false;
        try { SnapshotVerifier.Verify(published, published: true); } catch (InvalidDataException) { badReadPassesRejected = true; }
        check("verifier rejects unsupported read-pass count", badReadPassesRejected);
        File.WriteAllText(manifestPath, savedManifest);
        var badMethod = System.Text.Json.Nodes.JsonNode.Parse(savedManifest)!;
        badMethod["Completeness"]!["Method"] = "two-identical-reads-with-entity-specific-count-validation";
        File.WriteAllText(manifestPath, badMethod.ToJsonString());
        bool badReadMethodRejected = false;
        try { SnapshotVerifier.Verify(published, published: true); } catch (InvalidDataException) { badReadMethodRejected = true; }
        check("verifier rejects pass-method disagreement", badReadMethodRejected);
        File.WriteAllText(manifestPath, savedManifest);
        var badEntityMethod = System.Text.Json.Nodes.JsonNode.Parse(savedManifest)!;
        badEntityMethod["Entities"]!.AsArray().Single(x => x!["Entity"]!.GetValue<string>() == "Customer")!["Method"] = "unlimited-query-single-read";
        File.WriteAllText(manifestPath, badEntityMethod.ToJsonString());
        bool badEntityMethodRejected = false;
        try { SnapshotVerifier.Verify(published, published: true); } catch (InvalidDataException) { badEntityMethodRejected = true; }
        check("verifier rejects dataset method disagreement", badEntityMethodRejected);
        File.WriteAllText(manifestPath, savedManifest);
        var badSecondRead = System.Text.Json.Nodes.JsonNode.Parse(savedManifest)!;
        var badSecondCustomer = badSecondRead["Entities"]!.AsArray().Single(x => x!["Entity"]!.GetValue<string>() == "Customer")!;
        badSecondCustomer["SecondRead"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(badSecondCustomer["FirstRead"]![0]!.ToJsonString()));
        File.WriteAllText(manifestPath, badSecondRead.ToJsonString());
        bool unexpectedSecondReadRejected = false;
        try { SnapshotVerifier.Verify(published, published: true); } catch (InvalidDataException) { unexpectedSecondReadRejected = true; }
        check("verifier rejects unexpected single-read evidence", unexpectedSecondReadRejected);
        File.WriteAllText(manifestPath, savedManifest);
        var missingReadEvidence = System.Text.Json.Nodes.JsonNode.Parse(savedManifest)!;
        missingReadEvidence["Entities"]!.AsArray().Single(x => x!["Entity"]!.GetValue<string>() == "Customer")!.AsObject().Remove("FirstRead");
        File.WriteAllText(manifestPath, missingReadEvidence.ToJsonString());
        bool missingReadRejected = false;
        try { SnapshotVerifier.Verify(published, published: true); } catch (InvalidDataException) { missingReadRejected = true; }
        check("verifier rejects missing declared-pass evidence", missingReadRejected);
        File.WriteAllText(manifestPath, savedManifest);

        var legacyManifest = System.Text.Json.Nodes.JsonNode.Parse(savedManifest)!;
        legacyManifest["Completeness"]!.AsObject().Remove("PageSize");
        File.WriteAllText(manifestPath, legacyManifest.ToJsonString());
        SnapshotVerifier.Verify(published, published: true);
        check("legacy manifests retain 100-record page contract", true);
        File.WriteAllText(manifestPath, savedManifest);
        var changedManifest = System.Text.Json.Nodes.JsonNode.Parse(savedManifest)!;
        var payableEvidence = changedManifest["Entities"]!.AsArray().Single(x => x!["Entity"]!.GetValue<string>() == "BillToPay")!;
        payableEvidence["ReferenceCount"] = 123;
        File.WriteAllText(manifestPath, changedManifest.ToJsonString());
        bool badReferenceCountRejected = false;
        try { SnapshotVerifier.Verify(published, published: true); } catch (InvalidDataException) { badReferenceCountRejected = true; }
        check("verifier checks queried references against source list", badReferenceCountRejected);
        File.WriteAllText(manifestPath, savedManifest);
        changedManifest = System.Text.Json.Nodes.JsonNode.Parse(savedManifest)!;
        var customerEvidence = changedManifest["Entities"]!.AsArray().Single(x => x!["Entity"]!.GetValue<string>() == "Customer")!;
        customerEvidence["FirstRead"]![0]!["Remaining"] = 17;
        File.WriteAllText(manifestPath, changedManifest.ToJsonString());
        bool badRemainderRejected = false;
        try { SnapshotVerifier.Verify(published, published: true); } catch (InvalidDataException) { badRemainderRejected = true; }
        check("verifier rejects inconsistent iterator remainder", badRemainderRejected);
        File.WriteAllText(manifestPath, savedManifest);
        string jsonFile = Path.Combine(published, "Account.jsonl");
        string original = File.ReadAllText(jsonFile);
        File.AppendAllText(jsonFile, "corrupt");
        bool tamperRejected = false;
        try { SnapshotVerifier.Verify(published, published: true); } catch (InvalidDataException) { tamperRejected = true; }
        check("verifier rejects tampered data", tamperRejected);
        File.WriteAllText(jsonFile, original, new System.Text.UTF8Encoding(false));
        SnapshotVerifier.Verify(published, published: true);
        var testWindow = new DateWindow("2026-01-01T00:00:00", "2026-01-10T23:59:59");
        var filteredSnapshot = Snapshot.Run(() => new CatalogProcessor(), @"C:\synthetic.qbw", output, window: testWindow);
        check("date window explicitly labeled", filteredSnapshot.Success && filteredSnapshot.ExportMode == "date-window-test");
        check("verifier exposes date window mode", SnapshotVerifier.Verify(Path.Combine(output, filteredSnapshot.SnapshotId!), published: true) == "date-window-test");
    }

    private sealed class CatalogProcessor(Action? onQuery = null, Action? onBegin = null) : IProcessor
    {
        internal int Calls;
        public void Open() { }
        public void SetReadOnly() { }
        public string Begin(string company) { onBegin?.Invoke(); return "ticket"; }
        public string CurrentCompany(string ticket) => @"C:\synthetic.qbw";
        public string[] Versions(string ticket) => ["13.0", "16.0", "17.0"];
        public string Query(string ticket, string xml)
        {
            Calls++;
            onQuery?.Invoke();
            var rq = XDocument.Parse(xml).Root!.Element("QBXMLMsgsRq")!.Elements().Single();
            string kind = rq.Name.LocalName.Replace("QueryRq", "", StringComparison.Ordinal);
            string id = (string)rq.Attribute("requestID")!;
            var spec = QueryCatalog.All.Single(x => x.Kind == kind && x.Variant == (string?)(rq.Element("FormType") ?? rq.Element(kind + "Type")));
            if (kind == "Lead") return SelfTests.Response(kind, id, "", "3231", "Error");
            if (kind == "Currency") return SelfTests.Response(kind, id, "", "3250", "Error");
            string ret = kind == "Item" ? "ItemServiceRet" : kind == "Terms" ? "StandardTermsRet" : kind == "Entity" ? "CustomerRet" : spec.IsReport ? "ReportRet" : kind + "Ret";
            var segments = spec.Key.Split('/');
            var identity = new XElement(segments[^1], "synthetic");
            foreach (var segment in segments.Reverse().Skip(1)) identity = new XElement(segment, identity);
            var record = new XElement(ret, identity);
            if (kind == "Vendor") record.Add(new XElement("IsTaxAgency", "1"));
            if (kind == "ItemAssembliesCanBuild") record.Element("ItemInventoryAssemblyRef")!.Element("ListID")!.Value = rq.Element("ItemInventoryAssemblyRef")!.Element("ListID")!.Value;
            if (spec.IsReport) record = XElement.Parse("<ReportRet><ReportTitle>synthetic</ReportTitle><NumRows>1</NumRows><NumColumns>1</NumColumns><ColDesc colID='1'/><ReportData><DataRow rowNumber='1'><ColData colID='1' value='001.20'/></DataRow></ReportData></ReportRet>");
            if (kind == "Transaction") record.Add(new XElement("TxnType", "Invoice"));
            if (kind is "ListDeleted" or "TxnDeleted")
            {
                record.Add(new XElement(kind == "ListDeleted" ? "ListDelType" : "TxnDelType", kind == "ListDeleted" ? "Customer" : "Invoice"));
                record.Add(new XElement("TimeDeleted", "2026-01-01T00:00:00"));
            }
            var doc = XDocument.Parse(SelfTests.Response(kind, id, record.ToString() + (kind == "BuildAssembly" ? record.ToString() : "")));
            var rs = doc.Descendants(kind + "QueryRs").Single();
            if (spec.Metadata) rs.SetAttributeValue("retCount", kind == "BuildAssembly" ? "2" : "1");
            if (spec.Iterator)
            {
                rs.SetAttributeValue("iteratorRemainingCount", "0");
                rs.SetAttributeValue("iteratorID", "synthetic");
            }
            return doc.ToString();
        }
        public void End(string ticket) { }
        public void Close() { }
        public void Dispose() { }
    }

    private sealed class PayablesProcessor : IProcessor
    {
        internal int Calls;
        public void Open() { }
        public void SetReadOnly() { }
        public string Begin(string company) => "ticket";
        public string CurrentCompany(string ticket) => @"C:\synthetic.qbw";
        public string[] Versions(string ticket) => ["17.0"];
        public string Query(string ticket, string xml)
        {
            Calls++;
            var request = XDocument.Parse(xml).Descendants("BillToPayQueryRq").Single();
            string vendor = request.Element("PayeeEntityRef")!.Element("ListID")!.Value;
            var record = new XElement("BillToPayRet", new XElement(Calls == 1 ? "BillToPay" : "CreditToApply", new XElement("TxnID", vendor)));
            var doc = XDocument.Parse(SelfTests.Response("BillToPay", request.Attribute("requestID")!.Value, record.ToString()));
            doc.Descendants("BillToPayQueryRs").Single().SetAttributeValue("retCount", 1);
            return doc.ToString();
        }
        public void End(string ticket) { }
        public void Close() { }
        public void Dispose() { }
    }

    private sealed class Paged(string failure) : IProcessor
    {
        public void Open() { }
        public void SetReadOnly() { }
        public string Begin(string company) => "ticket";
        public string CurrentCompany(string ticket) => @"C:\synthetic.qbw";
        public string[] Versions(string ticket) => ["13.0"];
        public string Query(string ticket, string xml)
        {
            var rq = XDocument.Parse(xml).Descendants("CustomerQueryRq").Single();
            int batchSize = int.Parse(rq.Element("MaxReturned")!.Value);
            bool continuation = (string?)rq.Attribute("iterator") == "Continue";
            string id = (string)rq.Attribute("requestID")!;
            if (failure == "unprocessed") return SelfTests.Response("Customer", id, "", "3231", "Error");
            if (failure == "unavailable") return SelfTests.Response("Customer", id, "", "3250", "Error");
            if (failure == "empty-iterator")
            {
                var empty = XDocument.Parse(SelfTests.Response("Customer", id, "", "1", "Info"));
                var ers = empty.Descendants("CustomerQueryRs").Single();
                ers.SetAttributeValue("iteratorID", "finished"); ers.SetAttributeValue("iteratorRemainingCount", "0"); ers.SetAttributeValue("retCount", "0");
                return empty.ToString();
            }
            var rs = new XElement(failure == "wrong-type" ? "VendorQueryRs" : "CustomerQueryRs",
                new XAttribute("requestID", failure == "wrong-id" ? "bad" : id),
                new XAttribute("statusCode", failure is "empty" or "false-empty" ? 1 : 0),
                new XAttribute("statusSeverity", failure == "warning" ? "Warn" : "Info"), new XAttribute("statusMessage", "synthetic"));
            if (failure != "empty")
            {
                rs.SetAttributeValue("retCount", failure == "count-mismatch" ? 102 : continuation ? 1 : batchSize);
                rs.SetAttributeValue("iteratorID", continuation && failure == "wrong-cursor" ? "changed" : "stable");
                if (failure != "missing-remaining") rs.SetAttributeValue("iteratorRemainingCount", continuation && failure != "remaining-stalled" ? 0 : 1);
                foreach (int i in continuation ? new[] { failure == "duplicate" ? 1 : batchSize + 1 } : Enumerable.Range(1, batchSize))
                    rs.Add(new XElement("CustomerRet", new XElement("ListID", i), new XElement("Name", "secret")));
            }
            return new XElement("QBXML", new XElement("QBXMLMsgsRs", rs)).ToString();
        }
        public void End(string ticket) { }
        public void Close() { }
        public void Dispose() { }
    }
}
