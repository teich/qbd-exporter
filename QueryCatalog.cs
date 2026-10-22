using System.Xml.Linq;

namespace QuickBooksProbe;

internal sealed record DateWindow(string FromModifiedDate, string ToModifiedDate);

// Capabilities checked against the installed Intuit QBFC16 type library. XML requests
// are also validated by QBFC's parser before the live integration test (see test report).
internal sealed record QuerySpec(string Kind, string Key = "ListID", bool Iterator = false,
    bool Active = false, bool Owner = false, bool Lines = false, bool Linked = false,
    bool Singleton = false, bool Metadata = true, string? Variant = null, string? ReferenceId = null, string? AsOfDate = null, string[]? References = null, bool ExpandedReportOptions = false, bool? TestIncludeSubcolumns = null)
{
    internal string Name => Variant == null ? Kind : Kind + "-" + Variant;
    internal bool RequiresReferenceBatch => Kind == "SalesTaxReturnLine" || Variant is "MissingChecks" or "JobEstimatesVsActualsDetail" or "JobProfitabilityDetail";
    internal bool IsReport => Kind.EndsWith("Report", StringComparison.Ordinal);
    internal bool SupportsDateWindow => (IsReport && Variant != "PhysicalInventoryWorksheet") || Kind is not ("ItemSites" or "ReceivePaymentToDeposit" or "SalesTaxPayable" or "ListDeleted" or "TxnDeleted" or "BillToPay") &&
        (Active || Key == "TxnID" || Kind is "BillingRate" or "Lead" or "Transaction");
    // Observed on live QB2024: BuildAssembly can repeat byte-identical TxnIDs.
    // Preserve every returned occurrence and report the duplicates, never discard data.
    internal bool PreserveIdenticalDuplicates => Kind is "BuildAssembly" or "Transaction";
    internal string Request(string id, string? iterator = null, int pageSize = 100, string version = "17.0", DateWindow? window = null)
    {
        var q = new XElement(Kind + "QueryRq", new XAttribute("requestID", id));
        if (IsReport)
        {
            q.Add(new XElement(Kind + "Type", Variant), new XElement("DisplayReport", false));
            if (Variant == "PhysicalInventoryWorksheet") { /* This report does not support dates. */ }
            else if (window == null) q.Add(new XElement("ReportDateMacro", "All"));
            else q.Add(new XElement("ReportPeriod", new XElement("FromReportDate", window.FromModifiedDate[..10]), new XElement("ToReportDate", window.ToModifiedDate[..10])));
        }
        if (IsReport && RequiresReferenceBatch)
        {
            if (References == null || References.Length == 0) throw new InvalidOperationException("Report requires references.");
            q.Add(new XElement(Variant == "MissingChecks" ? "ReportAccountFilter" : "ReportEntityFilter", References.Select(id => new XElement("ListID", id))));
        }
        if (Kind == "CustomSummaryReport") q.Add(new XElement("SummarizeColumnsBy", "TotalOnly"));
        if (Kind is "CustomDetailReport" or "CustomSummaryReport") q.Add(new XElement("SummarizeRowsBy", "Account"));
        if (Kind == "CustomDetailReport") foreach (string column in new[] { "TxnID", "Date", "TxnType", "RefNumber", "Name", "Account", "Memo", "Amount", "Debit", "Credit", "Class", "Item", "Quantity", "UnitPrice" }) q.Add(new XElement("IncludeColumn", column));
        if (ReportOptions.For(this, window != null) is { } options)
        {
            if (options.IncludeSubcolumns is bool include) q.Add(new XElement("IncludeSubcolumns", include));
            if (options.ReturnRows != null) q.Add(new XElement("ReturnRows", options.ReturnRows));
            if (options.ReturnColumns != null) q.Add(new XElement("ReturnColumns", options.ReturnColumns));
        }
        if (Metadata) q.Add(new XAttribute("metaData", "MetaDataAndResponseData"));
        if (Iterator)
        {
            q.Add(new XAttribute("iterator", iterator == null ? "Start" : "Continue"));
            if (iterator != null) q.Add(new XAttribute("iteratorID", iterator));
            q.Add(new XElement("MaxReturned", pageSize));
        }
        if (Active) q.Add(new XElement("ActiveStatus", "All"));
        if (Kind == "BillToPay") q.Add(new XElement("PayeeEntityRef", new XElement("ListID", ReferenceId ?? throw new InvalidOperationException("BillToPay requires a vendor reference."))));
        if (Kind == "ItemAssembliesCanBuild") q.Add(new XElement("ItemInventoryAssemblyRef", new XElement("ListID", ReferenceId ?? throw new InvalidOperationException("Assembly query requires an item reference."))), new XElement("TxnDate", AsOfDate ?? DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        if (Kind == "SalesTaxReturnLine")
        {
            if (References == null || References.Length == 0) throw new InvalidOperationException("Tax return lines require agency references.");
            foreach (string reference in References) q.Add(new XElement("TaxAgencyVendorRef", new XElement("ListID", reference)));
        }
        if (Kind == "ListDeleted") foreach (string type in DeletedListTypes) q.Add(new XElement("ListDelType", type));
        if (Kind == "TxnDeleted") foreach (string type in DeletedTransactionTypes) q.Add(new XElement("TxnDelType", type));
        if (Kind == "Form1099CategoryAccountMapping") q.Add(new XElement("FormType", Variant));
        if (window != null && SupportsDateWindow && !IsReport)
        {
            var dates = new[] { new XElement("FromModifiedDate", window.FromModifiedDate), new XElement("ToModifiedDate", window.ToModifiedDate) };
            if (Kind == "Transaction") q.Add(new XElement("TransactionModifiedDateRangeFilter", dates));
            else if (Key == "TxnID") q.Add(new XElement("ModifiedDateRangeFilter", dates));
            else q.Add(dates);
        }
        if (Kind == "Transaction")
        {
            q.Add(new XElement("TransactionDetailLevelFilter", "All"));
            q.Add(new XElement("TransactionPostingStatusFilter", "Either"));
            q.Add(new XElement("TransactionPaidStatusFilter", "Either"));
        }
        if (Lines) q.Add(new XElement("IncludeLineItems", true));
        if (Kind == "BuildAssembly") q.Add(new XElement("IncludeComponentLineItems", true));
        if (Linked) q.Add(new XElement("IncludeLinkedTxns", true));
        if (Owner) q.Add(new XElement("OwnerID", "0"));
        return "<?xml version=\"1.0\"?><?qbxml version=\"" + version + "\"?>" +
            new XElement("QBXML", new XElement("QBXMLMsgsRq", new XAttribute("onError", "stopOnError"), q)).ToString(SaveOptions.DisableFormatting);
    }

    internal static readonly string[] DeletedListTypes = "Account BillingRate Class Currency Customer CustomerMsg CustomerType DateDrivenTerms Employee InventorySite ItemDiscount ItemFixedAsset ItemGroup ItemInventory ItemInventoryAssembly ItemNonInventory ItemOtherCharge ItemPayment ItemSalesTax ItemSalesTaxGroup ItemService ItemSubtotal JobType OtherName PaymentMethod PayrollItemNonWage PayrollItemWage PriceLevel SalesRep SalesTaxCode ShipMethod StandardTerms ToDo UnitOfMeasureSet Vehicle Vendor VendorType WorkersCompCode".Split(' ');
    internal static readonly string[] DeletedTransactionTypes = "ARRefundCreditCard Bill BillPaymentCheck BillPaymentCreditCard BuildAssembly Charge Check CreditCardCharge CreditCardCredit CreditMemo Deposit Estimate InventoryAdjustment Invoice ItemReceipt JournalEntry PurchaseOrder ReceivePayment SalesOrder SalesReceipt SalesTaxPaymentCheck TimeTracking TransferInventory VehicleMileage VendorCredit".Split(' ');

    internal bool Accepts(string name) => Kind switch
    {
        "Item" => new[] { "ItemServiceRet", "ItemNonInventoryRet", "ItemOtherChargeRet", "ItemInventoryRet",
            "ItemInventoryAssemblyRet", "ItemFixedAssetRet", "ItemSubtotalRet", "ItemDiscountRet", "ItemPaymentRet",
            "ItemSalesTaxRet", "ItemSalesTaxGroupRet", "ItemGroupRet" }.Contains(name),
        "Terms" => name is "StandardTermsRet" or "DateDrivenTermsRet",
        "Entity" => name is "CustomerRet" or "VendorRet" or "EmployeeRet" or "OtherNameRet",
        _ => name == (IsReport ? "ReportRet" : Kind + "Ret")
    };
}

internal static class QueryCatalog
{
    internal const int Version = 13;
    internal static readonly QuerySpec[] Version1 = Build();
    internal static readonly QuerySpec[] Version2 = Version1.Concat(new QuerySpec[]
    {
        new("Lead", Iterator: true),
        new("Entity", Iterator: true, Active: true),
        new("ItemSites", Iterator: true, Active: true),
        new("ReceivePaymentToDeposit", "TxnID"),
        new("SalesTaxPayable", "PayeeEntityRef/ListID"),
        new("Transaction", "TxnID", Iterator: true)
    }).ToArray();
    internal static readonly QuerySpec[] Version3 = Version2.Concat(new QuerySpec[]
    {
        new("AccountTaxLineInfo", "TaxLineID", Metadata: false),
        new("BillToPay", "BillToPay/TxnID"),
        new("ListDeleted"),
        new("TxnDeleted", "TxnID"),
        new("Form1099CategoryAccountMapping", Singleton: true, Metadata: false, Variant: "MISC"),
        new("Form1099CategoryAccountMapping", Singleton: true, Metadata: false, Variant: "NEC")
    }).ToArray();
    internal static readonly QuerySpec[] Version4 = Version3.Concat(
        new[] { "ItemService", "ItemNonInventory", "ItemOtherCharge", "ItemInventory", "ItemInventoryAssembly", "ItemFixedAsset", "ItemSubtotal", "ItemDiscount", "ItemPayment", "ItemSalesTax", "ItemSalesTaxGroup", "ItemGroup" }
            .Select(kind => new QuerySpec(kind, Iterator: true, Active: true, Owner: true)))
        .Concat(new[] { new QuerySpec("StandardTerms", Active: true), new QuerySpec("DateDrivenTerms", Active: true) }).ToArray();
    internal static readonly QuerySpec[] Version5 = Version4.Concat(ReportCatalog.All).ToArray();
    internal static readonly QuerySpec[] Version6 = Version5.Concat(new[] { new QuerySpec("SalesTaxReturn", Metadata: false), new QuerySpec("PayrollLastPeriod", Singleton: true, Metadata: false), new QuerySpec("ItemAssembliesCanBuild", "ItemInventoryAssemblyRef/ListID", Metadata: false) }).ToArray();
    internal static readonly QuerySpec[] Version7 = Version6.Concat(new[] { new QuerySpec("CustomDetailReport", Singleton: true, Metadata: false, Variant: "CustomTxnDetail"), new QuerySpec("CustomSummaryReport", Singleton: true, Metadata: false, Variant: "CustomSummary") }).ToArray();
    internal static readonly QuerySpec[] Version8 = Version7.Concat(new[] { new QuerySpec("BarCode") }).ToArray();
    internal static readonly QuerySpec[] Version9 = Version8.Concat(new[] { new QuerySpec("SalesTaxReturnLine") }).ToArray();
    internal static readonly QuerySpec[] Version10 = Version9;
    internal static readonly QuerySpec[] Version11 = Version10.Select(x => x with { ExpandedReportOptions = x.IsReport }).ToArray();
    internal static readonly QuerySpec[] Version12 = Version11.Select(x => x.Variant == "ProfitAndLossByJob" ? x with { TestIncludeSubcolumns = false } : x).ToArray();
    // Operational source-data scope. Keep older report catalogs for verifying
    // historical artifacts, but never issue their report queries in new exports.
    internal static readonly QuerySpec[] All = Version12.Where(x => !x.IsReport).ToArray();
    private static QuerySpec[] Build()
    {
        var q = new List<QuerySpec>();
        q.Add(new("Company", Singleton: true, Metadata: false, Owner: true));
        foreach (string kind in new[] { "Host", "Preferences", "CompanyActivity" })
            q.Add(new(kind, Singleton: true, Metadata: false));
        q.Add(new("Account", Active: true, Owner: true));
        foreach (string kind in new[] { "Customer", "Vendor", "Item" }) q.Add(new(kind, Iterator: true, Active: true, Owner: true));
        foreach (string kind in new[] { "Employee", "OtherName" }) q.Add(new(kind, Active: true, Owner: true));
        foreach (string kind in new[] { "Class", "CustomerType", "VendorType", "JobType", "CustomerMsg", "PaymentMethod", "ShipMethod",
            "SalesRep", "Terms", "SalesTaxCode", "PriceLevel", "Currency", "Vehicle", "ToDo", "PayrollItemWage", "PayrollItemNonWage",
            "WorkersCompCode", "UnitOfMeasureSet", "InventorySite" }) q.Add(new(kind, Active: true));
        q.Add(new("Template"));
        q.Add(new("BillingRate"));
        q.Add(new("DataExtDef", Key: "DataExtName", Owner: true));
        foreach (string kind in new[] { "Invoice", "CreditMemo", "Estimate", "SalesOrder", "PurchaseOrder", "Bill", "ItemReceipt", "VendorCredit", "Check" })
            q.Add(new(kind, "TxnID", Iterator: true, Owner: true, Lines: true, Linked: true));
        foreach (string kind in new[] { "SalesReceipt", "ReceivePayment", "BillPaymentCheck", "BillPaymentCreditCard", "Deposit", "CreditCardCharge",
            "CreditCardCredit", "JournalEntry", "InventoryAdjustment", "SalesTaxPaymentCheck", "ARRefundCreditCard" })
            q.Add(new(kind, "TxnID", Iterator: true, Owner: true, Lines: true));
        q.Add(new("BuildAssembly", "TxnID", Iterator: true, Owner: true));
        q.Add(new("Charge", "TxnID", Iterator: true, Owner: true, Linked: true));
        foreach (string kind in new[] { "TimeTracking", "VehicleMileage", "Transfer" }) q.Add(new(kind, "TxnID", Iterator: true));
        q.Add(new("TransferInventory", "TxnID", Iterator: true, Lines: true));
        return q.ToArray();
    }
}
