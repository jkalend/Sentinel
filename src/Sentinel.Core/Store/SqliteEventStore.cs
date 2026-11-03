using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sentinel.Core.Model;

namespace Sentinel.Core.Store;

public sealed record StoreCounts(long Events, long Alerts, long Gaps);

public sealed class SqliteEventStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteEventStore(string databasePath, bool readOnly = false)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory) && !readOnly)
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        _connection.Open();
        if (readOnly)
        {
            return;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = DELETE;";
        command.ExecuteNonQuery();
    }

    public void Initialize()
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS bookmarks (
                source_id TEXT PRIMARY KEY,
                incarnation_id TEXT NOT NULL,
                config_generation INTEGER NOT NULL,
                event_record_id INTEGER NOT NULL,
                coverage_start_record_id INTEGER NULL,
                sensor_sequence INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                bookmark_xml TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS source_state (
                source_id TEXT PRIMARY KEY,
                maximum_size_bytes INTEGER NOT NULL,
                log_mode TEXT NOT NULL,
                is_enabled INTEGER NOT NULL,
                log_file_path TEXT NULL,
                captured_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS events (
                sensor_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                source_key TEXT NOT NULL UNIQUE,
                contract_version TEXT NOT NULL,
                sensor_id TEXT NOT NULL,
                host_id TEXT NOT NULL,
                channel TEXT NOT NULL,
                provider TEXT NOT NULL,
                provider_guid TEXT NULL,
                incarnation_id TEXT NOT NULL,
                config_generation INTEGER NOT NULL,
                event_id INTEGER NOT NULL,
                event_record_id INTEGER NOT NULL,
                time_utc TEXT NOT NULL,
                logged_time_utc TEXT NULL,
                processed_time_utc TEXT NOT NULL,
                clock_status TEXT NOT NULL,
                record_kind TEXT NOT NULL,
                ocsf_json TEXT NULL,
                ocsf_class_uid INTEGER NULL,
                ocsf_activity_id INTEGER NULL,
                unmapped_json TEXT NOT NULL,
                raw_xml TEXT NOT NULL,
                raw_sha256 TEXT NOT NULL,
                raw_purged_at TEXT NULL,
                gap_id TEXT NULL,
                host_name TEXT NULL,
                user_name TEXT NULL,
                process_guid TEXT NULL,
                process_id INTEGER NULL,
                process_name TEXT NULL,
                process_cmd_line TEXT NULL,
                process_path TEXT NULL,
                parent_process_guid TEXT NULL,
                parent_process_id INTEGER NULL,
                parent_process_name TEXT NULL,
                parent_cmd_line TEXT NULL,
                network_protocol TEXT NULL,
                network_initiated INTEGER NULL,
                network_outcome TEXT NULL,
                src_ip TEXT NULL,
                src_port INTEGER NULL,
                src_hostname TEXT NULL,
                dst_ip TEXT NULL,
                dst_port INTEGER NULL,
                dst_hostname TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_events_guid ON events(host_id, process_guid);
            CREATE INDEX IF NOT EXISTS ix_events_time ON events(time_utc);

            CREATE TABLE IF NOT EXISTS alerts (
                alert_id TEXT PRIMARY KEY,
                rule_id TEXT NOT NULL,
                rule_version TEXT NOT NULL,
                engine_version TEXT NOT NULL,
                source_key TEXT NOT NULL,
                sensor_sequence INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                details_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_alerts_source ON alerts(source_key);

            CREATE TABLE IF NOT EXISTS gaps (
                gap_id TEXT PRIMARY KEY,
                source_id TEXT NOT NULL,
                incarnation_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                confidence TEXT NOT NULL,
                start_record_id INTEGER NULL,
                end_record_id INTEGER NULL,
                noted_at TEXT NOT NULL,
                details TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS retention_purges (
                purge_id TEXT PRIMARY KEY,
                created_at TEXT NOT NULL,
                policy_json TEXT NOT NULL,
                policy_sha256 TEXT NOT NULL,
                data_events_deleted INTEGER NOT NULL,
                source_health_deleted INTEGER NOT NULL,
                raw_xml_purged INTEGER NOT NULL,
                alerts_deleted INTEGER NOT NULL,
                gaps_deleted INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS purged_ranges (
                purge_id TEXT PRIMARY KEY,
                first_sequence INTEGER NOT NULL,
                last_sequence INTEGER NOT NULL,
                event_count INTEGER NOT NULL,
                first_time TEXT NOT NULL,
                last_time TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_purged_ranges_sequence
                ON purged_ranges(first_sequence, last_sequence);
            """;

        using var command = _connection.CreateCommand();
        command.CommandText = ddl;
        command.ExecuteNonQuery();

        EnsureColumn("bookmarks", "bookmark_xml", "TEXT NULL");
        EnsureColumn("events", "network_outcome", "TEXT NULL");
        EnsureColumn("events", "raw_purged_at", "TEXT NULL");
        SetMetaIfMissing("contract_version", SentinelVersions.ContractVersion);
        SetMetaIfMissing("ocsf_version", SentinelVersions.OcsfVersion);
        SetMetaIfMissing("engine_version", SentinelVersions.EngineVersion);
        SetMetaIfMissing("journal_mode", "delete");
        SetMetaIfMissing("created_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    public (string SensorId, string HostId) EnsureIdentity(string? sensorId, string? hostId)
    {
        var currentSensor = GetMeta("sensor_id");
        var currentHost = GetMeta("host_id");

        var resolvedSensor = currentSensor
            ?? (string.IsNullOrWhiteSpace(sensorId) ? Guid.NewGuid().ToString("D") : sensorId);
        var resolvedHost = currentHost
            ?? (string.IsNullOrWhiteSpace(hostId) ? Guid.NewGuid().ToString("D") : hostId);

        if (currentSensor is not null && !string.Equals(currentSensor, sensorId, StringComparison.OrdinalIgnoreCase) && sensorId is not null)
        {
            throw new InvalidOperationException($"Database sensor_id is '{currentSensor}', not '{sensorId}'.");
        }

        if (currentHost is not null && !string.Equals(currentHost, hostId, StringComparison.OrdinalIgnoreCase) && hostId is not null)
        {
            throw new InvalidOperationException($"Database host_id is '{currentHost}', not '{hostId}'.");
        }

        SetMeta("sensor_id", resolvedSensor);
        SetMeta("host_id", resolvedHost);
        return (resolvedSensor, resolvedHost);
    }

    public string? GetMeta(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public Bookmark? GetBookmark(string sourceId, SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_id, incarnation_id, config_generation, event_record_id,
                   coverage_start_record_id, sensor_sequence, updated_at, bookmark_xml
            FROM bookmarks WHERE source_id = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new Bookmark(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetInt64(5),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public AppendResult Append(
        LocalEvent value,
        IReadOnlyList<AlertCandidate> alerts,
        BookmarkUpdate? bookmark,
        CoverageGap? gapBefore = null)
    {
        using var transaction = _connection.BeginTransaction();

        if (gapBefore is not null)
        {
            InsertGap(gapBefore, transaction);
        }

        long sequence;
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = InsertEventSql;
            AddEventParameters(command, value);
            command.ExecuteNonQuery();
        }

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT changes();";
            var changed = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (changed == 0)
            {
                transaction.Commit();
                return new AppendResult(AppendStatus.Duplicate, 0);
            }
        }

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT last_insert_rowid();";
            sequence = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        foreach (var alert in alerts)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO alerts
                    (alert_id, rule_id, rule_version, engine_version, source_key,
                     sensor_sequence, created_at, details_json)
                VALUES
                    ($alert_id, $rule_id, $rule_version, $engine_version, $source_key,
                     $sensor_sequence, $created_at, $details_json);
                """;
            command.Parameters.AddWithValue("$alert_id", alert.AlertId);
            command.Parameters.AddWithValue("$rule_id", alert.RuleId);
            command.Parameters.AddWithValue("$rule_version", alert.RuleVersion);
            command.Parameters.AddWithValue("$engine_version", alert.EngineVersion);
            command.Parameters.AddWithValue("$source_key", alert.SourceKey);
            command.Parameters.AddWithValue("$sensor_sequence", sequence);
            command.Parameters.AddWithValue("$created_at", Timestamp(alert.CreatedAt));
            command.Parameters.AddWithValue("$details_json", alert.DetailsJson);
            command.ExecuteNonQuery();
        }

        if (bookmark is not null)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO bookmarks
                    (source_id, incarnation_id, config_generation, event_record_id,
                     coverage_start_record_id, sensor_sequence, updated_at, bookmark_xml)
                VALUES
                    ($source_id, $incarnation_id, $config_generation, $event_record_id,
                     $coverage_start, $sensor_sequence, $updated_at, $bookmark_xml)
                ON CONFLICT(source_id) DO UPDATE SET
                    incarnation_id = excluded.incarnation_id,
                    config_generation = excluded.config_generation,
                    event_record_id = excluded.event_record_id,
                    coverage_start_record_id = CASE
                        WHEN bookmarks.coverage_start_record_id IS NULL THEN excluded.coverage_start_record_id
                        WHEN excluded.coverage_start_record_id IS NULL THEN bookmarks.coverage_start_record_id
                        ELSE MIN(bookmarks.coverage_start_record_id, excluded.coverage_start_record_id)
                    END,
                    sensor_sequence = excluded.sensor_sequence,
                    updated_at = excluded.updated_at,
                    bookmark_xml = COALESCE(excluded.bookmark_xml, bookmarks.bookmark_xml)
                WHERE excluded.event_record_id >= bookmarks.event_record_id;
                """;
            command.Parameters.AddWithValue("$source_id", bookmark.SourceId);
            command.Parameters.AddWithValue("$incarnation_id", bookmark.IncarnationId);
            command.Parameters.AddWithValue("$config_generation", bookmark.ConfigGeneration);
            command.Parameters.AddWithValue("$event_record_id", bookmark.EventRecordId);
            command.Parameters.AddWithValue("$coverage_start", (object?)bookmark.CoverageStartRecordId ?? DBNull.Value);
            command.Parameters.AddWithValue("$sensor_sequence", sequence);
            command.Parameters.AddWithValue("$updated_at", Timestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$bookmark_xml", (object?)bookmark.BookmarkXml ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return new AppendResult(AppendStatus.Accepted, sequence);
    }

    public void InsertGap(CoverageGap gap)
    {
        using var transaction = _connection.BeginTransaction();
        InsertGap(gap, transaction);
        transaction.Commit();
    }

    private void InsertGap(CoverageGap gap, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO gaps
                (gap_id, source_id, incarnation_id, kind, confidence,
                 start_record_id, end_record_id, noted_at, details)
            VALUES
                ($gap_id, $source_id, $incarnation_id, $kind, $confidence,
                 $start_record_id, $end_record_id, $noted_at, $details);
            """;
        command.Parameters.AddWithValue("$gap_id", gap.GapId);
        command.Parameters.AddWithValue("$source_id", gap.SourceId);
        command.Parameters.AddWithValue("$incarnation_id", gap.IncarnationId);
        command.Parameters.AddWithValue("$kind", gap.Kind.ToString());
        command.Parameters.AddWithValue("$confidence", gap.Confidence.ToString());
        command.Parameters.AddWithValue("$start_record_id", (object?)gap.StartRecordId ?? DBNull.Value);
        command.Parameters.AddWithValue("$end_record_id", (object?)gap.EndRecordId ?? DBNull.Value);
        command.Parameters.AddWithValue("$noted_at", Timestamp(gap.NotedAt));
        command.Parameters.AddWithValue("$details", gap.Details);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<LocalEvent> ReadEvents()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM events ORDER BY sensor_sequence;";
        using var reader = command.ExecuteReader();
        var events = new List<LocalEvent>();
        while (reader.Read())
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    public StoreCounts GetCounts(SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM events),
                (SELECT COUNT(*) FROM alerts),
                (SELECT COUNT(*) FROM gaps);
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new StoreCounts(0, 0, 0);
        }

        return new StoreCounts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public (StoreCounts Counts, Bookmark? Bookmark) GetStatus(string sourceId)
    {
        using var transaction = _connection.BeginTransaction();
        var counts = GetCounts(transaction);
        var bookmark = GetBookmark(sourceId, transaction);
        transaction.Commit();
        return (counts, bookmark);
    }

    public IReadOnlyList<StoredAlert> ReadAlerts()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM alerts ORDER BY sensor_sequence, rule_id;";
        using var reader = command.ExecuteReader();
        var alerts = new List<StoredAlert>();
        while (reader.Read())
        {
            alerts.Add(new StoredAlert(
                reader.GetString(reader.GetOrdinal("alert_id")),
                reader.GetString(reader.GetOrdinal("rule_id")),
                reader.GetString(reader.GetOrdinal("rule_version")),
                reader.GetString(reader.GetOrdinal("engine_version")),
                reader.GetString(reader.GetOrdinal("source_key")),
                reader.GetInt64(reader.GetOrdinal("sensor_sequence")),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(reader.GetOrdinal("details_json"))));
        }

        return alerts;
    }

    public IReadOnlyList<CoverageGap> ReadGaps()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM gaps ORDER BY noted_at, gap_id;";
        using var reader = command.ExecuteReader();
        var gaps = new List<CoverageGap>();
        while (reader.Read())
        {
            gaps.Add(new CoverageGap(
                reader.GetString(reader.GetOrdinal("gap_id")),
                reader.GetString(reader.GetOrdinal("source_id")),
                reader.GetString(reader.GetOrdinal("incarnation_id")),
                Enum.Parse<GapKind>(reader.GetString(reader.GetOrdinal("kind"))),
                Enum.Parse<CauseConfidence>(reader.GetString(reader.GetOrdinal("confidence"))),
                reader.IsDBNull(reader.GetOrdinal("start_record_id")) ? null : reader.GetInt64(reader.GetOrdinal("start_record_id")),
                reader.IsDBNull(reader.GetOrdinal("end_record_id")) ? null : reader.GetInt64(reader.GetOrdinal("end_record_id")),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("noted_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(reader.GetOrdinal("details"))));
        }

        return gaps;
    }

    public RetentionReport Purge(RetentionPolicy policy, DateTimeOffset? now = null, bool apply = true)
    {
        policy.Validate();
        var at = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var eventCutoff = at.AddDays(-policy.EventDays);
        var rawCutoff = at.AddDays(-policy.RawDays);
        var alertCutoff = at.AddDays(-policy.AlertDays);
        var healthCutoff = at.AddDays(-policy.SourceHealthDays);
        var gapCutoff = at.AddDays(-policy.GapDays);
        var purgeId = Guid.NewGuid().ToString("D");

        using var transaction = _connection.BeginTransaction();
        var before = GetCounts(transaction);
        var latestSequence = ScalarInt64(transaction, "SELECT COALESCE(MAX(sensor_sequence), 0) FROM events;");
        var keepFrom = Math.Max(1, latestSequence - policy.KeepEvents + 1);
        var sizeBoundary = policy.MaxBytes > 0
            ? SizeBoundary(transaction, policy.MaxBytes, rawCutoff)
            : 0;

        var data = ReadDeleteStats(
            transaction,
            "record_kind = 'Data'",
            eventCutoff,
            keepFrom,
            sizeBoundary);
        var healthDeleted = Count(
            transaction,
            "SELECT COUNT(*) FROM events WHERE record_kind = 'SourceHealth' AND time_utc < $cutoff AND sensor_sequence < $keep_from;",
            healthCutoff,
            keepFrom);
        var alertsDeleted = Count(
            transaction,
            "SELECT COUNT(*) FROM alerts WHERE created_at < $cutoff;",
            alertCutoff,
            keepFrom: null);
        var gapsDeleted = Count(
            transaction,
            "SELECT COUNT(*) FROM gaps WHERE noted_at < $cutoff;",
            gapCutoff,
            keepFrom: null);

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO retention_purges
                    (purge_id, created_at, policy_json, policy_sha256,
                     data_events_deleted, source_health_deleted, raw_xml_purged,
                     alerts_deleted, gaps_deleted)
                VALUES
                    ($purge_id, $created_at, $policy_json, $policy_sha256,
                     $data_events_deleted, $source_health_deleted, 0,
                     $alerts_deleted, $gaps_deleted);
                """;
            command.Parameters.AddWithValue("$purge_id", purgeId);
            command.Parameters.AddWithValue("$created_at", Timestamp(at));
            command.Parameters.AddWithValue("$policy_json", policy.CanonicalJson());
            command.Parameters.AddWithValue("$policy_sha256", policy.Sha256());
            command.Parameters.AddWithValue("$data_events_deleted", data.Count);
            command.Parameters.AddWithValue("$source_health_deleted", healthDeleted);
            command.Parameters.AddWithValue("$alerts_deleted", alertsDeleted);
            command.Parameters.AddWithValue("$gaps_deleted", gapsDeleted);
            command.ExecuteNonQuery();
        }

        PurgedRange? range = null;
        if (data.Count > 0)
        {
            range = new PurgedRange(purgeId, data.FirstSequence, data.LastSequence, data.Count, data.FirstTime, data.LastTime);
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO purged_ranges
                    (purge_id, first_sequence, last_sequence, event_count, first_time, last_time)
                VALUES
                    ($purge_id, $first_sequence, $last_sequence, $event_count, $first_time, $last_time);
                """;
            command.Parameters.AddWithValue("$purge_id", range.PurgeId);
            command.Parameters.AddWithValue("$first_sequence", range.FirstSequence);
            command.Parameters.AddWithValue("$last_sequence", range.LastSequence);
            command.Parameters.AddWithValue("$event_count", range.EventCount);
            command.Parameters.AddWithValue("$first_time", Timestamp(range.FirstTime));
            command.Parameters.AddWithValue("$last_time", Timestamp(range.LastTime));
            command.ExecuteNonQuery();
        }

        var deletedData = DeleteData(
            transaction,
            eventCutoff,
            keepFrom,
            sizeBoundary);
        var deletedHealth = Delete(
            transaction,
            "DELETE FROM events WHERE record_kind = 'SourceHealth' AND time_utc < $cutoff AND sensor_sequence < $keep_from;",
            healthCutoff,
            keepFrom);

        long rawPurged;
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE events
                SET raw_xml = '', raw_purged_at = $at
                WHERE raw_purged_at IS NULL
                  AND raw_xml <> ''
                  AND time_utc < $cutoff;
                """;
            command.Parameters.AddWithValue("$at", Timestamp(at));
            command.Parameters.AddWithValue("$cutoff", Timestamp(rawCutoff));
            rawPurged = command.ExecuteNonQuery();
        }

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE retention_purges SET raw_xml_purged = $raw WHERE purge_id = $purge_id;";
            command.Parameters.AddWithValue("$raw", rawPurged);
            command.Parameters.AddWithValue("$purge_id", purgeId);
            command.ExecuteNonQuery();
        }

        var deletedAlerts = Delete(
            transaction,
            "DELETE FROM alerts WHERE created_at < $cutoff;",
            alertCutoff,
            keepFrom: null);
        var deletedGaps = Delete(
            transaction,
            "DELETE FROM gaps WHERE noted_at < $cutoff;",
            gapCutoff,
            keepFrom: null);
        var retainedBytes = SumBytes(transaction);
        var overLimit = policy.MaxBytes > 0 && retainedBytes > policy.MaxBytes;

        var after = GetCounts(transaction);
        if (apply)
        {
            transaction.Commit();
        }
        else
        {
            transaction.Rollback();
        }

        return new RetentionReport(
            purgeId,
            apply,
            at,
            eventCutoff,
            rawCutoff,
            alertCutoff,
            healthCutoff,
            gapCutoff,
            keepFrom,
            before,
            after,
            deletedData,
            deletedHealth,
            rawPurged,
            deletedAlerts,
            deletedGaps,
            retainedBytes,
            overLimit,
            range);
    }

    public IReadOnlyList<PurgedRange> ReadPurgedRanges()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT purge_id, first_sequence, last_sequence, event_count, first_time, last_time
            FROM purged_ranges ORDER BY first_sequence, purge_id;
            """;
        using var reader = command.ExecuteReader();
        var ranges = new List<PurgedRange>();
        while (reader.Read())
        {
            ranges.Add(new PurgedRange(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                RetentionClock.Parse(reader.GetString(4)),
                RetentionClock.Parse(reader.GetString(5))));
        }

        return ranges;
    }

    public IReadOnlyList<RetentionAudit> ReadRetentionAudits(int limit = 100)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT p.purge_id, p.created_at, p.policy_json, p.policy_sha256,
                   p.data_events_deleted, p.source_health_deleted, p.raw_xml_purged,
                   p.alerts_deleted, p.gaps_deleted,
                   r.first_sequence, r.last_sequence, r.event_count, r.first_time, r.last_time
            FROM retention_purges p
            LEFT JOIN purged_ranges r ON r.purge_id = p.purge_id
            ORDER BY p.created_at DESC, p.purge_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var audits = new List<RetentionAudit>();
        while (reader.Read())
        {
            PurgedRange? range = null;
            if (!reader.IsDBNull(9))
            {
                range = new PurgedRange(
                    reader.GetString(0),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    reader.GetInt64(11),
                    RetentionClock.Parse(reader.GetString(12)),
                    RetentionClock.Parse(reader.GetString(13)));
            }

            audits.Add(new RetentionAudit(
                reader.GetString(0),
                RetentionClock.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                range));
        }

        return audits;
    }

    public void SetSourceState(ChannelConfig config)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO source_state
                (source_id, maximum_size_bytes, log_mode, is_enabled, log_file_path, captured_at)
            VALUES
                ($source_id, $maximum_size_bytes, $log_mode, $is_enabled, $log_file_path, $captured_at)
            ON CONFLICT(source_id) DO UPDATE SET
                maximum_size_bytes = excluded.maximum_size_bytes,
                log_mode = excluded.log_mode,
                is_enabled = excluded.is_enabled,
                log_file_path = excluded.log_file_path,
                captured_at = excluded.captured_at;
            """;
        command.Parameters.AddWithValue("$source_id", config.SourceId);
        command.Parameters.AddWithValue("$maximum_size_bytes", config.MaximumSizeInBytes);
        command.Parameters.AddWithValue("$log_mode", config.LogMode);
        command.Parameters.AddWithValue("$is_enabled", config.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$log_file_path", (object?)config.LogFilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$captured_at", Timestamp(config.CapturedAt));
        command.ExecuteNonQuery();
    }

    public ChannelConfig? GetSourceState(string sourceId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, maximum_size_bytes, log_mode, is_enabled, log_file_path, captured_at
            FROM source_state WHERE source_id = $source_id;
            """;
        command.Parameters.AddWithValue("$source_id", sourceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ChannelConfig(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetInt32(3) != 0,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public long PageCount
    {
        get
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA page_count;";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Applies a page-count cap to this connection. Used by the bounded
    /// storage test to exercise SQLITE_FULL; it is not a retention policy.
    /// </summary>
    public void SetMaxPageCount(long pages)
    {
        if (pages <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pages));
        }

        using var command = _connection.CreateCommand();
        command.CommandText = $"PRAGMA max_page_count = {pages};";
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private static LocalEvent ReadEvent(SqliteDataReader reader)
    {
        var source = new SourceIdentity(
            reader.GetString(reader.GetOrdinal("channel")),
            reader.GetString(reader.GetOrdinal("provider")),
            reader.IsDBNull(reader.GetOrdinal("provider_guid")) ? null : reader.GetString(reader.GetOrdinal("provider_guid")),
            reader.GetString(reader.GetOrdinal("incarnation_id")),
            reader.GetInt32(reader.GetOrdinal("config_generation")),
            reader.GetInt32(reader.GetOrdinal("event_id")),
            reader.GetInt64(reader.GetOrdinal("event_record_id")));

        var kind = Enum.Parse<RecordKind>(reader.GetString(reader.GetOrdinal("record_kind")));
        var processGuid = reader.IsDBNull(reader.GetOrdinal("process_guid")) ? null : reader.GetString(reader.GetOrdinal("process_guid"));
        var processPid = reader.IsDBNull(reader.GetOrdinal("process_id")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("process_id"));
        ProcessContext? process = kind == RecordKind.Data
            ? new ProcessContext(
                processGuid,
                processPid,
                reader.IsDBNull(reader.GetOrdinal("process_name")) ? null : reader.GetString(reader.GetOrdinal("process_name")),
                reader.IsDBNull(reader.GetOrdinal("process_cmd_line")) ? null : reader.GetString(reader.GetOrdinal("process_cmd_line")),
                reader.IsDBNull(reader.GetOrdinal("process_path")) ? null : reader.GetString(reader.GetOrdinal("process_path")),
                reader.IsDBNull(reader.GetOrdinal("parent_process_guid")) ? null : reader.GetString(reader.GetOrdinal("parent_process_guid")),
                reader.IsDBNull(reader.GetOrdinal("parent_process_id")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("parent_process_id")),
                reader.IsDBNull(reader.GetOrdinal("parent_process_name")) ? null : reader.GetString(reader.GetOrdinal("parent_process_name")),
                reader.IsDBNull(reader.GetOrdinal("parent_cmd_line")) ? null : reader.GetString(reader.GetOrdinal("parent_cmd_line")),
                reader.IsDBNull(reader.GetOrdinal("user_name")) ? null : reader.GetString(reader.GetOrdinal("user_name")))
            : null;

        NetworkContext? network = null;
        if (!reader.IsDBNull(reader.GetOrdinal("network_protocol"))
            || !reader.IsDBNull(reader.GetOrdinal("dst_ip")))
        {
            var outcome = reader.IsDBNull(reader.GetOrdinal("network_outcome"))
                ? NetworkOutcome.Unknown
                : Enum.Parse<NetworkOutcome>(reader.GetString(reader.GetOrdinal("network_outcome")));
            network = new NetworkContext(
                reader.IsDBNull(reader.GetOrdinal("network_protocol")) ? null : reader.GetString(reader.GetOrdinal("network_protocol")),
                reader.IsDBNull(reader.GetOrdinal("network_initiated")) ? (bool?)null : reader.GetInt32(reader.GetOrdinal("network_initiated")) != 0,
                reader.IsDBNull(reader.GetOrdinal("src_ip")) ? null : reader.GetString(reader.GetOrdinal("src_ip")),
                reader.IsDBNull(reader.GetOrdinal("src_port")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("src_port")),
                reader.IsDBNull(reader.GetOrdinal("src_hostname")) ? null : reader.GetString(reader.GetOrdinal("src_hostname")),
                reader.IsDBNull(reader.GetOrdinal("dst_ip")) ? null : reader.GetString(reader.GetOrdinal("dst_ip")),
                reader.IsDBNull(reader.GetOrdinal("dst_port")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("dst_port")),
                reader.IsDBNull(reader.GetOrdinal("dst_hostname")) ? null : reader.GetString(reader.GetOrdinal("dst_hostname")),
                outcome);
        }

        var unmapped = JsonSerializer.Deserialize<List<UnmappedField>>(
            reader.GetString(reader.GetOrdinal("unmapped_json"))) ?? [];

        return new LocalEvent(
            reader.GetString(reader.GetOrdinal("contract_version")),
            reader.GetString(reader.GetOrdinal("sensor_id")),
            reader.GetString(reader.GetOrdinal("host_id")),
            source,
            reader.GetInt64(reader.GetOrdinal("sensor_sequence")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("time_utc")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(reader.GetOrdinal("logged_time_utc"))
                ? null
                : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("logged_time_utc")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("processed_time_utc")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetString(reader.GetOrdinal("clock_status")),
            kind,
            reader.IsDBNull(reader.GetOrdinal("ocsf_json")) ? null : reader.GetString(reader.GetOrdinal("ocsf_json")),
            reader.IsDBNull(reader.GetOrdinal("ocsf_class_uid")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("ocsf_class_uid")),
            reader.IsDBNull(reader.GetOrdinal("ocsf_activity_id")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("ocsf_activity_id")),
            unmapped,
            reader.GetString(reader.GetOrdinal("raw_xml")),
            reader.GetString(reader.GetOrdinal("raw_sha256")),
            reader.IsDBNull(reader.GetOrdinal("gap_id")) ? null : reader.GetString(reader.GetOrdinal("gap_id")),
            process,
            network,
            reader.IsDBNull(reader.GetOrdinal("host_name")) ? null : reader.GetString(reader.GetOrdinal("host_name")),
            reader.IsDBNull(reader.GetOrdinal("user_name")) ? null : reader.GetString(reader.GetOrdinal("user_name")),
            reader.IsDBNull(reader.GetOrdinal("raw_purged_at"))
                ? null
                : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("raw_purged_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static void AddEventParameters(SqliteCommand command, LocalEvent value)
    {
        command.Parameters.AddWithValue("$source_key", value.SourceKey);
        command.Parameters.AddWithValue("$contract_version", value.ContractVersion);
        command.Parameters.AddWithValue("$sensor_id", value.SensorId);
        command.Parameters.AddWithValue("$host_id", value.HostId);
        command.Parameters.AddWithValue("$channel", value.Source.Channel);
        command.Parameters.AddWithValue("$provider", value.Source.Provider);
        command.Parameters.AddWithValue("$provider_guid", (object?)value.Source.ProviderGuid ?? DBNull.Value);
        command.Parameters.AddWithValue("$incarnation_id", value.Source.IncarnationId);
        command.Parameters.AddWithValue("$config_generation", value.Source.ConfigGeneration);
        command.Parameters.AddWithValue("$event_id", value.Source.EventId);
        command.Parameters.AddWithValue("$event_record_id", value.Source.EventRecordId);
        command.Parameters.AddWithValue("$time_utc", Timestamp(value.Time));
        command.Parameters.AddWithValue(
            "$logged_time_utc",
            value.LoggedTime is null ? DBNull.Value : Timestamp(value.LoggedTime.Value));
        command.Parameters.AddWithValue("$processed_time_utc", Timestamp(value.ProcessedTime));
        command.Parameters.AddWithValue("$clock_status", value.ClockStatus);
        command.Parameters.AddWithValue("$record_kind", value.Kind.ToString());
        command.Parameters.AddWithValue("$ocsf_json", (object?)value.OcsfJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$ocsf_class_uid", (object?)value.OcsfClassUid ?? DBNull.Value);
        command.Parameters.AddWithValue("$ocsf_activity_id", (object?)value.OcsfActivityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$unmapped_json", JsonSerializer.Serialize(value.Unmapped));
        command.Parameters.AddWithValue("$raw_xml", value.RawXml);
        command.Parameters.AddWithValue("$raw_sha256", value.RawSha256);
        command.Parameters.AddWithValue("$gap_id", (object?)value.GapId ?? DBNull.Value);
        command.Parameters.AddWithValue("$host_name", (object?)value.HostName ?? DBNull.Value);
        command.Parameters.AddWithValue("$user_name", (object?)value.UserName ?? DBNull.Value);
        command.Parameters.AddWithValue("$process_guid", (object?)value.Process?.Guid ?? DBNull.Value);
        command.Parameters.AddWithValue("$process_id", (object?)value.Process?.Pid ?? DBNull.Value);
        command.Parameters.AddWithValue("$process_name", (object?)value.Process?.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$process_cmd_line", (object?)value.Process?.CommandLine ?? DBNull.Value);
        command.Parameters.AddWithValue("$process_path", (object?)value.Process?.ImagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$parent_process_guid", (object?)value.Process?.ParentGuid ?? DBNull.Value);
        command.Parameters.AddWithValue("$parent_process_id", (object?)value.Process?.ParentPid ?? DBNull.Value);
        command.Parameters.AddWithValue("$parent_process_name", (object?)value.Process?.ParentName ?? DBNull.Value);
        command.Parameters.AddWithValue("$parent_cmd_line", (object?)value.Process?.ParentCommandLine ?? DBNull.Value);
        command.Parameters.AddWithValue("$network_protocol", (object?)value.Network?.Protocol ?? DBNull.Value);
        command.Parameters.AddWithValue("$network_initiated", value.Network?.Initiated is null ? DBNull.Value : (object)(value.Network.Initiated.Value ? 1 : 0));
        command.Parameters.AddWithValue("$network_outcome", value.Network is null ? DBNull.Value : value.Network.Outcome.ToString());
        command.Parameters.AddWithValue("$src_ip", (object?)value.Network?.SourceIp ?? DBNull.Value);
        command.Parameters.AddWithValue("$src_port", (object?)value.Network?.SourcePort ?? DBNull.Value);
        command.Parameters.AddWithValue("$src_hostname", (object?)value.Network?.SourceHostname ?? DBNull.Value);
        command.Parameters.AddWithValue("$dst_ip", (object?)value.Network?.DestinationIp ?? DBNull.Value);
        command.Parameters.AddWithValue("$dst_port", (object?)value.Network?.DestinationPort ?? DBNull.Value);
        command.Parameters.AddWithValue("$dst_hostname", (object?)value.Network?.DestinationHostname ?? DBNull.Value);
    }

    private sealed record DeleteStats(long Count, long FirstSequence, long LastSequence, DateTimeOffset FirstTime, DateTimeOffset LastTime);

    private DeleteStats ReadDeleteStats(
        SqliteTransaction transaction,
        string kindPredicate,
        DateTimeOffset cutoff,
        long keepFrom,
        long sizeBoundary)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COUNT(*), COALESCE(MIN(sensor_sequence), 0), COALESCE(MAX(sensor_sequence), 0),
                   MIN(time_utc), MAX(time_utc)
            FROM events
            WHERE {kindPredicate}
              AND sensor_sequence < $keep_from
              AND (time_utc < $cutoff OR ($size_boundary > 0 AND sensor_sequence <= $size_boundary));
            """;
        command.Parameters.AddWithValue("$cutoff", Timestamp(cutoff));
        command.Parameters.AddWithValue("$keep_from", keepFrom);
        command.Parameters.AddWithValue("$size_boundary", sizeBoundary);
        using var reader = command.ExecuteReader();
        reader.Read();
        var count = reader.GetInt64(0);
        if (count == 0)
        {
            return new DeleteStats(0, 0, 0, cutoff, cutoff);
        }

        return new DeleteStats(
            count,
            reader.GetInt64(1),
            reader.GetInt64(2),
            RetentionClock.Parse(reader.GetString(3)),
            RetentionClock.Parse(reader.GetString(4)));
    }

    private long Count(SqliteTransaction transaction, string sql, DateTimeOffset cutoff, long? keepFrom)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$cutoff", Timestamp(cutoff));
        if (keepFrom.HasValue)
        {
            command.Parameters.AddWithValue("$keep_from", keepFrom.Value);
        }

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private long Delete(SqliteTransaction transaction, string sql, DateTimeOffset cutoff, long? keepFrom)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$cutoff", Timestamp(cutoff));
        if (keepFrom.HasValue)
        {
            command.Parameters.AddWithValue("$keep_from", keepFrom.Value);
        }

        return command.ExecuteNonQuery();
    }

    private long DeleteData(SqliteTransaction transaction, DateTimeOffset cutoff, long keepFrom, long sizeBoundary)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM events
            WHERE record_kind = 'Data'
              AND sensor_sequence < $keep_from
              AND (time_utc < $cutoff OR ($size_boundary > 0 AND sensor_sequence <= $size_boundary));
            """;
        command.Parameters.AddWithValue("$cutoff", Timestamp(cutoff));
        command.Parameters.AddWithValue("$keep_from", keepFrom);
        command.Parameters.AddWithValue("$size_boundary", sizeBoundary);
        return command.ExecuteNonQuery();
    }

    private long SizeBoundary(SqliteTransaction transaction, long maxBytes, DateTimeOffset rawCutoff)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH sized AS (
                SELECT sensor_sequence,
                       (length(CASE WHEN raw_purged_at IS NULL AND time_utc < $raw_cutoff THEN '' ELSE raw_xml END)
                        + COALESCE(length(ocsf_json), 0)
                        + length(unmapped_json)
                        + COALESCE(length(process_cmd_line), 0)
                        + COALESCE(length(parent_cmd_line), 0)
                        + 256) AS bytes
                FROM events
                WHERE record_kind = 'Data'
            ),
            cumulative AS (
                SELECT sensor_sequence,
                       SUM(bytes) OVER (ORDER BY sensor_sequence DESC ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS total
                FROM sized
            )
            SELECT COALESCE(MAX(sensor_sequence), 0)
            FROM cumulative
            WHERE total > $max_bytes;
            """;
        command.Parameters.AddWithValue("$max_bytes", maxBytes);
        command.Parameters.AddWithValue("$raw_cutoff", Timestamp(rawCutoff));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private long SumBytes(SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COALESCE(SUM({EventBytesSql}), 0) FROM events WHERE record_kind = 'Data';";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private long ScalarInt64(SqliteTransaction transaction, string sql)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void SetMetaIfMissing(string key, string value)
    {
        if (GetMeta(key) is null)
        {
            SetMeta(key, value);
        }
    }

    private void SetMeta(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meta(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void EnsureColumn(string table, string column, string declaration)
    {
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({table});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        alter.ExecuteNonQuery();
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private const string EventBytesSql = """
        (length(raw_xml)
         + COALESCE(length(ocsf_json), 0)
         + length(unmapped_json)
         + COALESCE(length(process_cmd_line), 0)
         + COALESCE(length(parent_cmd_line), 0)
         + 256)
        """;

    private const string InsertEventSql = """
        INSERT INTO events (
            source_key, contract_version, sensor_id, host_id, channel, provider,
            provider_guid, incarnation_id, config_generation, event_id, event_record_id,
            time_utc, logged_time_utc, processed_time_utc, clock_status, record_kind,
            ocsf_json, ocsf_class_uid, ocsf_activity_id, unmapped_json, raw_xml,
            raw_sha256, gap_id, host_name, user_name, process_guid, process_id,
            process_name, process_cmd_line, process_path, parent_process_guid,
            parent_process_id, parent_process_name, parent_cmd_line, network_protocol,
            network_initiated, network_outcome, src_ip, src_port, src_hostname, dst_ip, dst_port, dst_hostname)
        VALUES (
            $source_key, $contract_version, $sensor_id, $host_id, $channel, $provider,
            $provider_guid, $incarnation_id, $config_generation, $event_id, $event_record_id,
            $time_utc, $logged_time_utc, $processed_time_utc, $clock_status, $record_kind,
            $ocsf_json, $ocsf_class_uid, $ocsf_activity_id, $unmapped_json, $raw_xml,
            $raw_sha256, $gap_id, $host_name, $user_name, $process_guid, $process_id,
            $process_name, $process_cmd_line, $process_path, $parent_process_guid,
            $parent_process_id, $parent_process_name, $parent_cmd_line, $network_protocol,
            $network_initiated, $network_outcome, $src_ip, $src_port, $src_hostname, $dst_ip, $dst_port, $dst_hostname)
        ON CONFLICT(source_key) DO NOTHING;
        """;
}
