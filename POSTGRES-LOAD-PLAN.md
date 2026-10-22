# Postgres load contract

The format-2 Postgres loader is implemented as separate `postgres probe`, `postgres init`, `postgres status`, and `postgres load` commands. It uses Npgsql and a versioned schema; it does not connect to QuickBooks. A local PowerShell coordinator and Windows Task Scheduler installer provide unattended nightly operation.

The loader consumes only published snapshots that pass the complete `QuickBooksProbe verify` gate and requires full mode. Every load must provide an unavailable-entity allowlist that exactly matches the manifest, including an empty list when every entity is available. This makes the policy explicit per delivery and rejects omissions, wildcards, and stale extra names. Local export and verification finish before database writes. A database outage cannot prevent a due local export, and a network failure does not require repeating a completed QuickBooks extraction.

Schedule exports when QuickBooks is lightly used and no other integration writes. New revision13/format2 exports use one read, which provides no change-detection evidence between datasets and no transaction isolation. Extraction, pagination, identity, and file-integrity checks are retained.

## Versioned landing (schema version 4)

Each receipt has a UUID, monotonically increasing `snapshot_seq`, source completion time, full manifest, and raw-manifest SHA-256 fingerprint. `snapshot_entity` retains availability, status, counts, and digest evidence for every catalog entity in every snapshot. `loaded_records` counts observed rows, including unchanged versions, not newly inserted physical rows. Scalar values remain strings; projections and business rules belong to downstream consumers. One schema represents one source company/history.

`landing_record` stores `(entity, record_id, record_type, data, valid_from_seq, valid_to_seq)`. The end is exclusive; NULL means the last observed version remains open. The primary key is `(entity, record_id, valid_from_seq)`; a partial unique index on `(entity, record_id) WHERE valid_to_seq IS NULL` enforces one open version. A check requires end > start; both endpoints reference retained receipts. The extra `(entity, valid_from_seq, valid_to_seq)` index is removed in v4: broad entity reads still visit most rows, and the primary key already supports entity filtering. Selective historical workloads may justify a different measured index later.

The loader holds a schema-wide transaction advisory lock before checking receipts or ordering. It allocates the next sequence under that lock, writes receipt/entity evidence, and binary-COPYs verified rows into a session-local temporary table with unique `(entity, record_id)` keys and `ON COMMIT DROP`. Staging is disposable; receipts and versions remain logged. All snapshot changes commit in one transaction.

`merge_snapshot(snapshot_id)` is an internal loader operation requiring that lock, receipt, entity evidence, and complete temporary staging in the same transaction. It validates total/per-entity counts, rejects rows for unknown/unavailable entities, closes changed or missing records for successfully captured entities, inserts new versions, and checks reconstructed counts. Equality compares the complete JSONB value and record type directly, avoiding hash collisions and changes caused only by object key order. Arrays and scalar values remain significant. Identity uses the export's opaque keys; payload-derived identities may yield disappearance/new-record pairs, which remain exactly reconstructable.

A successfully captured empty entity closes all its open records. Unavailable entities and entities absent from the catalog do not imply deletion. Their previous versions remain open internally, but the reader hides them for snapshots without coverage. A disappeared record returning later gets a new interval even if its payload matches an older closed version.

Matching receipt retries return `already-loaded` before checking order; conflicting fingerprints fail. An unseen snapshot must complete strictly later than every loaded receipt; equal timestamps are rejected too. Pending deliveries sort by manifest completion time. A late delivery records an idempotent `delivery_issue` with UUID, manifest fingerprint, completion time, reason, first/last detection times, and nullable `acknowledged_at`; it creates no receipt. The CLI returns exit 14 and JSON reason `snapshot-out-of-order`, `Retryable=false`. Conflicting fingerprints remain ordinary failures, not quarantined successes. A dry run rolls back the issue too.

The coordinator handles exit 14 by preserving the export, writing `config/state/needs-review/<snapshot-id>.json`, emitting `load-needs-review`, and continuing to newer deliveries. Later heartbeats skip that distinct marker; it is never treated as a delivered receipt. Transient failures still stop the ordered queue for retry. Database `status` counts unacknowledged issues as `DeliveriesNeedingReview`; the local status script counts review markers. Health reporting must show these issues independently of freshness, since later successful loads do not fill the missing snapshot.

An operator can acknowledge the database issue by setting `acknowledged_at`, but acknowledgment does not load the missing snapshot or erase the local marker. Historical backfill into an existing sequence is not supported: replay retained exports in source order into a fresh schema. If intentionally retargeting the coordinator to a rebuilt schema, reconcile both delivered and needs-review markers against that destination before resuming. Do not simply retry an unchanged late snapshot against the same history, and do not prune receipts, entity evidence, or versions for analytics retention.

`--dry-run` exercises staging, merge, and checks and rolls back the load transaction, including interval closures and churn counters. Schema initialization/migration is a separate transaction performed first, so a dry run can still upgrade the schema. `status` exposes `RecordVersions` (all physical versions), `OpenVersions` (last observed open versions, including coverage gaps), and `DeliveriesNeedingReview`, alongside receipt/entity counts.

## Reading and rebuilding history

Use `landing_record_as_of(snapshot_id uuid)` to obtain `(entity, record_id, record_type, data)` for a pinned receipt:

```sql
SELECT v.*
FROM quickbooks_ingest.delivery_receipt s
CROSS JOIN LATERAL quickbooks_ingest.landing_record_as_of(s.snapshot_id) v
WHERE s.snapshot_seq = 42;
```

The function applies `valid_from_seq <= snapshot_seq AND (valid_to_seq IS NULL OR valid_to_seq > snapshot_seq)` and joins the receipt's available entity evidence. Retain `snapshot_entity` as the coverage source: no rows for an unavailable entity are not proof of emptiness. Open versions alone are not a reliable current-state source when an entity becomes unavailable. Unknown snapshot IDs return no rows; validate the receipt when choosing a pin.

To rebuild a historical downstream fact, enumerate receipts and evaluate each as-of state, retaining each receipt's ID/time in the output. Unchanged snapshots still represent distinct observation dates despite sharing stored versions. This reconstructs captured JSONB states, not an atomic company backup or events between exports. Downstream models are outside this repository and must adopt the interface separately.

## Warehouse handoff

`snapshot_entity.versions_inserted` and `versions_closed` are nonnegative bigint counts recorded by the merge's actual INSERT/UPDATE RETURNING rows in the receipt transaction. A changed key usually increments both; deletion increments closed only; a new or reappearing key increments inserted only. Unchanged and unavailable entities have zero counts, but their coverage statuses remain different. Retry leaves the original counters intact. Migrating from v3 backfills exact counts from historical interval endpoints.

A downstream pipeline-health model should expose these fields by entity and receipt, together with `loaded_records`, coverage, and whether the entity is being seeded for the first time. For available nonempty entities, `versions_inserted::numeric / NULLIF(loaded_records, 0)` reports the fraction of current rows newly versioned; initial seeding is expected to be 100%. Keep `versions_closed` visible even for empty captures, and keep unavailable rates NULL. Include an independent count of `delivery_issue WHERE acknowledged_at IS NULL` so a healthy latest delivery does not hide a missing historical snapshot.

Pin one committed receipt for the entire warehouse run. Under this repository's no-pruning/no-history-rewrite contract, later merges cannot change the data returned for that pin. Readers should not acquire `FOR SHARE` on version rows: those locks block the loader from closing versions. A receipt-only `FOR SHARE OF r` may protect the pin; it is not protection against arbitrary manual child-row deletion.

## Upgrade and acceptance

Migration from v1/v2 runs transactionally under the history lock. It assigns sequences by source completion time with UUID as a tie-breaker for existing receipts, replays every historical snapshot, and compares both directions of the complete reconstructed record set against the original. Only then does it drop the per-snapshot table and record schema version 3. Receipts, manifests, entity evidence, and snapshot IDs are retained. Legacy PostgreSQL `source_xml` copies are removed with the old table; verified local XML/JSON exports are untouched and remain authoritative.

Migration from v1/v2 needs space for old and new storage concurrently. It rolls back if old landing rows were pruned, validation fails, or downstream dependencies prevent dropping the old table. It does not cascade away downstream objects or fabricate history. The subsequent v4 step backfills counters, installs delivery-issue storage, updates the merge function, and drops the extra historical index atomically. Restore/replay incomplete history into a fresh schema. Stop old loader binaries before upgrading and deploy the v4 loader/coordinator together. Automatic historical backfill and retention pruning remain deferred.

Optional PostgreSQL acceptance runs with `--self-test` when `QBD_POSTGRES_TEST_CONNECTION_STRING` targets an isolated loopback PostgreSQL 16 instance. Tests create and clean up random schemas, use synthetic fixtures, and never use the operational connection variable. Coverage includes a verified full export, exact historical reconstruction, JSON key ordering, changes, deletion, reappearance, unavailable/absent entities, retries/conflicts, chronology, rollback, concurrency, constraints, complete migration, and failed migration rollback.

## Unattended operation

The local coordinator runs export and load separately under the logged-in interactive Windows account that can access QuickBooks. If the configured company file is missing it skips the export. QuickBooks may be started by the SDK when the integrated application is allowed to log in automatically; an unavailable session is logged and retried on the next heartbeat. Pending deliveries resume without QuickBooks. The scheduled task ignores overlapping triggers, and the exporter retains its process and output locks. Hard-timeout COM recovery is not certified, so the task has no forced execution timeout.

The connection string is supplied to the loader through process-local `QBD_POSTGRES_CONNECTION_STRING`; the installer protects it for the current Windows user with DPAPI, and it is not accepted as a command argument or stored in the repository. `QBD_POSTGRES_SCHEMA` defaults to `quickbooks_ingest`. Use PostgreSQL TLS (`SSL Mode=Require` or stricter) unless the database is reachable only over a private, encrypted network.

The scheduler wakes every 15 minutes, while the coordinator acts only in a bounded 02:00-05:00 local-time window by default. A successful export marker prevents duplicate exports for that window date; a reboot or transient failure can retry during the remaining window without waiting another day. It does not catch up later during business hours. Pending full-format deliveries completed after the persisted installation cutoff retry before any new export on each in-window attempt; the cutoff excludes older test and historical artifacts and survives reinstall. The coordinator emits sanitized local JSONL events but no freshness alerts because downstream dashboard monitoring owns that responsibility. Still open: snapshot retention and an explicit administrative reload procedure after downstream pruning. Routine load deliberately remains receipt-idempotent.
