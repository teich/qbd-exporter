using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace QuickBooksProbe;

internal sealed record ListCoverage(string GenericQuery, string DedicatedQuery, int GenericRecords,
    int? DedicatedRecords, int? MissingFromGeneric, int? MissingFromDedicated, int? DifferentRecords);

internal static class ListCoverageAudit
{
    internal static ListCoverage[] Analyze(string directory, bool includeEntity = true)
    {
        var result = new List<ListCoverage>();
        foreach (string generic in includeEntity ? new[] { "Item", "Terms", "Entity" } : new[] { "Item", "Terms" })
        {
            var records = Read(Path.Combine(directory, generic + ".xml"));
            var specs = generic == "Entity"
                ? QueryCatalog.Version1.Where(x => x.Kind is "Customer" or "Vendor" or "Employee" or "OtherName")
                : QueryCatalog.Version4.Skip(QueryCatalog.Version3.Length)
                    .Where(x => generic == "Item" ? x.Kind.StartsWith("Item", StringComparison.Ordinal) : x.Kind.EndsWith("Terms", StringComparison.Ordinal));
            foreach (var spec in specs)
            {
                var expected = records.Where(x => x.Value.Type == spec.Kind + "Ret").ToDictionary(x => x.Key, x => x.Value);
                string file = Path.Combine(directory, spec.Name + ".xml");
                var actual = File.Exists(file) ? Read(file) : null;
                result.Add(new(generic, spec.Name, expected.Count, actual?.Count,
                    actual?.Keys.Except(expected.Keys).Count(), actual == null ? null : expected.Keys.Except(actual.Keys).Count(),
                    actual == null ? null : expected.Keys.Intersect(actual.Keys).Count(k => expected[k].Hash != actual[k].Hash)));
            }
        }
        return result.ToArray();
    }

    private static Dictionary<string, (string Type, string Hash)> Read(string file)
    {
        var records = new Dictionary<string, (string Type, string Hash)>(StringComparer.Ordinal);
        using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        reader.MoveToContent();
        reader.ReadStartElement("Records");
        while (reader.MoveToContent() == XmlNodeType.Element)
        {
            var record = (XElement)XNode.ReadFrom(reader);
            var ids = record.Elements("ListID").ToArray();
            if (ids.Length != 1 || string.IsNullOrWhiteSpace(ids[0].Value)) throw new InvalidDataException();
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.ToString(SaveOptions.DisableFormatting))));
            if (!records.TryAdd(ids[0].Value, (record.Name.LocalName, hash))) throw new InvalidDataException();
        }
        return records;
    }
}
