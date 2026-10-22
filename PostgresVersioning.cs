using Npgsql;

namespace QuickBooksProbe;

internal static partial class PostgresLoader
{
    // One schema is one source history. Serialize schema changes and all snapshot IDs,
    // including the receipt/order check; a per-snapshot lock is insufficient here.
    private static async Task LockHistory(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", connection, transaction);
        command.Parameters.AddWithValue("key", "qbd-history:" + schema);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task CreateStaging(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE TEMP TABLE qbd_landing_stage (
                entity text NOT NULL,
                record_id text NOT NULL,
                record_type text NOT NULL,
                data jsonb NOT NULL,
                PRIMARY KEY (entity, record_id)
            ) ON COMMIT DROP;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MigrateVersions(NpgsqlConnection connection, NpgsqlTransaction transaction, string q, CancellationToken cancellationToken)
    {
        // Replay every existing snapshot before removing its duplicated payloads. There
        // is deliberately no CASCADE: downstream dependencies must be migrated explicitly.
        string sql = $$"""
            ALTER TABLE {{q}}.delivery_receipt ADD COLUMN snapshot_seq bigint;
            WITH ordered AS (
                SELECT snapshot_id, row_number() OVER (ORDER BY source_completed_utc, snapshot_id) AS seq
                FROM {{q}}.delivery_receipt
            )
            UPDATE {{q}}.delivery_receipt r SET snapshot_seq = o.seq
            FROM ordered o WHERE r.snapshot_id = o.snapshot_id;
            ALTER TABLE {{q}}.delivery_receipt
                ALTER COLUMN snapshot_seq SET NOT NULL,
                ADD UNIQUE (snapshot_seq), ADD CHECK (snapshot_seq > 0);
            ALTER TABLE {{q}}.landing_record RENAME TO landing_record_snapshot;
            CREATE TABLE {{q}}.landing_record (
                entity text NOT NULL,
                record_id text NOT NULL,
                record_type text NOT NULL,
                data jsonb NOT NULL,
                valid_from_seq bigint NOT NULL REFERENCES {{q}}.delivery_receipt(snapshot_seq),
                valid_to_seq bigint REFERENCES {{q}}.delivery_receipt(snapshot_seq),
                CONSTRAINT landing_record_version_pk PRIMARY KEY (entity, record_id, valid_from_seq),
                CHECK (valid_to_seq IS NULL OR valid_to_seq > valid_from_seq)
            );
            CREATE UNIQUE INDEX landing_record_current_key ON {{q}}.landing_record(entity, record_id)
                WHERE valid_to_seq IS NULL;
            CREATE INDEX landing_record_as_of_idx ON {{q}}.landing_record(entity, valid_from_seq, valid_to_seq);
            """;
        await using (var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 1800 })
            await command.ExecuteNonQueryAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(VersionFunctions(q), connection, transaction))
            await command.ExecuteNonQueryAsync(cancellationToken);
        await CreateStaging(connection, transaction, cancellationToken);
        sql = $$"""
            DO $migration$
            DECLARE r record;
            BEGIN
                FOR r IN SELECT snapshot_id FROM {{q}}.delivery_receipt ORDER BY snapshot_seq LOOP
                    TRUNCATE pg_temp.qbd_landing_stage;
                    INSERT INTO pg_temp.qbd_landing_stage (entity, record_id, record_type, data)
                        SELECT entity, record_id, record_type, data FROM {{q}}.landing_record_snapshot
                        WHERE snapshot_id = r.snapshot_id;
                    PERFORM {{q}}.merge_snapshot(r.snapshot_id);
                    IF EXISTS (
                        (SELECT entity, record_id, record_type, data FROM {{q}}.landing_record_as_of(r.snapshot_id)
                         EXCEPT ALL SELECT entity, record_id, record_type, data FROM pg_temp.qbd_landing_stage)
                        UNION ALL
                        (SELECT entity, record_id, record_type, data FROM pg_temp.qbd_landing_stage
                         EXCEPT ALL SELECT entity, record_id, record_type, data FROM {{q}}.landing_record_as_of(r.snapshot_id))
                    ) THEN
                        RAISE EXCEPTION 'Historical snapshot reconstruction failed' USING ERRCODE = '23514';
                    END IF;
                END LOOP;
            END $migration$;
            DROP TABLE {{q}}.landing_record_snapshot;
            INSERT INTO {{q}}.loader_schema(version) VALUES (3);
            """;
        await using var replay = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 1800 };
        await replay.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string VersionFunctions(string q, bool recordChurn = false) => $$"""
        -- Intervals represent observed JSONB states, not business-effective dates. An
        -- unavailable or absent catalog entity must never expose a carried-forward state.
        CREATE OR REPLACE FUNCTION {{q}}.landing_record_as_of(p_snapshot_id uuid)
        RETURNS TABLE (entity text, record_id text, record_type text, data jsonb)
        LANGUAGE sql STABLE AS $asof$
            SELECT v.entity, v.record_id, v.record_type, v.data
            FROM {{q}}.delivery_receipt r
            JOIN {{q}}.snapshot_entity e ON e.snapshot_id = r.snapshot_id AND e.declared_records IS NOT NULL
            JOIN {{q}}.landing_record v ON v.entity = e.entity
                AND v.valid_from_seq <= r.snapshot_seq
                AND (v.valid_to_seq IS NULL OR v.valid_to_seq > r.snapshot_seq)
            WHERE r.snapshot_id = p_snapshot_id
        $asof$;

        -- The caller owns the schema-wide history lock and stages the complete verified
        -- snapshot in this transaction. No hash is needed: JSONB equality compares the
        -- complete stored value and ignores irrelevant object-key ordering/whitespace.
        CREATE OR REPLACE FUNCTION {{q}}.merge_snapshot(p_snapshot_id uuid) RETURNS void
        LANGUAGE plpgsql AS $merge$
        DECLARE receipt {{q}}.delivery_receipt%ROWTYPE;
        BEGIN
            SELECT * INTO STRICT receipt FROM {{q}}.delivery_receipt WHERE snapshot_id = p_snapshot_id;
            IF EXISTS (SELECT 1 FROM {{q}}.landing_record WHERE valid_from_seq >= receipt.snapshot_seq
                       OR valid_to_seq >= receipt.snapshot_seq) THEN
                RAISE EXCEPTION 'Snapshot merge is out of order' USING ERRCODE = '23514';
            END IF;
            ANALYZE pg_temp.qbd_landing_stage;
            IF (SELECT count(*) FROM {{q}}.snapshot_entity WHERE snapshot_id = p_snapshot_id) <> receipt.entity_count
               OR (SELECT count(*) FROM pg_temp.qbd_landing_stage) <> receipt.record_count
               OR EXISTS (
                   SELECT 1 FROM pg_temp.qbd_landing_stage s
                   LEFT JOIN {{q}}.snapshot_entity e ON e.snapshot_id = p_snapshot_id AND e.entity = s.entity
                   WHERE e.entity IS NULL OR e.declared_records IS NULL
               ) OR EXISTS (
                   SELECT 1 FROM {{q}}.snapshot_entity e
                   LEFT JOIN (SELECT entity, count(*) AS n FROM pg_temp.qbd_landing_stage GROUP BY entity) s USING (entity)
                   WHERE e.snapshot_id = p_snapshot_id AND
                       (COALESCE(s.n, 0) <> COALESCE(e.declared_records, 0) OR COALESCE(s.n, 0) <> e.loaded_records)
               ) THEN
                RAISE EXCEPTION 'Staged snapshot counts or coverage failed' USING ERRCODE = '23514';
            END IF;

            {{(recordChurn ? "WITH closed AS (" : "")}}
            UPDATE {{q}}.landing_record v SET valid_to_seq = receipt.snapshot_seq
            WHERE v.valid_to_seq IS NULL
              AND EXISTS (SELECT 1 FROM {{q}}.snapshot_entity e WHERE e.snapshot_id = p_snapshot_id
                          AND e.entity = v.entity AND e.declared_records IS NOT NULL)
              AND NOT EXISTS (SELECT 1 FROM pg_temp.qbd_landing_stage s WHERE s.entity = v.entity
                              AND s.record_id = v.record_id AND s.record_type = v.record_type AND s.data = v.data)
            {{(recordChurn ? $$"""
                RETURNING v.entity
            ), counts AS (SELECT entity, count(*) AS n FROM closed GROUP BY entity)
            UPDATE {{q}}.snapshot_entity e SET versions_closed = c.n
            FROM counts c WHERE e.snapshot_id = p_snapshot_id AND e.entity = c.entity
            """ : "")}};
            {{(recordChurn ? "WITH inserted AS (" : "")}}
            INSERT INTO {{q}}.landing_record (entity, record_id, record_type, data, valid_from_seq)
                SELECT s.entity, s.record_id, s.record_type, s.data, receipt.snapshot_seq
                FROM pg_temp.qbd_landing_stage s
                WHERE NOT EXISTS (SELECT 1 FROM {{q}}.landing_record v WHERE v.entity = s.entity
                                  AND v.record_id = s.record_id AND v.valid_to_seq IS NULL)
            {{(recordChurn ? $$"""
                RETURNING entity
            ), counts AS (SELECT entity, count(*) AS n FROM inserted GROUP BY entity)
            UPDATE {{q}}.snapshot_entity e SET versions_inserted = c.n
            FROM counts c WHERE e.snapshot_id = p_snapshot_id AND e.entity = c.entity
            """ : "")}};

            IF EXISTS (
                SELECT 1 FROM {{q}}.snapshot_entity e
                LEFT JOIN (SELECT entity, count(*) AS n FROM {{q}}.landing_record_as_of(p_snapshot_id) GROUP BY entity) v USING (entity)
                WHERE e.snapshot_id = p_snapshot_id AND COALESCE(v.n, 0) <> e.loaded_records
            ) THEN
                RAISE EXCEPTION 'Reconstructed snapshot counts failed' USING ERRCODE = '23514';
            END IF;
        END $merge$;
        """;
}
