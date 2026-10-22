using System.Globalization;
using System.Xml.Linq;

namespace QuickBooksProbe;

internal static class ReportValidation
{
    internal static void Validate(XElement report)
    {
        int Number(string name)
        {
            var fields = report.Elements(name).ToArray();
            if (fields.Length != 1 || !int.TryParse(fields[0].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out int value)) throw new InvalidDataException();
            return value;
        }
        int rows = Number("NumRows"), columns = Number("NumColumns");
        var descriptors = report.Elements("ColDesc").ToArray();
        var columnIds = descriptors.Select(x => (string?)x.Attribute("colID")).ToArray();
        var columnIdSet = new HashSet<string?>(columnIds, StringComparer.Ordinal);
        if (descriptors.Length != columns || columnIds.Any(string.IsNullOrWhiteSpace) ||
            columnIdSet.Count != columns) throw new InvalidDataException();
        var data = report.Elements("ReportData").ToArray();
        if (data.Length > 1) throw new InvalidDataException();
        var records = data.SelectMany(x => x.Elements()).ToArray();
        if (records.Length != rows) throw new InvalidDataException();
        var rowNumbers = new HashSet<int>();
        foreach (var row in records)
        {
            if (row.Name.LocalName is not ("TextRow" or "DataRow" or "SubtotalRow" or "TotalRow") ||
                !int.TryParse((string?)row.Attribute("rowNumber"), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ||
                number < 1 || number > rows || !rowNumbers.Add(number)) throw new InvalidDataException();
            foreach (var cell in row.Elements("ColData"))
                if (!columnIdSet.Contains((string?)cell.Attribute("colID"))) throw new InvalidDataException();
        }
    }
}
