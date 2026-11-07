namespace Mirror.Data;

/// <summary>Raw SQL for the high-volume, partitioned log tables. Executed from the EF migration.</summary>
public static class LogSchema
{
    public const string Create = """
        CREATE EXTENSION IF NOT EXISTS pg_trgm;

        -- Narrow index rows. Two-level partitioning: LIST(mission_id) → RANGE(ts) per day,
        -- so retention and size limits are enforced per mission by dropping whole day partitions.
        CREATE TABLE traffic_log (
            id            uuid        NOT NULL,
            mission_id    uuid        NOT NULL,
            ts            timestamptz NOT NULL,
            method        text        NOT NULL,
            path          text        NOT NULL,
            query         text,
            main_status   smallint    NOT NULL,
            dev_status    smallint,
            main_ms       real        NOT NULL,
            dev_ms        real,
            verdict       smallint    NOT NULL,
            diff_kinds    integer     NOT NULL DEFAULT 0,
            diff_count    integer     NOT NULL DEFAULT 0,
            signature     bigint      NOT NULL DEFAULT 0,
            rules_version integer     NOT NULL DEFAULT 0,
            source        smallint    NOT NULL DEFAULT 0,
            job_id        uuid,
            replay_of     uuid,
            note          text,
            summary       jsonb,
            PRIMARY KEY (mission_id, ts, id)
        ) PARTITION BY LIST (mission_id);

        -- The PK (mission_id, ts, id) serves the default newest-first listing (backward scan).
        CREATE INDEX ix_traffic_log_verdict   ON traffic_log (verdict, ts DESC, id DESC);
        CREATE INDEX ix_traffic_log_path      ON traffic_log (path text_pattern_ops, ts DESC);
        CREATE INDEX ix_traffic_log_path_trgm ON traffic_log USING gin (path gin_trgm_ops);
        CREATE INDEX ix_traffic_log_signature ON traffic_log (signature, ts DESC) WHERE signature <> 0;
        CREATE INDEX ix_traffic_log_job       ON traffic_log (job_id, ts DESC) WHERE job_id IS NOT NULL;

        -- Heavy payloads, Brotli-compressed by the writer. Same partitioning as traffic_log.
        CREATE TABLE traffic_body (
            log_id          uuid        NOT NULL,
            mission_id      uuid        NOT NULL,
            ts              timestamptz NOT NULL,
            codec           smallint    NOT NULL DEFAULT 1,
            request_headers bytea,
            request_body    bytea,
            main_headers    bytea,
            main_body       bytea,
            dev_headers     bytea,
            dev_body        bytea,
            diff            bytea,
            PRIMARY KEY (mission_id, ts, log_id)
        ) PARTITION BY LIST (mission_id);

        CREATE TABLE traffic_stats_minute (
            mission_id  uuid             NOT NULL,
            bucket      timestamptz      NOT NULL,
            verdict     smallint         NOT NULL,
            n           bigint           NOT NULL,
            main_ms_sum double precision NOT NULL,
            dev_ms_sum  double precision NOT NULL,
            PRIMARY KEY (mission_id, bucket, verdict)
        );

        CREATE OR REPLACE FUNCTION mirror_ensure_partitions(p_mission uuid, p_from date, p_days int)
        RETURNS void LANGUAGE plpgsql AS $fn$
        DECLARE
            m      text := replace(p_mission::text, '-', '');
            t      text;
            parent text;
            part   text;
            d      date;
        BEGIN
            PERFORM pg_advisory_xact_lock(hashtext('mirror_partitions:' || m));
            FOREACH t IN ARRAY ARRAY['traffic_log', 'traffic_body'] LOOP
                parent := t || '_m_' || m;
                IF to_regclass(parent) IS NULL THEN
                    EXECUTE format('CREATE TABLE %I PARTITION OF %I FOR VALUES IN (%L) PARTITION BY RANGE (ts)', parent, t, p_mission);
                END IF;
                FOR i IN 0 .. greatest(p_days, 1) - 1 LOOP
                    d := p_from + i;
                    part := parent || '_' || to_char(d, 'YYYYMMDD');
                    IF to_regclass(part) IS NULL THEN
                        EXECUTE format('CREATE TABLE %I PARTITION OF %I FOR VALUES FROM (%L) TO (%L)',
                            part, parent, (d::timestamp AT TIME ZONE 'UTC'), ((d + 1)::timestamp AT TIME ZONE 'UTC'));
                        IF t = 'traffic_body' THEN
                            -- Already compressed by the writer: store out of line without pglz.
                            EXECUTE format('ALTER TABLE %I ALTER COLUMN request_headers SET STORAGE EXTERNAL,
                                ALTER COLUMN request_body SET STORAGE EXTERNAL, ALTER COLUMN main_headers SET STORAGE EXTERNAL,
                                ALTER COLUMN main_body SET STORAGE EXTERNAL, ALTER COLUMN dev_headers SET STORAGE EXTERNAL,
                                ALTER COLUMN dev_body SET STORAGE EXTERNAL, ALTER COLUMN diff SET STORAGE EXTERNAL', part);
                        END IF;
                    END IF;
                END LOOP;
            END LOOP;
        END
        $fn$;

        -- Lists a mission's day partitions with their date and combined size (log + body).
        CREATE OR REPLACE FUNCTION mirror_mission_partitions(p_mission uuid)
        RETURNS TABLE(day date, log_table text, body_table text, bytes bigint) LANGUAGE sql STABLE AS $fn$
            WITH m AS (SELECT replace(p_mission::text, '-', '') AS id),
            parts AS (
                SELECT c.relname::text AS name, to_date(right(c.relname, 8), 'YYYYMMDD') AS day
                FROM pg_inherits i
                JOIN pg_class c ON c.oid = i.inhrelid
                JOIN pg_class p ON p.oid = i.inhparent, m
                WHERE p.relname = 'traffic_log_m_' || m.id
            )
            SELECT parts.day, parts.name, replace(parts.name, 'traffic_log_', 'traffic_body_'),
                   pg_total_relation_size(parts.name::regclass)
                   + coalesce(pg_total_relation_size(to_regclass(replace(parts.name, 'traffic_log_', 'traffic_body_'))), 0)
            FROM parts ORDER BY parts.day;
        $fn$;

        CREATE OR REPLACE FUNCTION mirror_drop_mission_partitions(p_mission uuid)
        RETURNS void LANGUAGE plpgsql AS $fn$
        DECLARE m text := replace(p_mission::text, '-', '');
        BEGIN
            PERFORM pg_advisory_xact_lock(hashtext('mirror_partitions:' || m));
            EXECUTE format('DROP TABLE IF EXISTS %I, %I', 'traffic_log_m_' || m, 'traffic_body_m_' || m);
            DELETE FROM traffic_stats_minute WHERE mission_id = p_mission;
        END
        $fn$;

        CREATE OR REPLACE FUNCTION mirror_drop_day_partition(p_mission uuid, p_day date)
        RETURNS void LANGUAGE plpgsql AS $fn$
        DECLARE m text := replace(p_mission::text, '-', '');
                s text := to_char(p_day, 'YYYYMMDD');
        BEGIN
            PERFORM pg_advisory_xact_lock(hashtext('mirror_partitions:' || m));
            EXECUTE format('DROP TABLE IF EXISTS %I, %I', 'traffic_log_m_' || m || '_' || s, 'traffic_body_m_' || m || '_' || s);
        END
        $fn$;
        """;

    public const string Drop = """
        DROP FUNCTION IF EXISTS mirror_drop_day_partition(uuid, date);
        DROP FUNCTION IF EXISTS mirror_drop_mission_partitions(uuid);
        DROP FUNCTION IF EXISTS mirror_mission_partitions(uuid);
        DROP FUNCTION IF EXISTS mirror_ensure_partitions(uuid, date, int);
        DROP TABLE IF EXISTS traffic_stats_minute;
        DROP TABLE IF EXISTS traffic_body;
        DROP TABLE IF EXISTS traffic_log;
        """;
}
