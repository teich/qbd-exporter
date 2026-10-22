using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace QuickBooksProbe;

internal static class SnapshotTests
{
    internal static void Run(Action<string, bool> check)
    {
        // Only synthetic data is written. Keep failed fixtures for diagnosis in the temp directory.
        string root = Path.Combine(Path.GetTempPath(), "qbd-snapshot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var request = XDocument.Parse(Snapshot.Request("Account", "test")).Descendants("AccountQueryRq").Single();
        check("full account scope", request.Element("MaxReturned") == null && (string?)request.Element("ActiveStatus") == "All" &&
            (string?)request.Attribute("metaData") == "MetaDataAndResponseData");
        foreach (string failure in new[] { "", "count", "missing-count", "duplicate", "empty-id", "changed", "company-changed", "second-query", "end", "close", "dispose", "mismatch", "reordered" })
        {
            string output = Path.Combine(root, failure.Length == 0 ? "success" : failure);
            var fake = new Processor(failure);
            var result = Snapshot.Run(() => fake, @"C:\synthetic.qbw", output, full: false);
            bool success = failure is "" or "changed" or "company-changed" or "second-query" or "reordered";
            check("snapshot outcome " + failure, result.Success == success);
            check("snapshot cleanup attempted " + failure, fake.Ended && fake.Closed && fake.Disposed);
            var committed = Directory.GetDirectories(output).Where(x => !Path.GetFileName(x).StartsWith(".staging-", StringComparison.Ordinal)).ToArray();
            check("snapshot publication " + failure, committed.Length == (success ? 1 : 0));
            if (!success)
            {
                check("no committed id " + failure, result.SnapshotId == null);
                check("failure code " + failure, result.ExitCode == (failure == "mismatch" ? 4 : failure is "end" or "close" or "dispose" ? 7 : 5));
                continue;
            }
            check("all accounts exported", result.Queries.Single(x => x.Response == "AccountQueryRs").Count == 6);
            check("single company and account read", fake.Queries == 2);
            string directory = committed.Single();
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
            check("manifest identity", manifest.RootElement.GetProperty("SnapshotId").GetString() == result.SnapshotId);
            check("manifest partial scope", !manifest.RootElement.GetProperty("Completeness").GetProperty("CoversAllEntityTypes").GetBoolean());
            check("proof manifest declares one read", manifest.RootElement.GetProperty("Completeness").GetProperty("ReadPasses").GetInt32() == 1);
            foreach (var file in manifest.RootElement.GetProperty("Files").EnumerateArray())
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(directory, file.GetProperty("Name").GetString()!));
                check("manifest bytes", file.GetProperty("Bytes").GetInt64() == bytes.Length);
                check("manifest checksum", file.GetProperty("Sha256").GetString() == Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                check("manifest record count", XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes)).Root!.Elements().Count() == file.GetProperty("Records").GetInt32());
            }
            check("private ACL survives rename", new DirectoryInfo(directory).GetAccessControl().AreAccessRulesProtected);
            string json = JsonSerializer.Serialize(result);
            check("console has no record values", !json.Contains("secret-company") && !json.Contains("secret-account"));
        }

        string locked = Path.Combine(root, "locked");
        Directory.CreateDirectory(locked);
        using (var fileLock = new FileStream(Path.Combine(locked, ".export.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            bool activated = false;
            var result = Snapshot.Run(() => { activated = true; return new Processor(""); }, @"C:\synthetic.qbw", locked);
            check("output lock before COM", result.ExitCode == 8 && !activated);
        }
        string git = Path.Combine(root, "git");
        Directory.CreateDirectory(Path.Combine(git, ".git"));
        bool rejected = false;
        try { Snapshot.ValidateOutput(Path.Combine(git, "exports")); } catch (ArgumentException) { rejected = true; }
        check("reject exports inside Git", rejected);
        string gitFile = Path.Combine(root, "worktree");
        Directory.CreateDirectory(gitFile);
        File.WriteAllText(Path.Combine(gitFile, ".git"), "synthetic");
        rejected = false;
        try { Snapshot.ValidateOutput(gitFile); } catch (ArgumentException) { rejected = true; }
        check("reject exports inside worktree", rejected);
    }

    private sealed class Processor(string failure) : IProcessor
    {
        public int Queries;
        public bool Ended, Closed, Disposed;
        public void Open() { }
        public void SetReadOnly() { }
        public string Begin(string company) => "synthetic-ticket";
        public string CurrentCompany(string ticket) => failure == "mismatch" ? @"C:\wrong.qbw" : @"C:\synthetic.qbw";
        public string[] Versions(string ticket) => ["13.0"];
        public string Query(string ticket, string xml)
        {
            Queries++;
            var rq = XDocument.Parse(xml).Root!.Element("QBXMLMsgsRq")!.Elements().Single();
            string id = (string)rq.Attribute("requestID")!;
            bool second = id.EndsWith("-2", StringComparison.Ordinal);
            if (rq.Name == "CompanyQueryRq")
                return SelfTests.Response("Company", id, "<CompanyRet><CompanyName>" + (second && failure == "company-changed" ? "changed" : "secret-company") + "</CompanyName></CompanyRet>");
            if (second && failure == "second-query") return "<broken>";
            var records = Enumerable.Range(1, 6).Select(i => new XElement("AccountRet",
                new XElement("ListID", failure == "duplicate" ? "same" : failure == "empty-id" ? " " : i.ToString()),
                new XElement("Name", "secret-account"), new XElement("Balance", second && failure == "changed" ? "2" : "1"))).ToArray();
            var response = XDocument.Parse(SelfTests.Response("Account", id, string.Concat((second && failure == "reordered" ? records.Reverse() : records).Select(x => x.ToString()))));
            if (failure != "missing-count") response.Descendants("AccountQueryRs").Single().SetAttributeValue("retCount", failure == "count" ? "7" : "6");
            return response.ToString();
        }
        public void End(string ticket) { Ended = true; if (failure == "end") throw new InvalidOperationException(); }
        public void Close() { Closed = true; if (failure == "close") throw new InvalidOperationException(); }
        public void Dispose() { Disposed = true; if (failure == "dispose") throw new InvalidOperationException(); }
    }
}
