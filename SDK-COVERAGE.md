# SDK coverage

Catalog revision 13 requests 89 source and auxiliary datasets and excludes all 88 report variants. The exact request inventory is defined in `QueryCatalog.cs`; do not infer broader completeness from a successful run.

## Availability

Availability depends on the QuickBooks edition, region, enabled features, and subscriptions. A dataset the SDK cannot provide is recorded in the manifest with its status code, a null record count, and no data files. The status codes the exporter accepts as unavailable are:

| SDK status | Meaning recorded | Typical datasets |
| --- | --- | --- |
| 3250 | Feature not enabled or not supported | `Currency` (multi-currency off), `WorkersCompCode`, `InventorySite` and `TransferInventory` (advanced inventory), `BarCode` |
| 3220 | Not authorized or feature disabled | Any |
| 3260 | Insufficient permissions | Any |
| 3261 | Sensitive-data permission required | Any |
| 3262 | Payroll subscription required | `PayrollItemNonWage` |
| 3231 | Request unprocessed; data not captured | `Lead`, `SalesTaxReturn`, `PayrollLastPeriod` only |

None of these statuses means the dataset is empty. Any other error fails the export. The loader requires an explicit, exact list of unavailable datasets for each load; see the README for how to discover yours.

## Representation notes

Generic `Entity` and dedicated `Customer` queries return the same customer records, except that generic `Entity` omits `DataExtRet` custom-field children (`DataExtName`, `DataExtType`, `DataExtValue`, `OwnerID`). The dedicated `Customer` query preserves them; both representations are retained. The program does not query private custom fields owned by other applications.

## Scope limits

Revision 13 intentionally omits report queries, so budgets and report output are out of scope; their absence does not mean budgets are absent. Generic transaction-index coverage is permission-scoped, excludes TimeTracking, and does not prove that every transaction has full detail. List and transaction coverage comparisons are diagnostic evidence; investigate differences without discarding either representation. Deletion history is limited by SDK retention.

## Evidence boundary

The SDK/schema audit is not a substitute for live validation: schema parsing has accepted at least one report enum the live SDK rejected. See [EXPORT-CONTRACT.md](EXPORT-CONTRACT.md) for interpretation rules.
