namespace QuickBooksProbe;

internal static class SelfTests
{
    internal static string Response(string kind, string id, string records, string code = "0", string severity = "Info") =>
        $"<QBXML><QBXMLMsgsRs><{kind}QueryRs requestID=\"{id}\" statusCode=\"{code}\" statusSeverity=\"{severity}\" statusMessage=\"synthetic\">{records}</{kind}QueryRs></QBXMLMsgsRs></QBXML>";
    public static int Run()
    {
        int passed = 0;
        void Check(string name, bool condition) { if (!condition) throw new Exception(name); passed++; }
        void Reject(string name, string xml, string kind = "Account", string id = "2")
        {
            bool rejected = false;
            try { Proof.Validate(xml, kind, id); } catch (Exception ex) when (ex is InvalidDataException or ResponseStatusException or System.Xml.XmlException) { rejected = true; }
            Check(name, rejected);
        }
        try
        {
            Check("valid company", Proof.Validate(Response("Company", "1", "<CompanyRet><CompanyName>synthetic</CompanyName></CompanyRet>"), "Company", "1").Count == 1);
            Check("zero accounts", Proof.Validate(Response("Account", "2", ""), "Account", "2").Count == 0);
            Reject("warning rejected", Response("Account", "2", "", "1", "Warn"));
            Reject("error rejected", Response("Account", "2", "", "500", "Error"));
            Reject("missing response", "<QBXML><QBXMLMsgsRs/></QBXML>");
            Reject("mismatched ID", Response("Account", "9", ""));
            Reject("wrong type", Response("Company", "2", ""));
            Reject("malformed XML", "<QBXML>");
            Reject("DTD", "<!DOCTYPE QBXML [<!ENTITY x 'abc'>]><QBXML/>");
            Reject("missing company", Response("Company", "1", ""), "Company", "1");
            Reject("missing ListID", Response("Account", "2", "<AccountRet/>"));
            Reject("duplicate records", Response("Account", "2", "<AccountRet><ListID>A</ListID></AccountRet><AccountRet><ListID>A</ListID></AccountRet>"));
            Reject("too many", Response("Account", "2", string.Concat(Enumerable.Range(1, 6).Select(i => $"<AccountRet><ListID>{i}</ListID></AccountRet>"))));
            foreach (string fail in new[] { "", "open", "preferences", "begin", "company", "mismatch", "versions", "query", "end", "close", "dispose" })
            {
                var fake = new FakeProcessor(fail);
                var result = Proof.Run(() => fake, @"C:\synthetic.qbw");
                Check("exit " + fail, (result.ExitCode == 0) == (fail == ""));
                Check("dispose " + fail, fake.Calls.Contains("dispose"));
                Check("close " + fail, fake.Calls.Contains("close") == (fail != "open"));
                Check("end " + fail, fake.Calls.Contains("end") == !new[] { "open", "preferences", "begin" }.Contains(fail));
                if (fail is "company" or "mismatch") Check("no query for wrong company", !fake.Calls.Contains("query"));
                if (fail == "mismatch") Check("mismatch exit", result.ExitCode == 4);
            }
            var activation = Proof.Run(() => throw new InvalidOperationException(), @"C:\synthetic.qbw");
            Check("activation failure", activation.ExitCode != 0);
            SnapshotTests.Run(Check);
            FullExportTests.Run(Check);
            Console.WriteLine($"PASS: {passed} synthetic assertions; no QuickBooks data used.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex.Message); return 1; }
    }
    private sealed class FakeProcessor(string fail) : IProcessor
    {
        public List<string> Calls { get; } = new();
        private void Call(string name) { Calls.Add(name); if (fail == name) throw new InvalidOperationException(); }
        public void Open() => Call("open");
        public void SetReadOnly() => Call("preferences");
        public string Begin(string company) { Call("begin"); return "synthetic-ticket"; }
        public string CurrentCompany(string ticket) { Call("company"); return fail == "mismatch" ? @"C:\other.qbw" : @"C:\synthetic.qbw"; }
        public string[] Versions(string ticket) { Call("versions"); return ["13.0"]; }
        public string Query(string ticket, string xml)
        {
            Call("query");
            if (!xml.Contains("QueryRq") || xml.Contains("AddRq")) throw new Exception();
            return xml.Contains("CompanyQueryRq") ? Response("Company", "1", "<CompanyRet/>") : Response("Account", "2", "<AccountRet><ListID>synthetic</ListID></AccountRet>");
        }
        public void End(string ticket) => Call("end");
        public void Close() => Call("close");
        public void Dispose() => Call("dispose");
    }
}
