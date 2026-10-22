# Export and snapshot contract (format 2)

The exporter writes local snapshot artifacts and does not connect to Postgres. Full exports are unfiltered within the selected catalog. Date-window exports are test artifacts, not incremental synchronization.

## Read and verify

Ignore `.staging-*` directories and `.export.lock`. A published directory name must match its manifest's 32-character `SnapshotId`. Require `FormatVersion` 2 and a supported `CatalogVersion` (a missing version denotes revision 1). Before loading, run `QuickBooksProbe verify --snapshot <directory>` or implement equivalent checks for catalog inventory, cleanup state, file lengths and hashes, XML record digests/counts, JSONL/XML agreement, and recorded transaction coverage.

For production full-snapshot use, require `ExportMode` `full` and a null `QueryScope.DateFilter`. Inspect `HasUnavailableEntities` and each entity's availability. A downstream job must apply an explicit required/optional policy; unavailable data must not be silently treated as an available empty dataset.

Manifests and hashes detect accidental inconsistency, not malicious replacement of both data and manifest. Keep private NTFS ACLs on snapshots. Do not add exported data to Git or upload it casually.

## Record representation

Each JSONL record carries `Entity`, opaque `RecordId`, `RecordType`, and `Data`. Preserve the record ID as text; it is not always a QuickBooks TxnID or ListID. Preserve `RecordType`, including for generic `Item` records. The implemented landing-table key is `(snapshot_id, entity, record_id)`; records from different snapshots coexist. XML remains authoritative in the verified local snapshot, while schema-version-2 PostgreSQL loads retain JSONB rather than duplicating XML per row.

XML is authoritative. XML fields map to arrays in JSON even when they occur once; leaf values remain strings; nested structures remain nested. XML attributes use `@attributes`, and a leaf with attributes stores its text under `#text`. Do not coerce or round source values during landing. Repeated identical BuildAssembly rows are preserved with occurrence-specific IDs; inspect their original `TxnID` before business-level deduplication. Other unexpected or conflicting duplicate identities fail export.

Transaction-index row IDs combine SDK transaction/line identifiers, normalized XML content hash, and an occurrence number. They are snapshot-local and are not cross-snapshot upsert keys. Use original SDK identifiers retained in `Data` for business modeling.

## Completeness and consistency

New revision13/format2 exports perform one SDK read pass while retaining the existing supported fields, pagination evidence, and record digests. They record `Completeness.ReadPasses=1`; `SecondRead` is empty. The verifier must accept legacy two-read snapshots with `ReadPasses=2`. When the field is absent in historical snapshots, interpret it as 2. A single read no longer provides the prior two-pass change-detection evidence; it still does not provide transaction isolation or an atomic point-in-time snapshot.

Full exports have no date filter and request active and inactive list records, transaction lines, linked transactions, and public custom fields where supported by the SDK. Counts mean returned SDK records, not necessarily unique business transactions or transaction lines.

The transaction coverage audit compares distinct transaction IDs in the general index with dedicated transaction datasets. Generic `TransactionQuery` is permission-scoped and excludes TimeTracking; a zero gap does not prove unrestricted access or full detail. Nonzero coverage differences require investigation. List coverage comparisons are evidence to inspect; they do not discard either representation.

QuickBooks remains writable during extraction. Settings and derived balances can reflect different moments.

Status 3231 for `Lead`, `SalesTaxReturn`, and `PayrollLastPeriod` is recorded as `request-unprocessed-data-not-captured` after one empty initial status response, preserving null record counts and no data files. This narrow guard does not mean these are empty lists and does not apply to unrelated queries. Other unprocessed request types fail extraction. Tax-return line queries without agency references are recorded as no request, not as an empty SDK response.

## Current catalog and tuning

Revision 13 has 89 source and auxiliary datasets and excludes all 88 report variants. Require `ExportProfile=source-data-no-reports` and the exact excluded query kinds declared in the manifest. Missing report files in this revision are intentional and do not mean empty reports. Revision 12 remains supported with its original 177-dataset catalog and historical two-read semantics.

`Completeness.PageSize` is optional and ranges from 1 to 5000; absent means 100 for older artifacts. The verifier checks iterator pages against the recorded value. Page size does not change catalog or date scope. Ordinary query responses are capped; oversized responses fail rather than truncate. Optional timing fields are telemetry, not completeness evidence. Cancellation must not publish a partial snapshot.

This contract does not define incremental merges, deletion application, checkpoints, or a Postgres loader. A date-window test is not a change feed, and a missing record in it does not imply deletion.
