using Npgsql;

namespace QuickBooksProbe;

internal static partial class PostgresLoader
{
    private static async Task MigrateHealth(NpgsqlConnection connection, NpgsqlTransaction transaction, string q, CancellationToken cancellationToken)
    {
        string sql = $$"""
            ALTER TABLE {{q}}.snapshot_entity
                ADD COLUMN versions_inserted bigint NOT NULL DEFAULT 0 CHECK (versions_inserted >= 0),
                ADD COLUMN versions_closed bigint NOT NULL DEFAULT 0 CHECK (versions_closed >= 0);
            WITH endpoints AS (
                SELECT valid_from_seq AS seq, entity, count(*) AS inserted, 0::bigint AS closed
                FROM {{q}}.landing_record GROUP BY valid_from_seq, entity
                UNION ALL
                SELECT valid_to_seq, entity, 0::bigint, count(*)
                FROM {{q}}.landing_record WHERE valid_to_seq IS NOT NULL GROUP BY valid_to_seq, entity
            ), counts AS (
                SELECT seq, entity, sum(inserted) AS inserted, sum(closed) AS closed
                FROM endpoints GROUP BY seq, entity
            )
            UPDATE {{q}}.snapshot_entity e
            SET versions_inserted = c.inserted, versions_closed = c.closed
            FROM counts c JOIN {{q}}.delivery_receipt r ON r.snapshot_seq = c.seq
            WHERE e.snapshot_id = r.snapshot_id AND e.entity = c.entity;
            DROP INDEX IF EXISTS {{q}}.landing_record_as_of_idx;
            CREATE TABLE {{q}}.delivery_issue (
                snapshot_id uuid PRIMARY KEY,
                manifest_sha256 text NOT NULL CHECK (manifest_sha256 ~ '^[0-9a-f]{64}$'),
                source_completed_utc timestamptz NOT NULL,
                reason text NOT NULL CHECK (reason = 'snapshot-out-of-order'),
                first_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                last_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                acknowledged_at timestamptz
            );
            INSERT INTO {{q}}.loader_schema(version) VALUES (4);
            """;
        await using (var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 1800 })
            await command.ExecuteNonQueryAsync(cancellationToken);
        await using var functions = new NpgsqlCommand(VersionFunctions(q, recordChurn: true), connection, transaction);
        await functions.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RecordDeliveryIssue(NpgsqlConnection connection, NpgsqlTransaction transaction, string q, SnapshotPlan plan, CancellationToken cancellationToken)
    {
        string sql = $$"""
            INSERT INTO {{q}}.delivery_issue (snapshot_id, manifest_sha256, source_completed_utc, reason)
            VALUES (@id, @fingerprint, @completed, 'snapshot-out-of-order')
            ON CONFLICT (snapshot_id) DO UPDATE SET last_seen_at = clock_timestamp()
            WHERE {{q}}.delivery_issue.manifest_sha256 = EXCLUDED.manifest_sha256;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", plan.SnapshotId);
        command.Parameters.AddWithValue("fingerprint", plan.ManifestSha256);
        command.Parameters.AddWithValue("completed", plan.CompletedUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidDataException();
    }
}
