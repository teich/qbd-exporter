using System.Xml;
using System.Xml.Linq;

namespace QuickBooksProbe;

internal sealed record TransactionCoverage(string TransactionType, int IndexRows, int IndexTransactions,
    bool HasDedicatedQuery, bool DedicatedFileAvailable, int? DetailedTransactions, int? MissingFromDetail, int? MissingFromIndex);

internal static class TransactionAudit
{
    internal static TransactionCoverage[]? Analyze(string directory)
    {
        string indexPath = Path.Combine(directory, "Transaction.xml");
        if (!File.Exists(indexPath)) return null;
        var index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var record in Records(indexPath))
        {
            string kind = Required(record, "TxnType"), id = Required(record, "TxnID");
            if (!index.TryGetValue(kind, out var ids)) index[kind] = ids = new(StringComparer.Ordinal);
            ids.Add(id);
            rows[kind] = rows.GetValueOrDefault(kind) + 1;
        }
        var dedicatedKinds = QueryCatalog.Version1.Where(x => x.Key == "TxnID").Select(x => x.Kind).ToHashSet(StringComparer.Ordinal);
        var result = new List<TransactionCoverage>();
        foreach (string kind in index.Keys.Union(dedicatedKinds).Order(StringComparer.Ordinal))
        {
            var indexed = index.GetValueOrDefault(kind) ?? new HashSet<string>(StringComparer.Ordinal);
            bool hasQuery = dedicatedKinds.Contains(kind);
            string detailPath = Path.Combine(directory, kind + ".xml");
            bool available = hasQuery && File.Exists(detailPath);
            var detailed = available ? Records(detailPath).Select(x => Required(x, "TxnID")).ToHashSet(StringComparer.Ordinal) : null;
            result.Add(new(kind, rows.GetValueOrDefault(kind), indexed.Count, hasQuery, available, detailed?.Count,
                detailed == null ? null : indexed.Except(detailed).Count(), detailed == null ? null : detailed.Except(indexed).Count()));
        }
        return result.ToArray();
    }

    private static string Required(XElement record, string name)
    {
        var fields = record.Elements(name).ToArray();
        if (fields.Length != 1 || string.IsNullOrWhiteSpace(fields[0].Value)) throw new InvalidDataException();
        return fields[0].Value;
    }

    private static IEnumerable<XElement> Records(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        reader.MoveToContent();
        reader.ReadStartElement("Records");
        while (reader.MoveToContent() == XmlNodeType.Element) yield return (XElement)XNode.ReadFrom(reader);
    }
}
