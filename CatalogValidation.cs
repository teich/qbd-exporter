using System.Text.Json;

namespace QuickBooksProbe;

// Emits requests for the independent, 32-bit installed QBFC schema validator.
internal static class CatalogValidation
{
    internal static int Run()
    {
        Console.WriteLine(JsonSerializer.Serialize(QueryCatalog.All.Select(x => x.Kind is "BillToPay" or "ItemAssembliesCanBuild" ? x with { ReferenceId = "synthetic-vendor" } : x.RequiresReferenceBatch ? x with { References = new[] { "synthetic-agency" } } : x).SelectMany(spec => new DateWindow?[] { null, new("2026-01-01T00:00:00", "2026-01-10T23:59:59") }.SelectMany(window =>
            (spec.Iterator ? new string?[] { null, "{11111111-1111-1111-1111-111111111111}" } : new string?[] { null })
            .Select(iterator => new { Kind = spec.Name, Phase = (iterator == null ? "start" : "continue") + (window == null ? "-full" : "-date-window"), SchemaValidationVersion = "16.0", Xml = spec.Request("validation", iterator, version: "16.0", window: window) })))));
        return 0;
    }
}
