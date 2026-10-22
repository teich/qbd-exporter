# Current development status

This is the single current checkpoint for the project. Replace obsolete text rather than appending session logs. Keep company paths, record counts, snapshot or release identifiers, and other operational details of any real installation out of this file.

## Capability

- **Export (catalog revision 13, format 2).** 89 source and auxiliary datasets in one SDK read pass with page validation, identity checks, record digests, and file hashes. All report queries are excluded. The verifier also accepts legacy revision 12 and two-read artifacts.
- **Loader (schema version 4).** Versioned JSONB landing with exclusive-end validity intervals, per-entity churn counts (`versions_inserted`, `versions_closed`), receipt idempotency, strict chronological delivery, and persistent `delivery_issue` records for late snapshots. Migrations from schema versions 1 to 3 verify exact reconstruction before removing old storage.
- **Unattended operation.** Windows Task Scheduler heartbeat within a bounded daily window, at most three export attempts per window date, independent retry of pending deliveries, a configurable unavailable-dataset policy, DPAPI-protected credentials, and sanitized JSONL logging.

Full exports and database loads have been accepted against a real QuickBooks Desktop company file and a remote PostgreSQL 16 database. Repeated-run reliability is not certified.

## Verification

`build.ps1` builds with warnings as errors, runs the C# synthetic self-tests, and runs the mocked scheduler tests (`Test-PendingDeliveries.ps1`, `Test-ExportRecovery.ps1`) under Windows PowerShell 5.1. Setting `QBD_POSTGRES_TEST_CONNECTION_STRING` to an isolated loopback PostgreSQL 16 instance adds database acceptance: reconstruction, churn, retries, changes, deletion and reappearance, coverage gaps, concurrency, rollback, and migrations. None of these tests uses QuickBooks or real company data.

Unreleased on this branch: the integrated-application name changed to `qbd-exporter (read-only)`, the installer accepts `-AllowedUnavailableEntities`, and the loader reports the snapshot's actual unavailable set on a policy mismatch. These need one Windows build and self-test run.

## Known limitations

- A snapshot is not an atomic point-in-time backup. QuickBooks stays writable during extraction, and a single read pass provides no change detection between datasets.
- QuickBooks has raised an internal fatal error ("a write txn has been initiated inside a read transaction") when another integration wrote while the exporter was reading. Schedule exports when no other integration writes.
- Cancellation is cooperative. An in-flight SDK call can block indefinitely, and there is no forced-termination watchdog.
- `Lead`, `SalesTaxReturn`, and `PayrollLastPeriod` can return status 3231 with no data; this is recorded as not captured, not as empty.
- Historical insertion into an existing sequence is unsupported; replay retained exports in order into a fresh schema instead.

## Deferred

- Forced-termination and recovery policy, and independent hang detection.
- Snapshot retention and an administrative reload procedure after downstream pruning.
- Broader fixed validation reason codes.
- CI on a Windows runner (self-tests only; no QuickBooks).
