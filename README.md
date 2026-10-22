# qbd-exporter

Read-only extraction of QuickBooks Desktop company data into verified local snapshots, plus an optional loader that keeps a versioned history of those snapshots in PostgreSQL.

- **Exporter**: a Windows command-line tool that talks to an installed QuickBooks Desktop through the QuickBooks SDK (qbXML), reads 89 source and auxiliary datasets (lists, transactions with lines, linked transactions, public custom fields), and publishes a self-verifying snapshot of XML plus JSONL. Reports are not exported.
- **Loader**: imports verified snapshots into PostgreSQL as JSONB records with validity intervals, so you can reconstruct exactly what any snapshot contained and see what changed between them. It never connects to QuickBooks.
- **Scheduler**: optional PowerShell scripts that run export and load nightly through Windows Task Scheduler.

The exporter only sends query requests and opens its session with read-only preferences. It does not modify your company file.

This project is not affiliated with or endorsed by Intuit. QuickBooks is a trademark of Intuit Inc.

## Requirements

- Windows x64 with QuickBooks Desktop installed and a company file you are authorized to read. The SDK must report qbXML 13.0 or later; the newest version up to 17.0 is used.
- The [QuickBooks Desktop SDK](https://developer.intuit.com/app/developer/qbdesktop/docs/get-started) request processor (`QBXMLRP2`), which QuickBooks Desktop normally installs.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build.
- PostgreSQL 16 or later, only if you use the loader.

Which datasets are available depends on your QuickBooks edition, region, and subscriptions (multi-currency, advanced inventory, payroll, and so on). Unavailable datasets are recorded explicitly with their SDK status; they are never treated as empty. See [SDK-COVERAGE.md](SDK-COVERAGE.md).

## Build

```powershell
.\build.ps1
```

This restores, builds Release, and runs the synthetic self-tests (no QuickBooks or database needed). The executable is `bin\Release\net10.0-windows\QuickBooksProbe.exe`.

If `dotnet` cannot write to your user profile, point its directories at the ignored `private\` folder first:

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD 'private\build\dotnet'
$env:APPDATA = Join-Path $PWD 'private\build\appdata'
$env:NUGET_PACKAGES = Join-Path $PWD 'private\build\packages'
```

## Export a snapshot

Run from PowerShell in the same logged-in Windows account that runs QuickBooks. The first run triggers QuickBooks' Integrated Applications prompt for **qbd-exporter (read-only)**; an administrator must approve it. Allowing access when QuickBooks is not running lets unattended runs start QuickBooks through the SDK.

```powershell
$exe = '.\bin\Release\net10.0-windows\QuickBooksProbe.exe'
$snapshotRoot = Join-Path $env:USERPROFILE 'qbd-snapshots'
& $exe snapshot --company 'C:\path\to\company.qbw' --output $snapshotRoot --max-minutes 60
```

- `--output` must be an absolute directory on a local fixed drive, outside any Git checkout, with no reparse points.
- `--page-size 1..5000` sets the iterator page size (default 5000).
- `--max-minutes` requests cooperative cancellation. An in-flight SDK call may not return promptly; a cancelled run never publishes a partial snapshot.
- Do not run two exports at once, and avoid running while another integration writes to the same company file. QuickBooks has been observed to raise an internal fatal error when an integration writes during the exporter's read.

A snapshot is published as a directory named by its 32-character snapshot ID only after extraction, validation, and cleanup succeed. `.staging-*` directories are incomplete. Verify a snapshot independently at any time:

```powershell
& $exe verify --snapshot "$snapshotRoot\<snapshot-id>"
```

Snapshots contain your complete accounting data. Keep them private and out of Git. A snapshot is a consistent read of each dataset, not a point-in-time backup: QuickBooks stays writable during extraction. See [EXPORT-CONTRACT.md](EXPORT-CONTRACT.md) for the file format and interpretation rules.

## Load into PostgreSQL

The loader reads its connection string only from the environment, so it never appears in arguments or files.

```powershell
$env:QBD_POSTGRES_CONNECTION_STRING = 'Host=<host>;Port=5432;Database=<database>;Username=<role>;Password=<password>;SSL Mode=Require'
$env:QBD_POSTGRES_SCHEMA = 'quickbooks_ingest'   # optional; this is the default

& $exe postgres probe     # connectivity check
& $exe postgres init      # create or migrate the schema
& $exe postgres load --snapshot "$snapshotRoot\<snapshot-id>" --dry-run
& $exe postgres load --snapshot "$snapshotRoot\<snapshot-id>" --allow-unavailable Currency,Lead
& $exe postgres status
```

`load` re-verifies the snapshot and requires `--allow-unavailable` to list exactly the datasets that snapshot could not capture (omit it when everything was available). This makes coverage gaps an explicit decision. If the list does not match, the loader exits 9 and prints the snapshot's actual set as `SnapshotUnavailable`, so on a new company file you can run a `--dry-run` without the option to discover it.

Loads are idempotent: reloading the same snapshot is a no-op. Snapshots must be loaded in completion order; an older snapshot arriving after a newer one is recorded as a delivery issue and rejected with exit 14.

Read the records captured by any loaded snapshot:

```sql
SELECT * FROM quickbooks_ingest.landing_record_as_of('<snapshot-uuid>'::uuid);
```

Consult `snapshot_entity` for per-dataset coverage; an unavailable dataset is not an empty one. [POSTGRES-LOAD-PLAN.md](POSTGRES-LOAD-PLAN.md) documents the schema, versioning, and the interface for downstream consumers.

## Unattended nightly operation

`Install-QuickBooksExporter.ps1` builds a versioned release under `%LOCALAPPDATA%\QuickBooksExporter`, stores the Postgres connection string with Windows DPAPI for the current user, and registers a Windows scheduled task. Run it from a normal PowerShell window as the Windows user that runs QuickBooks.

```powershell
.\Install-QuickBooksExporter.ps1 -CompanyFile 'C:\path\to\company.qbw' -AllowedUnavailableEntities Currency,Lead -DailyAt '02:00' -WindowMinutes 180
```

- `-AllowedUnavailableEntities` is the fixed unavailable-dataset policy passed to every load. Find your set by running one manual export and a `postgres load --dry-run` as described above. Re-running the installer keeps the saved value unless you pass a new one.
- If `-CompanyFile` is omitted and exactly one `.qbw` sits directly in the repository's parent directory, that file is used; otherwise you are prompted.
- The task wakes every 15 minutes inside the daily window, retries unloaded deliveries first, and makes at most one successful export per window date with at most three attempts. It requires the company file to exist and 5 GiB free on the snapshot drive. It does not catch up outside the window.
- There is no forced timeout: a hung SDK call can block until someone intervenes.

Related scripts:

| Script | Purpose |
| --- | --- |
| `Update-QuickBooksExporter.ps1` | Build and install a new release, keeping configuration, credential, state, and logs |
| `Get-QuickBooksExporterStatus.ps1` | Show the installed release and task state; `-ProbePostgres` checks the database |
| `Test-ScheduledExport.ps1` | No-data canary that runs the coordinator in scheduled-task context |
| `Uninstall-QuickBooksExporter.ps1` | Remove the task and releases; `-Purge` also removes configuration and data |

The coordinator writes sanitized JSONL logs under `%LOCALAPPDATA%\QuickBooksExporter\logs`. It sends no alerts; monitor freshness downstream (for example from `delivery_receipt` and `delivery_issue`).

## Testing

`build.ps1` runs the C# self-tests and the mocked scheduler tests. To include the PostgreSQL acceptance tests, point `QBD_POSTGRES_TEST_CONNECTION_STRING` at an isolated, disposable PostgreSQL 16 instance on loopback. The tests create and drop random schemas using synthetic data and never read `QBD_POSTGRES_CONNECTION_STRING`.

## Project notes

[DEVELOPMENT-STATUS.md](DEVELOPMENT-STATUS.md) summarizes current capability and known limitations. [AGENTS.md](AGENTS.md) holds contributor and coding-agent workflow rules, including the privacy rules for issues and notes.

## License

[MIT](LICENSE)
