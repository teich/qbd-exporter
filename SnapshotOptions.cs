using System.Globalization;

namespace QuickBooksProbe;

internal sealed record SnapshotOptions(int? TestDays, int PageSize, int? MaxMinutes)
{
    internal const int DefaultPageSize = 5000;

    internal static SnapshotOptions Parse(string[] args)
    {
        int? days = null, minutes = null;
        int pageSize = DefaultPageSize;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (args.Length % 2 != 0) throw new ArgumentException();
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!seen.Add(args[i]) || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                throw new ArgumentException();
            switch (args[i])
            {
                case "--test-days" when value is >= 1 and <= 3650: days = value; break;
                case "--page-size" when value is >= 1 and <= FullExport.MaximumPageSize: pageSize = value; break;
                case "--max-minutes" when value is >= 1 and <= 1440: minutes = value; break;
                default: throw new ArgumentException();
            }
        }
        return new(days, pageSize, minutes);
    }
}
