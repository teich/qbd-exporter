using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Npgsql;
using NpgsqlTypes;

namespace QuickBooksProbe;

// Opt-in integration acceptance, using only synthetic fixtures and random schemas.
// The separate test connection must point at an isolated loopback PostgreSQL server.
internal static class PostgresVersioningTests
{
    internal static async Task Run(Action<string, bool> check, string connectionString, SnapshotPlan verified)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidDataException("Postgres tests require an isolated loopback database.");
        settings.IncludeErrorDetail = false;
        await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
        string prefix = "qbd_test_" + Guid.NewGuid().ToString("N");
        string[] schemas = [prefix, prefix + "_migration", prefix + "_broken", prefix + "_verified"];
        string directory = Path.Combine(Path.GetTempPath(), prefix);
        Directory.CreateDirectory(directory);
        try
        {
            await PostgresLoader.EnsureSchema(source, schemas[3]);
            await PostgresLoader.Load(source, schemas[3], verified, commit: false);
            check("postgres verified full dry run leaves no receipt", await Scalar(source, $"SELECT count(*) FROM {schemas[3]}.delivery_receipt") == 0);
            await PostgresLoader.Load(source, schemas[3], verified);
            await AssertSnapshot(check, source, schemas[3], verified);

            string schema = schemas[0];
            await PostgresLoader.EnsureSchema(source, schema);
            await PostgresLoader.EnsureSchema(source, schema);
            var plans = new[]
            {
                Fixture(directory, 1, [("a", "one"), ("b", "two")], [("c", "customer")]),
                Fixture(directory, 2, [("a", "one"), ("b", "two")], [("c", "customer")], reverseFields: true),
                Fixture(directory, 3, [("a", "changed"), ("d", "new")], null),
                Fixture(directory, 4, [], [("c", "customer")]),
                Fixture(directory, 5, [("b", "two")], []),
                Fixture(directory, 6, null, null, omitCustomer: true),
                Fixture(directory, 7, [("b", "two")], [("c", "customer")])
            };
            long[] versions = [3, 3, 5, 5, 6, 6, 7];
            (long Inserted, long Closed)[][] churn =
            [
                [(2, 0), (1, 0)], [(0, 0), (0, 0)], [(2, 2), (0, 0)],
                [(0, 2), (0, 0)], [(1, 0), (0, 1)], [(0, 0)], [(0, 0), (1, 0)]
            ];
            for (int i = 0; i < plans.Length; i++)
            {
                await PostgresLoader.Load(source, schema, plans[i]);
                await AssertChurn(check, source, schema, plans[i], churn[i]);
                check("postgres retains only changed versions " + i, await Scalar(source, $"SELECT count(*) FROM {schema}.landing_record") == versions[i]);
                // Recheck every historical result after subsequent updates, not just the current state.
                foreach (var prior in plans.Take(i + 1)) await AssertSnapshot(check, source, schema, prior);
            }
            check("postgres reappearance creates a fresh interval", await Scalar(source, $"SELECT count(*) FROM {schema}.landing_record WHERE record_id = 'AccountRet:b'") == 2);
            check("postgres missing entity and unavailable interval retain open state", await Scalar(source, $"SELECT count(*) FROM {schema}.landing_record WHERE record_id = 'AccountRet:b' AND valid_from_seq = 5 AND valid_to_seq IS NULL") == 1);

            var retry = JsonSerializer.Serialize(await PostgresLoader.Load(source, schema, plans[0]));
            check("postgres old matching receipt retries before order check", retry.Contains("already-loaded"));
            await AssertChurn(check, source, schema, plans[0], churn[0]);
            await Reject(check, "postgres conflicting fingerprint rejected", () => PostgresLoader.Load(source, schema, CopyPlan(plans[0], fingerprint: new string('f', 64))));
            await Reject(check, "postgres older unseen snapshot rejected", () => PostgresLoader.Load(source, schema, Fixture(directory, 0, [], [])));
            await Reject(check, "postgres equal completion time rejected", () => PostgresLoader.Load(source, schema, Fixture(directory, 7, [], [])));
            check("postgres rejected deliveries are visible without receipts", await Scalar(source, $"SELECT count(*) FROM {schema}.delivery_issue WHERE acknowledged_at IS NULL") == 2
                && await Scalar(source, $"SELECT count(*) FROM {schema}.delivery_receipt") == 7);
            var late = Fixture(directory, 0, [], []);
            await Reject(check, "postgres late dry run rejected", () => PostgresLoader.Load(source, schema, late, commit: false));
            check("postgres late dry run does not persist an issue", await Scalar(source, $"SELECT count(*) FROM {schema}.delivery_issue") == 2);
            for (int i = 0; i < 2; i++)
                await Reject(check, "postgres repeated late rejection", () => PostgresLoader.Load(source, schema, late));
            check("postgres late issue is idempotent", await Scalar(source, $"SELECT count(*) FROM {schema}.delivery_issue") == 3);
            await Reject(check, "postgres issue fingerprint conflict rejected", () => PostgresLoader.Load(source, schema, CopyPlan(late, new string('f', 64))));
            check("postgres status reports review backlog", JsonSerializer.Serialize(await PostgresLoader.Status(source, schema)).Contains("\"DeliveriesNeedingReview\":3"));
            var next = Fixture(directory, 8, [("b", "dry-run-change")], []);
            string before = await State(source, schema);
            await PostgresLoader.Load(source, schema, next, commit: false);
            check("postgres dry run rolls back versions closures and receipt", before == await State(source, schema));

            // A pairing failure occurs after the receipt and entity evidence have been written.
            var malformed = Fixture(directory, 9, [("b", "bad-pair")], []);
            File.WriteAllText(Path.Combine(malformed.Directory, "Account.jsonl"), "{}");
            await Reject(check, "postgres failed row pairing rolls back", () => PostgresLoader.Load(source, schema, malformed));
            check("postgres failed load leaves history untouched", before == await State(source, schema));

            // A held history lock must block a different snapshot before it writes a receipt.
            await using (var blocker = await source.OpenConnectionAsync())
            await using (var transaction = await blocker.BeginTransactionAsync())
            {
                await using var held = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", blocker, transaction);
                held.Parameters.AddWithValue("key", "qbd-history:" + schema);
                await held.ExecuteNonQueryAsync();
                Task<object> waiting = PostgresLoader.Load(source, schema, next);
                bool observedWait = false;
                for (int i = 0; i < 100 && !waiting.IsCompleted; i++)
                {
                    if (await Scalar(source, "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted") > 0)
                    { observedWait = true; break; }
                    await Task.Delay(20);
                }
                await transaction.RollbackAsync();
                await waiting;
                check("postgres serializes distinct snapshots", observedWait);
            }
            var concurrent = Fixture(directory, 10, [("b", "concurrent")], []);
            var results = await Task.WhenAll(PostgresLoader.Load(source, schema, concurrent), PostgresLoader.Load(source, schema, concurrent));
            check("postgres concurrent matching retry commits once", results.Count(r => JsonSerializer.Serialize(r).Contains("already-loaded")) == 1);
            await AssertSnapshot(check, source, schema, concurrent);
            foreach (var prior in plans) await AssertSnapshot(check, source, schema, prior);
            check("postgres status exposes physical version count", JsonSerializer.Serialize(await PostgresLoader.Status(source, schema)).Contains("RecordVersions"));

            await RejectSql(check, source, schema, "postgres one open version per key", $"INSERT INTO {schema}.landing_record SELECT entity, record_id, record_type, data, valid_from_seq - 1, NULL FROM {schema}.landing_record WHERE valid_to_seq IS NULL LIMIT 1");
            await RejectSql(check, source, schema, "postgres interval end must exceed start", $"UPDATE {schema}.landing_record SET valid_to_seq = valid_from_seq WHERE valid_to_seq IS NULL");
            await MergeFailure(check, source, schema, concurrent, duplicate: false);
            await MergeFailure(check, source, schema, concurrent, duplicate: true);

            await SeedLegacy(source, schemas[1], plans, incomplete: false);
            await PostgresLoader.EnsureSchema(source, schemas[1]);
            check("postgres migration compacts historical duplicates", await Scalar(source, $"SELECT count(*) FROM {schemas[1]}.landing_record") == 7);
            foreach (var plan in plans) await AssertSnapshot(check, source, schemas[1], plan);
            check("postgres migration retains receipts and metadata", await Scalar(source, $"SELECT count(*) FROM {schemas[1]}.delivery_receipt") == plans.Length);
            for (int i = 0; i < plans.Length; i++) await AssertChurn(check, source, schemas[1], plans[i], churn[i]);
            // Restore the v3 storage shape to exercise upgrading an already-versioned history.
            await Execute(source, $"ALTER TABLE {schemas[1]}.snapshot_entity DROP COLUMN versions_inserted, DROP COLUMN versions_closed; DROP TABLE {schemas[1]}.delivery_issue; DELETE FROM {schemas[1]}.loader_schema WHERE version = 4; CREATE INDEX landing_record_as_of_idx ON {schemas[1]}.landing_record(entity, valid_from_seq, valid_to_seq)");
            await PostgresLoader.EnsureSchema(source, schemas[1]);
            for (int i = 0; i < plans.Length; i++) await AssertChurn(check, source, schemas[1], plans[i], churn[i]);
            check("postgres v4 removes redundant historical index", await Scalar(source, $"SELECT count(*) FROM pg_indexes WHERE schemaname = '{schemas[1]}' AND indexname = 'landing_record_as_of_idx'") == 0);
            await PostgresLoader.Load(source, schemas[1], next);
            await AssertSnapshot(check, source, schemas[1], next);
            await PostgresLoader.EnsureSchema(source, schemas[1]);

            await SeedLegacy(source, schemas[2], plans.Take(1).ToArray(), incomplete: true);
            await Reject(check, "postgres incomplete legacy history blocks migration", () => PostgresLoader.EnsureSchema(source, schemas[2]));
            check("postgres migration failure preserves legacy schema", await Scalar(source, $"SELECT max(version) FROM {schemas[2]}.loader_schema") == 2);
            check("postgres migration failure preserves legacy rows", await Scalar(source, $"SELECT count(*) FROM {schemas[2]}.landing_record WHERE source_xml IS NOT NULL") == 2);
        }
        finally
        {
            // These names are generated above; never use configured ingest schema names here.
            foreach (string schema in schemas) await Execute(source, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SnapshotPlan Fixture(string root, int day, (string Id, string Value)[]? accounts, (string Id, string Value)[]? customers, bool reverseFields = false, bool omitCustomer = false)
    {
        Guid id = Guid.NewGuid();
        string directory = Path.Combine(root, id.ToString("N"));
        Directory.CreateDirectory(directory);
        var entities = new List<EntityPlan>();
        foreach (var (name, rows) in new[] { ("Account", accounts), ("Customer", customers) })
        {
            if (name == "Customer" && omitCustomer) continue;
            var spec = QueryCatalog.All.Single(x => x.Name == name);
            entities.Add(new(name, rows?.Length, null, rows is null ? "unavailable" : "available", rows is null ? 3250 : null, spec));
            if (rows is null) continue;
            var records = rows.Select(r => new XElement(name + "Ret", reverseFields
                ? new[] { new XElement("Name", r.Value), new XElement("ListID", r.Id) }
                : new[] { new XElement("ListID", r.Id), new XElement("Name", r.Value) })).ToArray();
            new XElement("Records", records).Save(Path.Combine(directory, name + ".xml"));
            File.WriteAllLines(Path.Combine(directory, name + ".jsonl"), records.Select(r => FullExport.JsonRecord(spec, r)), new UTF8Encoding(false));
        }
        string manifest = JsonSerializer.Serialize(new { Synthetic = true, Id = id });
        return new SnapshotPlan
        {
            Directory = directory, SnapshotId = id, ManifestJson = manifest,
            ManifestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant(),
            FormatVersion = 2, CatalogVersion = QueryCatalog.Version, CompletedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day),
            Entities = entities.ToArray()
        };
    }

    private static SnapshotPlan CopyPlan(SnapshotPlan plan, string fingerprint) => new()
    {
        Directory = plan.Directory, SnapshotId = plan.SnapshotId, ManifestJson = plan.ManifestJson,
        ManifestSha256 = fingerprint, FormatVersion = plan.FormatVersion, CatalogVersion = plan.CatalogVersion,
        CompletedUtc = plan.CompletedUtc, Entities = plan.Entities
    };

    private static async Task AssertSnapshot(Action<string, bool> check, NpgsqlDataSource source, string schema, SnapshotPlan plan)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM {schema}.landing_record_as_of(@id)", connection);
        count.Parameters.AddWithValue("id", plan.SnapshotId);
        check("postgres as-of snapshot total", (long)(await count.ExecuteScalarAsync())! == plan.RecordCount);
        foreach (var entity in plan.Entities)
        foreach (var row in plan.ReadRows(entity))
        {
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {schema}.landing_record_as_of(@id) WHERE entity = @entity AND record_id = @record AND record_type = @type AND data = @data", connection);
            command.Parameters.AddWithValue("id", plan.SnapshotId);
            command.Parameters.AddWithValue("entity", entity.Name);
            command.Parameters.AddWithValue("record", row.RecordId);
            command.Parameters.AddWithValue("type", row.RecordType);
            command.Parameters.AddWithValue("data", NpgsqlDbType.Jsonb, row.DataJson);
            check("postgres as-of exact payload", (long)(await command.ExecuteScalarAsync())! == 1);
        }
    }

    private static async Task AssertChurn(Action<string, bool> check, NpgsqlDataSource source, string schema, SnapshotPlan plan, (long Inserted, long Closed)[] expected)
    {
        await using var command = source.CreateCommand($"SELECT versions_inserted, versions_closed FROM {schema}.snapshot_entity WHERE snapshot_id = @id ORDER BY entity");
        command.Parameters.AddWithValue("id", plan.SnapshotId);
        await using var reader = await command.ExecuteReaderAsync();
        foreach (var counts in expected)
            check("postgres per-entity inserted and closed counts", await reader.ReadAsync() && reader.GetInt64(0) == counts.Inserted && reader.GetInt64(1) == counts.Closed);
        check("postgres churn includes exactly the captured catalog", !await reader.ReadAsync());
    }

    private static async Task SeedLegacy(NpgsqlDataSource source, string schema, SnapshotPlan[] plans, bool incomplete)
    {
        await Execute(source, $$"""
            CREATE SCHEMA {{schema}};
            CREATE TABLE {{schema}}.loader_schema (version integer PRIMARY KEY, installed_at timestamptz DEFAULT now());
            INSERT INTO {{schema}}.loader_schema(version) VALUES (1), (2);
            CREATE TABLE {{schema}}.delivery_receipt (
                snapshot_id uuid PRIMARY KEY, manifest_sha256 text NOT NULL, manifest jsonb NOT NULL,
                format_version integer NOT NULL, catalog_version integer NOT NULL, export_mode text NOT NULL,
                entity_count integer NOT NULL, record_count bigint NOT NULL, source_completed_utc timestamptz NOT NULL,
                loaded_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE {{schema}}.snapshot_entity (
                snapshot_id uuid NOT NULL REFERENCES {{schema}}.delivery_receipt, entity text NOT NULL, availability text NOT NULL,
                unavailable_status_code integer, declared_records bigint, loaded_records bigint NOT NULL, record_digest text,
                PRIMARY KEY(snapshot_id, entity));
            CREATE TABLE {{schema}}.landing_record (
                snapshot_id uuid NOT NULL, entity text NOT NULL, record_id text NOT NULL, record_type text NOT NULL,
                data jsonb NOT NULL, source_xml text, PRIMARY KEY(snapshot_id, entity, record_id),
                FOREIGN KEY(snapshot_id, entity) REFERENCES {{schema}}.snapshot_entity);
            CREATE INDEX landing_record_entity_type_idx ON {{schema}}.landing_record(entity, record_type, snapshot_id);
            """);
        await using var connection = await source.OpenConnectionAsync();
        // Deliberately seed in reverse insertion order to check chronological migration.
        foreach (var plan in plans.Reverse())
        {
            await using var receipt = new NpgsqlCommand($"INSERT INTO {schema}.delivery_receipt VALUES (@id, @hash, @manifest, 2, 13, 'full', @entities, @records, @completed, now())", connection);
            receipt.Parameters.AddWithValue("id", plan.SnapshotId);
            receipt.Parameters.AddWithValue("hash", plan.ManifestSha256);
            receipt.Parameters.AddWithValue("manifest", NpgsqlDbType.Jsonb, plan.ManifestJson);
            receipt.Parameters.AddWithValue("entities", plan.EntityCount);
            receipt.Parameters.AddWithValue("records", plan.RecordCount);
            receipt.Parameters.AddWithValue("completed", plan.CompletedUtc);
            await receipt.ExecuteNonQueryAsync();
            foreach (var entity in plan.Entities)
            {
                await using var evidence = new NpgsqlCommand($"INSERT INTO {schema}.snapshot_entity VALUES (@id, @entity, @availability, @status, @declared, @loaded, NULL)", connection);
                evidence.Parameters.AddWithValue("id", plan.SnapshotId);
                evidence.Parameters.AddWithValue("entity", entity.Name);
                evidence.Parameters.AddWithValue("availability", entity.Availability);
                evidence.Parameters.AddWithValue("status", (object?)entity.UnavailableStatusCode ?? DBNull.Value);
                evidence.Parameters.AddWithValue("declared", (object?)entity.Records ?? DBNull.Value);
                evidence.Parameters.AddWithValue("loaded", entity.Records ?? 0);
                await evidence.ExecuteNonQueryAsync();
                foreach (var row in plan.ReadRows(entity))
                {
                    if (incomplete && entity.Name == "Customer") continue;
                    await using var insert = new NpgsqlCommand($"INSERT INTO {schema}.landing_record VALUES (@id, @entity, @record, @type, @data, '<synthetic/>')", connection);
                    insert.Parameters.AddWithValue("id", plan.SnapshotId);
                    insert.Parameters.AddWithValue("entity", entity.Name);
                    insert.Parameters.AddWithValue("record", row.RecordId);
                    insert.Parameters.AddWithValue("type", row.RecordType);
                    insert.Parameters.AddWithValue("data", NpgsqlDbType.Jsonb, row.DataJson);
                    await insert.ExecuteNonQueryAsync();
                }
            }
        }
    }

    private static async Task MergeFailure(Action<string, bool> check, NpgsqlDataSource source, string schema, SnapshotPlan current, bool duplicate)
    {
        string before = await State(source, schema);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PostgresLoader.CreateStaging(connection, transaction);
        await using var command = new NpgsqlCommand(duplicate
            ? "INSERT INTO pg_temp.qbd_landing_stage VALUES ('Account', 'synthetic', 'AccountRet', '{}'), ('Account', 'synthetic', 'AccountRet', '{}')"
            : $$"""
                INSERT INTO {{schema}}.delivery_receipt
                SELECT @id, manifest_sha256, manifest, format_version, catalog_version, export_mode, entity_count,
                       record_count, source_completed_utc + interval '1 day', loaded_at, snapshot_seq + 1
                FROM {{schema}}.delivery_receipt WHERE snapshot_id = @current;
                INSERT INTO {{schema}}.snapshot_entity (snapshot_id, entity, availability, unavailable_status_code, declared_records, loaded_records, record_digest)
                SELECT @id, entity, availability, unavailable_status_code,
                    declared_records, loaded_records, record_digest FROM {{schema}}.snapshot_entity WHERE snapshot_id = @current;
                SELECT {{schema}}.merge_snapshot(@id);
                """, connection, transaction);
        if (!duplicate)
        {
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("current", current.SnapshotId);
        }
        bool rejected = false;
        try { await command.ExecuteNonQueryAsync(); }
        catch (PostgresException ex) when (ex.SqlState is "23514" or "23505") { rejected = true; }
        await transaction.RollbackAsync();
        check(duplicate ? "postgres staging rejects duplicate keys" : "postgres merge rejects incomplete staging", rejected);
        check("postgres rejected staging preserves history", before == await State(source, schema));
    }

    private static async Task RejectSql(Action<string, bool> check, NpgsqlDataSource source, string schema, string name, string sql)
    {
        string before = await State(source, schema);
        await Reject(check, name, () => Execute(source, sql));
        check("postgres constraint failure leaves history intact", before == await State(source, schema));
    }

    private static async Task Reject(Action<string, bool> check, string name, Func<Task> action)
    {
        bool rejected = false;
        try { await action(); }
        catch (Exception ex) when (ex is InvalidDataException or PostgresException or KeyNotFoundException or PostgresLoader.SnapshotOrderException) { rejected = true; }
        check(name, rejected);
    }

    private static async Task<string> State(NpgsqlDataSource source, string schema)
    {
        await using var command = source.CreateCommand($"SELECT jsonb_build_array((SELECT jsonb_agg(r ORDER BY snapshot_seq) FROM {schema}.delivery_receipt r), (SELECT jsonb_agg(e ORDER BY snapshot_id, entity) FROM {schema}.snapshot_entity e), (SELECT jsonb_agg(v ORDER BY entity, record_id, valid_from_seq) FROM {schema}.landing_record v))::text");
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> Scalar(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task Execute(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
