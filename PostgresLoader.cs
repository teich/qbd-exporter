using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Npgsql;
using NpgsqlTypes;

namespace QuickBooksProbe;

internal static partial class PostgresLoader
{
    private const string ConnectionVariable = "QBD_POSTGRES_CONNECTION_STRING";
    private const string SchemaVariable = "QBD_POSTGRES_SCHEMA";
    private const int SchemaVersion = 4;
    internal const int SnapshotOrderExitCode = 14;

    internal sealed class SnapshotOrderException : Exception;

    // Entity names come from the fixed catalog, so reporting them reveals no company data.
    internal sealed class UnavailablePolicyException(string[] unavailable) : Exception
    {
        internal string[] Unavailable { get; } = unavailable;
    }

    internal static async Task<int> Command(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("probe" or "init" or "status" or "load"))
        {
            Usage();
            return 2;
        }

        string stage = "configuration";
        try
        {
            var settings = Settings.FromEnvironment();
            await using var dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
            switch (args[0])
            {
                case "probe" when args.Length == 1:
                    stage = "connect";
                    var probe = await Probe(dataSource, settings.Schema);
                    Console.WriteLine(JsonSerializer.Serialize(probe));
                    return 0;
                case "init" when args.Length == 1:
                    stage = "schema";
                    await EnsureSchema(dataSource, settings.Schema);
                    Console.WriteLine(JsonSerializer.Serialize(new { Success = true, Stage = "schema-ready", SchemaVersion }));
                    return 0;
                case "status" when args.Length == 1:
                    stage = "status";
                    Console.WriteLine(JsonSerializer.Serialize(await Status(dataSource, settings.Schema)));
                    return 0;
                case "load":
                    stage = "arguments";
                    var options = LoadOptions.Parse(args[1..]);
                    stage = "snapshot-verify";
                    var plan = SnapshotPlan.Read(options.Snapshot, options.AllowedUnavailable);
                    stage = "schema";
                    await EnsureSchema(dataSource, settings.Schema);
                    stage = "load";
                    var result = await Load(dataSource, settings.Schema, plan, commit: !options.DryRun);
                    Console.WriteLine(JsonSerializer.Serialize(result));
                    return 0;
                default:
                    Usage();
                    return 2;
            }
        }
        catch (SnapshotOrderException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { Success = false, Stage = "load", Reason = "snapshot-out-of-order", Retryable = false }));
            return SnapshotOrderExitCode;
        }
        catch (UnavailablePolicyException ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { Success = false, Stage = stage, Reason = "unavailable-policy-mismatch", SnapshotUnavailable = ex.Unavailable, Retryable = false }));
            return 9;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or JsonException or XmlException or NpgsqlException or TimeoutException)
        {
            string? sqlState = ex is PostgresException postgres ? postgres.SqlState : null;
            string? serverRoutine = ex is PostgresException server ? server.Routine : null;
            string errorChain = string.Join(">", ExceptionChain(ex).Select(x => x.GetType().Name + ":0x" + x.HResult.ToString("X8")));
            Console.Error.WriteLine(JsonSerializer.Serialize(new { Success = false, Stage = stage, ErrorType = ex.GetType().Name, SqlState = sqlState, ServerRoutine = serverRoutine, ErrorChain = errorChain }));
            return stage switch { "configuration" or "arguments" => 2, "snapshot-verify" => 9, "connect" => 11, "schema" => 12, _ => 13 };
        }
    }

    private static void Usage() => Console.Error.WriteLine(
        "Usage: QuickBooksProbe postgres probe | init | status | load --snapshot <absolute published snapshot directory> [--allow-unavailable <comma-separated entities>] [--dry-run]\n" +
        $"Set {ConnectionVariable}; optionally set {SchemaVariable} (default quickbooks_ingest). Connection secrets are never accepted on the command line.");

    internal static async Task<object> Probe(NpgsqlDataSource dataSource, string schema, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT current_setting('server_version'),
                   EXISTS (SELECT 1 FROM pg_stat_ssl WHERE pid = pg_backend_pid() AND ssl),
                   to_regnamespace(@schema) IS NOT NULL,
                   COALESCE(has_schema_privilege(current_user, @schema, 'USAGE'), false),
                   COALESCE(has_schema_privilege(current_user, @schema, 'CREATE'), false),
                   has_database_privilege(current_user, current_database(), 'CREATE')
            """;
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidDataException();
        return new
        {
            Success = true,
            Stage = "probe",
            ServerVersion = reader.GetString(0),
            Tls = reader.GetBoolean(1),
            SchemaExists = reader.GetBoolean(2),
            SchemaUsage = reader.GetBoolean(3),
            SchemaCreate = reader.GetBoolean(4),
            DatabaseCreate = reader.GetBoolean(5)
        };
    }

    internal static async Task EnsureSchema(NpgsqlDataSource dataSource, string schema, CancellationToken cancellationToken = default)
    {
        string q = Quote(schema);
        string sql = $$"""
            CREATE TABLE IF NOT EXISTS {{q}}.loader_schema (
                version integer PRIMARY KEY,
                installed_at timestamptz NOT NULL DEFAULT clock_timestamp()
            );
            INSERT INTO {{q}}.loader_schema(version) VALUES (1) ON CONFLICT DO NOTHING;
            CREATE TABLE IF NOT EXISTS {{q}}.delivery_receipt (
                snapshot_id uuid PRIMARY KEY,
                manifest_sha256 text NOT NULL CHECK (manifest_sha256 ~ '^[0-9a-f]{64}$'),
                manifest jsonb NOT NULL,
                format_version integer NOT NULL,
                catalog_version integer NOT NULL,
                export_mode text NOT NULL CHECK (export_mode = 'full'),
                entity_count integer NOT NULL CHECK (entity_count >= 0),
                record_count bigint NOT NULL CHECK (record_count >= 0),
                source_completed_utc timestamptz NOT NULL,
                loaded_at timestamptz NOT NULL DEFAULT clock_timestamp()
            );
            CREATE TABLE IF NOT EXISTS {{q}}.snapshot_entity (
                snapshot_id uuid NOT NULL REFERENCES {{q}}.delivery_receipt(snapshot_id),
                entity text NOT NULL,
                availability text NOT NULL,
                unavailable_status_code integer,
                declared_records bigint,
                loaded_records bigint NOT NULL CHECK (loaded_records >= 0),
                record_digest text,
                PRIMARY KEY (snapshot_id, entity),
                CHECK ((declared_records IS NULL) = (unavailable_status_code IS NOT NULL)),
                CHECK (declared_records IS NULL OR declared_records = loaded_records)
            );
            CREATE TABLE IF NOT EXISTS {{q}}.landing_record (
                snapshot_id uuid NOT NULL,
                entity text NOT NULL,
                record_id text NOT NULL,
                record_type text NOT NULL,
                data jsonb NOT NULL,
                source_xml text,
                PRIMARY KEY (snapshot_id, entity, record_id),
                FOREIGN KEY (snapshot_id, entity) REFERENCES {{q}}.snapshot_entity(snapshot_id, entity)
            );
            CREATE INDEX IF NOT EXISTS landing_record_entity_type_idx
                ON {{q}}.landing_record(entity, record_type, snapshot_id);
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockHistory(connection, transaction, schema, cancellationToken);
        await using (var exists = new NpgsqlCommand("SELECT to_regnamespace(@schema) IS NOT NULL", connection, transaction))
        {
            exists.Parameters.AddWithValue("schema", schema);
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                await using var create = new NpgsqlCommand($"CREATE SCHEMA {q}", connection, transaction);
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await using (var exists = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", connection, transaction))
        {
            exists.Parameters.AddWithValue("name", q + ".loader_schema");
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 120 };
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        int installedVersion;
        await using (var version = new NpgsqlCommand($"SELECT min(version), max(version), count(*) FROM {q}.loader_schema", connection, transaction))
        await using (var reader = await version.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != 1 || reader.GetInt64(2) != reader.GetInt32(1) || reader.GetInt32(1) > SchemaVersion)
                throw new InvalidDataException();
            installedVersion = reader.GetInt32(1);
        }
        if (installedVersion < 2)
        {
            await using var migrate = new NpgsqlCommand($"ALTER TABLE {q}.landing_record ALTER COLUMN source_xml DROP NOT NULL; INSERT INTO {q}.loader_schema(version) VALUES (2)", connection, transaction);
            await migrate.ExecuteNonQueryAsync(cancellationToken);
        }
        if (installedVersion < 3)
            await MigrateVersions(connection, transaction, q, cancellationToken);
        if (installedVersion < 4)
            await MigrateHealth(connection, transaction, q, cancellationToken);
        await using (var version = new NpgsqlCommand($"SELECT min(version), max(version), count(*) FROM {q}.loader_schema", connection, transaction))
        await using (var reader = await version.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != 1 || reader.GetInt32(1) != SchemaVersion || reader.GetInt64(2) != SchemaVersion)
                throw new InvalidDataException();
        }
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task<object> Status(NpgsqlDataSource dataSource, string schema, CancellationToken cancellationToken = default)
    {
        string q = Quote(schema);
        string sql = $"SELECT (SELECT max(version) FROM {q}.loader_schema), (SELECT count(*) FROM {q}.delivery_receipt), (SELECT count(*) FROM {q}.snapshot_entity), (SELECT count(*) FROM {q}.landing_record), (SELECT count(*) FROM {q}.landing_record WHERE valid_to_seq IS NULL), (SELECT count(*) FROM {q}.delivery_issue WHERE acknowledged_at IS NULL)";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidDataException();
        return new { Success = true, Stage = "status", SchemaVersion = reader.GetInt32(0), Receipts = reader.GetInt64(1), Entities = reader.GetInt64(2), RecordVersions = reader.GetInt64(3), OpenVersions = reader.GetInt64(4), DeliveriesNeedingReview = reader.GetInt64(5) };
    }

    internal static async Task<object> Load(NpgsqlDataSource dataSource, string schema, SnapshotPlan plan, bool commit = true, CancellationToken cancellationToken = default)
    {
        string q = Quote(schema);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockHistory(connection, transaction, schema, cancellationToken);

        await using (var existing = new NpgsqlCommand($"SELECT manifest_sha256 FROM {q}.delivery_receipt WHERE snapshot_id = @id", connection, transaction))
        {
            existing.Parameters.AddWithValue("id", plan.SnapshotId);
            object? fingerprint = await existing.ExecuteScalarAsync(cancellationToken);
            if (fingerprint != null)
            {
                if (!string.Equals((string)fingerprint, plan.ManifestSha256, StringComparison.Ordinal)) throw new InvalidDataException();
                if (commit) await transaction.CommitAsync(cancellationToken);
                else await transaction.RollbackAsync(cancellationToken);
                return new { Success = true, Stage = "already-loaded", SnapshotId = plan.SnapshotId.ToString("N"), plan.EntityCount, plan.RecordCount };
            }
        }

        // A late snapshot requires rebuilding history, not appending an older state as current.
        await using (var order = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {q}.delivery_receipt WHERE source_completed_utc >= @completed)", connection, transaction))
        {
            order.Parameters.AddWithValue("completed", plan.CompletedUtc);
            if ((bool)(await order.ExecuteScalarAsync(cancellationToken) ?? true))
            {
                await RecordDeliveryIssue(connection, transaction, q, plan, cancellationToken);
                if (commit) await transaction.CommitAsync(cancellationToken);
                else await transaction.RollbackAsync(cancellationToken);
                throw new SnapshotOrderException();
            }
        }
        const string receiptColumns = "(snapshot_id, snapshot_seq, manifest_sha256, manifest, format_version, catalog_version, export_mode, entity_count, record_count, source_completed_utc)";
        await using (var receipt = new NpgsqlCommand($"INSERT INTO {q}.delivery_receipt {receiptColumns} VALUES (@id, (SELECT COALESCE(max(snapshot_seq), 0) + 1 FROM {q}.delivery_receipt), @fingerprint, @manifest, @format, @catalog, 'full', @entities, @records, @completed)", connection, transaction))
        {
            receipt.Parameters.AddWithValue("id", plan.SnapshotId);
            receipt.Parameters.AddWithValue("fingerprint", plan.ManifestSha256);
            receipt.Parameters.AddWithValue("manifest", NpgsqlDbType.Jsonb, plan.ManifestJson);
            receipt.Parameters.AddWithValue("format", plan.FormatVersion);
            receipt.Parameters.AddWithValue("catalog", plan.CatalogVersion);
            receipt.Parameters.AddWithValue("entities", plan.EntityCount);
            receipt.Parameters.AddWithValue("records", plan.RecordCount);
            receipt.Parameters.AddWithValue("completed", plan.CompletedUtc);
            await receipt.ExecuteNonQueryAsync(cancellationToken);
        }

        await CreateStaging(connection, transaction, cancellationToken);
        long loadedTotal = 0;
        foreach (var entity in plan.Entities)
        {
            Console.Error.WriteLine($"Loading {entity.Name}: {(entity.Records is null ? "unavailable" : entity.Records)}");
            await InsertEntity(connection, transaction, q, plan.SnapshotId, entity, cancellationToken);
            long loaded = entity.Records is null ? 0 : await CopyEntity(connection, plan, entity, cancellationToken);
            if (loaded != entity.Records.GetValueOrDefault()) throw new InvalidDataException();
            loadedTotal += loaded;
        }
        if (loadedTotal != plan.RecordCount) throw new InvalidDataException();

        await using (var merge = new NpgsqlCommand($"SELECT {q}.merge_snapshot(@id)", connection, transaction) { CommandTimeout = 1800 })
        {
            merge.Parameters.AddWithValue("id", plan.SnapshotId);
            await merge.ExecuteNonQueryAsync(cancellationToken);
        }
        if (commit) await transaction.CommitAsync(cancellationToken);
        else await transaction.RollbackAsync(cancellationToken);
        return new { Success = true, Stage = commit ? "loaded" : "dry-run-rolled-back", SnapshotId = plan.SnapshotId.ToString("N"), plan.EntityCount, plan.RecordCount };
    }

    private static async Task InsertEntity(NpgsqlConnection connection, NpgsqlTransaction transaction, string q, Guid snapshotId, EntityPlan entity, CancellationToken cancellationToken)
    {
        string sql = $"INSERT INTO {q}.snapshot_entity (snapshot_id, entity, availability, unavailable_status_code, declared_records, loaded_records, record_digest) VALUES (@id, @entity, @availability, @status, @records, @loaded, @digest)";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", snapshotId);
        command.Parameters.AddWithValue("entity", entity.Name);
        command.Parameters.AddWithValue("availability", entity.Availability);
        command.Parameters.AddWithValue("status", (object?)entity.UnavailableStatusCode ?? DBNull.Value);
        command.Parameters.AddWithValue("records", (object?)entity.Records ?? DBNull.Value);
        command.Parameters.AddWithValue("loaded", entity.Records.GetValueOrDefault());
        command.Parameters.AddWithValue("digest", (object?)entity.RecordDigest ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidDataException();
    }

    private static async Task<long> CopyEntity(NpgsqlConnection connection, SnapshotPlan plan, EntityPlan entity, CancellationToken cancellationToken)
    {
        const string copy = "COPY pg_temp.qbd_landing_stage (entity, record_id, record_type, data) FROM STDIN (FORMAT BINARY)";
        await using var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken);
        importer.Timeout = TimeSpan.FromMinutes(30);
        long count = 0;
        foreach (var row in plan.ReadRows(entity))
        {
            await importer.StartRowAsync(cancellationToken);
            await importer.WriteAsync(entity.Name, NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(row.RecordId, NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(row.RecordType, NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(row.DataJson, NpgsqlDbType.Jsonb, cancellationToken);
            count++;
        }
        ulong completed = await importer.CompleteAsync(cancellationToken);
        if (completed != (ulong)count) throw new InvalidDataException();
        return count;
    }

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';

    private static IEnumerable<Exception> ExceptionChain(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException) yield return current;
    }

    private sealed record Settings(string ConnectionString, string Schema)
    {
        internal static Settings FromEnvironment()
        {
            string? raw = Environment.GetEnvironmentVariable(ConnectionVariable);
            if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException();
            var builder = new NpgsqlConnectionStringBuilder(raw)
            {
                ApplicationName = "QuickBooksProbe Postgres Loader",
                IncludeErrorDetail = false,
                Timeout = 15,
                CommandTimeout = 120,
                MaxPoolSize = 2
            };
            if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database) || string.IsNullOrWhiteSpace(builder.Username))
                throw new ArgumentException();
            string schema = Environment.GetEnvironmentVariable(SchemaVariable) ?? "quickbooks_ingest";
            if (!SchemaName().IsMatch(schema)) throw new ArgumentException();
            return new(builder.ConnectionString, schema);
        }
    }

    internal sealed record LoadOptions(string Snapshot, HashSet<string> AllowedUnavailable, bool DryRun)
    {
        internal static LoadOptions Parse(string[] args)
        {
            string? snapshot = null;
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            bool dryRun = false;
            for (int i = 0; i < args.Length;)
            {
                if (args[i] == "--dry-run" && !dryRun) { dryRun = true; i++; continue; }
                if (i + 1 >= args.Length) throw new ArgumentException();
                switch (args[i])
                {
                    case "--snapshot" when snapshot == null:
                        snapshot = args[i + 1];
                        break;
                    case "--allow-unavailable" when allowed.Count == 0:
                        foreach (string value in args[i + 1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                            if (!allowed.Add(value)) throw new ArgumentException();
                        break;
                    default:
                        throw new ArgumentException();
                }
                i += 2;
            }
            if (snapshot == null || !Path.IsPathFullyQualified(snapshot)) throw new ArgumentException();
            return new(snapshot, allowed, dryRun);
        }
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaName();
}

internal sealed record EntityPlan(string Name, int? Records, string? RecordDigest, string Availability, int? UnavailableStatusCode, QuerySpec Spec);
internal sealed record LandingRow(string RecordId, string RecordType, string DataJson);

internal sealed class SnapshotPlan
{
    internal required string Directory { get; init; }
    internal required Guid SnapshotId { get; init; }
    internal required string ManifestJson { get; init; }
    internal required string ManifestSha256 { get; init; }
    internal required int FormatVersion { get; init; }
    internal required int CatalogVersion { get; init; }
    internal required DateTimeOffset CompletedUtc { get; init; }
    internal required EntityPlan[] Entities { get; init; }
    internal int EntityCount => Entities.Length;
    internal long RecordCount => Entities.Sum(x => (long?)x.Records ?? 0);

    internal static SnapshotPlan Read(string directory, HashSet<string> allowedUnavailable)
    {
        string mode = SnapshotVerifier.Verify(directory, published: true);
        if (mode != "full") throw new InvalidDataException();
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string manifestPath = Path.Combine(directory, "manifest.json");
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        string manifestJson = System.Text.Encoding.UTF8.GetString(manifestBytes);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var root = manifest.RootElement;
        int catalogVersion = root.GetProperty("CatalogVersion").GetInt32();
        var catalog = Catalog(catalogVersion);
        var entities = root.GetProperty("Entities").EnumerateArray().Select(value =>
        {
            string name = value.GetProperty("Entity").GetString() ?? throw new InvalidDataException();
            var spec = catalog.Single(x => x.Name == name);
            int? records = value.GetProperty("Records").ValueKind == JsonValueKind.Null ? null : value.GetProperty("Records").GetInt32();
            int? unavailable = value.GetProperty("UnavailableStatusCode").ValueKind == JsonValueKind.Null ? null : value.GetProperty("UnavailableStatusCode").GetInt32();
            string? digest = value.GetProperty("RecordDigest").ValueKind == JsonValueKind.Null ? null : value.GetProperty("RecordDigest").GetString();
            return new EntityPlan(name, records, digest, value.GetProperty("Availability").GetString()!, unavailable, spec);
        }).ToArray();
        var unavailableNames = entities.Where(x => x.UnavailableStatusCode != null).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (!unavailableNames.SetEquals(allowedUnavailable)) throw new PostgresLoader.UnavailablePolicyException(unavailableNames.Order(StringComparer.Ordinal).ToArray());
        return new SnapshotPlan
        {
            Directory = directory,
            SnapshotId = Guid.ParseExact(root.GetProperty("SnapshotId").GetString()!, "N"),
            ManifestJson = manifestJson,
            ManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
            FormatVersion = root.GetProperty("FormatVersion").GetInt32(),
            CatalogVersion = catalogVersion,
            CompletedUtc = root.GetProperty("CompletedUtc").GetDateTimeOffset(),
            Entities = entities
        };
    }

    internal IEnumerable<LandingRow> ReadRows(EntityPlan entity)
    {
        if (entity.Records is null) yield break;
        string xmlPath = Path.Combine(Directory, entity.Name + ".xml");
        string jsonPath = Path.Combine(Directory, entity.Name + ".jsonl");
        using var xml = XmlReader.Create(xmlPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        using var json = new StreamReader(jsonPath, new System.Text.UTF8Encoding(false, true));
        xml.MoveToContent();
        xml.ReadStartElement("Records");
        int count = 0;
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        while (xml.MoveToContent() == XmlNodeType.Element)
        {
            var source = (XElement)XNode.ReadFrom(xml);
            string line = json.ReadLine() ?? throw new InvalidDataException();
            using var row = JsonDocument.Parse(line);
            var root = row.RootElement;
            string recordId = root.GetProperty("RecordId").GetString() ?? throw new InvalidDataException();
            string recordType = root.GetProperty("RecordType").GetString() ?? throw new InvalidDataException();
            string primary = FullExport.Identity(entity.Spec, source);
            int occurrence = occurrences.GetValueOrDefault(primary) + 1;
            occurrences[primary] = occurrence;
            if (root.GetProperty("Entity").GetString() != entity.Name || source.Name.LocalName != recordType ||
                recordId != FullExport.Identity(entity.Spec, source, occurrence) || line != FullExport.JsonRecord(entity.Spec, source, occurrence))
                throw new InvalidDataException();
            yield return new(recordId, recordType, root.GetProperty("Data").GetRawText());
            count++;
        }
        if (xml.NodeType == XmlNodeType.EndElement) xml.ReadEndElement();
        if (xml.MoveToContent() != XmlNodeType.None || json.ReadLine() != null || count != entity.Records) throw new InvalidDataException();
    }

    private static IReadOnlyList<QuerySpec> Catalog(int version) => version switch
    {
        1 => QueryCatalog.Version1,
        2 => QueryCatalog.Version2,
        3 => QueryCatalog.Version3,
        4 => QueryCatalog.Version4,
        5 => QueryCatalog.Version5,
        6 => QueryCatalog.Version6,
        7 => QueryCatalog.Version7,
        8 => QueryCatalog.Version8,
        9 => QueryCatalog.Version9,
        10 => QueryCatalog.Version10,
        11 => QueryCatalog.Version11,
        12 => QueryCatalog.Version12,
        QueryCatalog.Version => QueryCatalog.All,
        _ => throw new InvalidDataException()
    };
}
