namespace QuickBooksProbe;

internal sealed record ReportRequestOptions(bool? IncludeSubcolumns, string? ReturnRows, string? ReturnColumns);

internal static class ReportOptions
{
    // Intuit Programmer's Guide table 9-18 lists these summary variants as
    // supporting row/column selection. Inventory reports are deliberately absent.
    private static readonly HashSet<string> SummarySelection = new(
        ("BalanceSheetPrevYearComp BalanceSheetStandard BalanceSheetSummary CustomerBalanceSummary " +
         "ExpenseByVendorSummary IncomeByCustomerSummary IncomeTaxSummary ProfitAndLossByClass ProfitAndLossByJob " +
         "ProfitAndLossPrevYearComp ProfitAndLossStandard ProfitAndLossYTDComp PurchaseByItemSummary " +
         "PurchaseByVendorSummary SalesByCustomerSummary SalesByItemSummary SalesByRepSummary SalesTaxLiability " +
         "SalesTaxRevenueSummary TrialBalance VendorBalanceSummary").Split(' '), StringComparer.Ordinal);

    internal static ReportRequestOptions? For(QuerySpec spec, bool dateWindowTest = false)
    {
        if (!spec.ExpandedReportOptions) return null;
        // Table 9-13 and live QB2024: this detail variant rejects even
        // IncludeSubcolumns; its standard five columns are returned by default.
        if (spec.Kind == "TimeReport" && spec.Variant == "TimeByJobDetail") return null;
        if (spec.Kind == "GeneralSummaryReport" && SummarySelection.Contains(spec.Variant!))
            return new(dateWindowTest && spec.TestIncludeSubcolumns.HasValue ? spec.TestIncludeSubcolumns : spec.Variant is "IncomeTaxSummary" or "SalesTaxLiability" or "TrialBalance" ? null : true, "All", "All");
        if (spec.Kind is "PayrollSummaryReport" or "TimeReport" or "CustomSummaryReport") return new(true, "All", "All");
        if (spec.Kind == "JobReport") return new(true, null, null);
        return null;
    }
}
