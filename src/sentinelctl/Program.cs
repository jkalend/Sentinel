using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sentinel.Core.Fixture;
using Sentinel.Core.Fleet;
using Sentinel.Core.Model;
using Sentinel.Core.Ocsf;
using Sentinel.Core.Rules;
using Sentinel.Core.Runs;
using Sentinel.Core.Store;
using Sentinel.Core.Transport;
using Sentinel.Core.Sysmon;
using Sentinel.Core.Timeline;

var exitCode = 0;

try
{
    if (args.Length == 0)
    {
        Usage();
        return 2;
    }

    var command = args[0].ToLowerInvariant();
    var options = ParseOptions(args.Skip(1).ToArray());

    switch (command)
    {
        case "init":
            Init(options);
            break;
        case "enroll":
            Enroll(options);
            break;
        case "fleet":
            Fleet(options);
            break;
        case "collector":
            Collector(options);
            break;
        case "push":
            Push(options);
            break;
        case "ingest":
            Ingest(options);
            break;
        case "timeline":
            Timeline(options);
            break;
        case "replay":
            Replay(options);
            break;
        case "gaps":
            Gaps(options);
            break;
        case "status":
            Status(options);
            break;
        case "purge":
            Purge(options);
            break;
        case "purges":
            Purges(options);
            break;
        case "run":
            Run(options);
            break;
        case "verify-run":
            exitCode = VerifyRun(options);
            break;
        case "backup":
            Backup(options);
            break;
        case "restore":
            Restore(options);
            break;
        case "verify":
            exitCode = Verify(options);
            break;
        case "help":
        case "--help":
        case "-h":
            Usage();
            break;
        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            Usage();
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

return exitCode;

static void Init(Dictionary<string, string?> options)
{
    using var store = OpenStore(options);
    store.Initialize();
    var (sensorId, hostId) = store.EnsureIdentity(Get(options, "sensor-id"), Get(options, "host-id"));
    Console.WriteLine($"database: {Get(options, "db") ?? "data/sentinel.db"}");
    Console.WriteLine($"contract: {SentinelVersions.ContractVersion}");
    Console.WriteLine($"sensor_id: {sensorId}");
    Console.WriteLine($"host_id: {hostId}");
}

static void Enroll(Dictionary<string, string?> options)
{
    var sensorId = Get(options, "sensor-id")
        ?? throw new ArgumentException("--sensor-id is required.");
    var hostId = Get(options, "host-id")
        ?? throw new ArgumentException("--host-id is required.");
    using var store = OpenStore(options);
    store.Initialize();
    var enrollment = store.Enroll(
        sensorId,
        hostId,
        Get(options, "host-name"),
        Integer(options, "config-generation", 1),
        Long(options, "clock-offset-ms", 0),
        Long(options, "clock-uncertainty-ms", 0));
    Console.WriteLine($"sensor_id: {enrollment.SensorId}");
    Console.WriteLine($"host_id: {enrollment.HostId}");
    Console.WriteLine($"host_name: {enrollment.HostName ?? ""}");
    Console.WriteLine($"clock_offset_ms: {enrollment.ClockOffsetMs}");
    Console.WriteLine($"clock_uncertainty_ms: {enrollment.ClockUncertaintyMs}");
}

static void Fleet(Dictionary<string, string?> options)
{
    var paths = (Get(options, "dbs") ?? Get(options, "db")
        ?? throw new ArgumentException("--dbs is required."))
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var report = FleetService.Inspect(paths);
    var format = (Get(options, "format") ?? "table").ToLowerInvariant();
    Console.Write(format switch
    {
        "json" => FleetService.RenderJson(report),
        "table" => FleetService.RenderTable(report),
        _ => throw new ArgumentException("--format must be table or json.")
    });
}

static void Collector(Dictionary<string, string?> options)
{
    var prefix = Get(options, "listen")
        ?? throw new ArgumentException("--listen is required.");
    var root = Get(options, "root")
        ?? throw new ArgumentException("--root is required.");
    var rules = Get(options, "rules") is { } rulesPath
        ? RuleLoader.LoadDirectory(rulesPath)
        : null;
    var allowed = Get(options, "allow-sensors")?
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    using var collector = new TransportCollector(prefix, root, rules, Get(options, "token"), allowed);
    collector.Start();
    Console.WriteLine($"collector: {collector.Prefix}");
    Console.WriteLine($"root: {Path.GetFullPath(root)}");
    Console.WriteLine("press Ctrl+C to stop");
    var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        stop.Set();
    };
    stop.Wait();
    collector.StopAsync().GetAwaiter().GetResult();
}

static void Push(Dictionary<string, string?> options)
{
    var collector = Get(options, "collector")
        ?? throw new ArgumentException("--collector is required.");
    var sender = new TransportSender(
        Get(options, "db") ?? "data/sentinel.db",
        collector,
        Get(options, "token"),
        Integer(options, "batch-size", 100),
        Integer(options, "retries", 3));
    if (options.ContainsKey("reset-ack"))
    {
        sender.ResetAck();
    }

    var result = sender.PushAllAsync().GetAwaiter().GetResult();
    Console.WriteLine($"batches: {result.Batches}");
    Console.WriteLine($"accepted: {result.Accepted}");
    Console.WriteLine($"duplicates: {result.Duplicates}");
    Console.WriteLine($"last_acked_sequence: {result.LastAckedSequence}");
}

static void Ingest(Dictionary<string, string?> options)
{
    var fixturePath = Get(options, "fixture")
        ?? throw new ArgumentException("--fixture is required.");
    var rulesPath = Get(options, "rules") ?? "rules";

    var fixture = FixtureSet.Load(fixturePath);
    var rules = RuleLoader.LoadDirectory(rulesPath);
    using var store = OpenStore(options);
    store.Initialize();
    var (sensorId, hostId) = store.EnsureIdentity(
        Get(options, "sensor-id") ?? fixture.SensorId,
        Get(options, "host-id") ?? fixture.HostId);
    var service = new IngestionService(store, sensorId, hostId);

    var report = service.Ingest(
        fixture.Records,
        rules,
        fixture.Channel,
        fixture.IncarnationId,
        fixture.ConfigGeneration,
        fixture.GroundTruth);

    Console.WriteLine($"fixture: {fixture.Root}");
    Console.WriteLine($"records: {fixture.Records.Count}");
    Console.WriteLine($"accepted: {report.Accepted}");
    Console.WriteLine($"duplicates: {report.Duplicates}");
    Console.WriteLine($"source_health: {report.SourceHealth}");
    Console.WriteLine($"alerts: {report.Alerts}");
    Console.WriteLine($"gaps: {report.Gaps}");
    Console.WriteLine($"first_sensor_sequence: {report.FirstSequence?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
    Console.WriteLine($"last_sensor_sequence: {report.LastSequence?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
}

static void Timeline(Dictionary<string, string?> options)
{
    using var store = OpenStore(options);
    store.Initialize();
    Console.Write(TimelineBuilder.Render(store.ReadEvents(), store.ReadPurgedRanges()));
}

static void Replay(Dictionary<string, string?> options)
{
    var rulesPath = Get(options, "rules") ?? "rules";
    var rules = RuleLoader.LoadDirectory(rulesPath);
    using var store = OpenStore(options);
    store.Initialize();
    var report = ReplayService.Replay(store, rules);
    Console.WriteLine($"live_alerts: {report.LiveAlerts}");
    Console.WriteLine($"replay_alerts: {report.ReplayAlerts}");
    Console.WriteLine($"matches: {report.Matches.ToString().ToLowerInvariant()}");
    foreach (var alert in report.ReplayOnly)
    {
        Console.WriteLine($"replay_only: {alert.RuleId}@{alert.RuleVersion} {alert.SourceKey}");
    }

    foreach (var alert in report.LiveOnly)
    {
        Console.WriteLine($"live_only: {alert.RuleId}@{alert.RuleVersion} {alert.SourceKey}");
    }

    if (!report.Matches)
    {
        throw new InvalidOperationException("Replay results do not match the stored alert set.");
    }
}

static void Gaps(Dictionary<string, string?> options)
{
    using var store = OpenStore(options);
    store.Initialize();
    var bookmark = store.GetBookmark(SysmonXmlReader.SysmonChannel);
    if (bookmark is null)
    {
        Console.WriteLine("coverage: unknown (no bookmark)");
    }
    else
    {
        Console.WriteLine($"bookmark: incarnation={bookmark.IncarnationId} record={bookmark.EventRecordId} coverage_start={bookmark.CoverageStartRecordId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
    }

    var gaps = store.ReadGaps();
    Console.WriteLine($"gaps: {gaps.Count}");
    foreach (var gap in gaps)
    {
        Console.WriteLine($"{gap.Kind} confidence={gap.Confidence} start={gap.StartRecordId?.ToString(CultureInfo.InvariantCulture) ?? "?"} end={gap.EndRecordId?.ToString(CultureInfo.InvariantCulture) ?? "?"} {gap.Details}");
    }
}

static void Status(Dictionary<string, string?> options)
{
    var databasePath = Get(options, "db") ?? "data/sentinel.db";
    if (!File.Exists(databasePath))
    {
        throw new FileNotFoundException($"database not found: {databasePath}");
    }

    using var store = new SqliteEventStore(databasePath, readOnly: true);
    var (counts, bookmark) = store.GetStatus(SysmonXmlReader.SysmonChannel);

    Console.WriteLine($"events: {counts.Events}");
    Console.WriteLine($"alerts: {counts.Alerts}");
    Console.WriteLine($"gaps: {counts.Gaps}");
    if (bookmark is null)
    {
        Console.WriteLine("bookmark: none");
        return;
    }

    var coverageStart = bookmark.CoverageStartRecordId?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
    Console.WriteLine($"bookmark: incarnation={bookmark.IncarnationId} record={bookmark.EventRecordId} generation={bookmark.ConfigGeneration} coverage_start={coverageStart}");
}

static void Purge(Dictionary<string, string?> options)
{
    var policy = new RetentionPolicy(
        Integer(options, "event-days", 30),
        Integer(options, "raw-days", 7),
        Integer(options, "alert-days", 90),
        Integer(options, "health-days", 90),
        Integer(options, "gap-days", 90),
        Integer(options, "keep-events", 1000),
        Long(options, "max-bytes", 10L * 1024 * 1024 * 1024));
    var now = Get(options, "now") is { } text
        ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        : DateTimeOffset.UtcNow;
    var apply = options.ContainsKey("apply");

    using var store = OpenStore(options);
    store.Initialize();
    var report = store.Purge(policy, now, apply);
    Console.WriteLine($"mode: {(apply ? "apply" : "dry-run")}");
    Console.WriteLine($"policy: {policy.CanonicalJson()}");
    Console.WriteLine($"policy_sha256: {policy.Sha256()}");
    Console.WriteLine($"keep_from_sequence: {report.KeepFromSequence}");
    Console.WriteLine($"event_cutoff: {report.EventCutoff:O}");
    Console.WriteLine($"raw_cutoff: {report.RawCutoff:O}");
    Console.WriteLine($"alert_cutoff: {report.AlertCutoff:O}");
    Console.WriteLine($"health_cutoff: {report.SourceHealthCutoff:O}");
    Console.WriteLine($"gap_cutoff: {report.GapCutoff:O}");
    Console.WriteLine($"before: events={report.Before.Events} alerts={report.Before.Alerts} gaps={report.Before.Gaps}");
    Console.WriteLine($"after: events={report.After.Events} alerts={report.After.Alerts} gaps={report.After.Gaps}");
    Console.WriteLine($"data_events_deleted: {report.DataEventsDeleted}");
    Console.WriteLine($"source_health_deleted: {report.SourceHealthDeleted}");
    Console.WriteLine($"raw_xml_purged: {report.RawXmlPurged}");
    Console.WriteLine($"alerts_deleted: {report.AlertsDeleted}");
    Console.WriteLine($"gaps_deleted: {report.GapsDeleted}");
    Console.WriteLine($"retained_bytes: {report.RetainedBytes}");
    Console.WriteLine($"over_limit: {report.OverLimit.ToString().ToLowerInvariant()}");
    if (report.Range is not null)
    {
        Console.WriteLine($"purged_range: {report.Range.FirstSequence}-{report.Range.LastSequence} ({report.Range.EventCount} events)");
    }
}

static void Purges(Dictionary<string, string?> options)
{
    using var store = OpenStore(options);
    store.Initialize();
    var audits = store.ReadRetentionAudits(Integer(options, "limit", 100));
    Console.WriteLine($"purges: {audits.Count}");
    foreach (var audit in audits)
    {
        var range = audit.Range is null
            ? string.Empty
            : $" range={audit.Range.FirstSequence}-{audit.Range.LastSequence}";
        Console.WriteLine(
            $"{audit.CreatedAt:O} {audit.PurgeId} data={audit.DataEventsDeleted} " +
            $"health={audit.SourceHealthDeleted} raw={audit.RawXmlPurged} " +
            $"alerts={audit.AlertsDeleted} gaps={audit.GapsDeleted}{range} " +
            $"policy_sha256={audit.PolicySha256}");
    }
}

static void Backup(Dictionary<string, string?> options)
{
    var databasePath = Get(options, "db") ?? "data/sentinel.db";
    var output = Get(options, "output")
        ?? throw new ArgumentException("--output is required.");
    var report = BackupService.Create(databasePath, output);
    Console.WriteLine($"backup: {report.BackupPath}");
    Console.WriteLine($"manifest: {report.ManifestPath}");
    Console.WriteLine($"sha256: {report.Manifest.DatabaseSha256}");
    Console.WriteLine($"bytes: {report.Manifest.DatabaseBytes}");
    Console.WriteLine($"events: {report.Manifest.Counts.Events}");
    Console.WriteLine($"alerts: {report.Manifest.Counts.Alerts}");
    Console.WriteLine($"gaps: {report.Manifest.Counts.Gaps}");
    if (report.Manifest.BookmarkSource is not null)
    {
        Console.WriteLine(
            $"bookmark: incarnation={report.Manifest.BookmarkIncarnation} " +
            $"record={report.Manifest.BookmarkRecordId} " +
            $"coverage_start={report.Manifest.BookmarkCoverageStart?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
    }
}

static void Restore(Dictionary<string, string?> options)
{
    var backupPath = Get(options, "backup")
        ?? throw new ArgumentException("--backup is required.");
    var databasePath = Get(options, "db") ?? "data/sentinel.db";
    var report = BackupService.Restore(backupPath, databasePath);
    Console.WriteLine($"restored: {report.DatabasePath}");
    Console.WriteLine($"from: {report.BackupPath}");
    Console.WriteLine($"sha256: {report.Manifest.DatabaseSha256}");
    Console.WriteLine($"events: {report.Manifest.Counts.Events}");
}

static void Run(Dictionary<string, string?> options)
{
    var output = Get(options, "output")
        ?? throw new ArgumentException("--output is required.");
    var rules = RuleLoader.LoadDirectory(Get(options, "rules") ?? "rules");
    var schema = ResolveSchemaRoot(Get(options, "schema"));
    using var store = OpenStore(options);
    store.Initialize();
    var selected = Get(options, "source-keys")?
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var includePayload = options.ContainsKey("include-payload") || selected is { Length: > 0 };
    var manifest = RunService.Create(store, rules, schema, output, includePayload, selected);
    Console.WriteLine($"run_id: {manifest.RunId}");
    Console.WriteLine($"manifest: {Path.Combine(Path.GetFullPath(output), "manifest.json")}");
    Console.WriteLine($"content_sha256: {manifest.ContentSha256}");
    Console.WriteLine($"manifest_sha256: {manifest.ManifestSha256}");
    Console.WriteLine($"events: {manifest.Coverage.EventCount}");
    Console.WriteLine($"sources: {manifest.Coverage.SourceCount}");
    Console.WriteLine($"alerts: {manifest.Coverage.AlertCount}");
    Console.WriteLine($"gaps: {manifest.Coverage.GapCount}");
    Console.WriteLine($"export: {manifest.Export.Mode}");
}

static int VerifyRun(Dictionary<string, string?> options)
{
    var manifestPath = Get(options, "manifest")
        ?? throw new ArgumentException("--manifest is required.");
    var manifest = RunService.ReadManifest(manifestPath);
    SqliteEventStore? store = null;
    try
    {
        var databasePath = Get(options, "db");
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException($"database not found: {databasePath}");
            }

            store = new SqliteEventStore(databasePath, readOnly: true);
        }

        var verification = RunService.Verify(manifest, Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, store);
        Console.WriteLine($"manifest_valid: {verification.ManifestValid.ToString().ToLowerInvariant()}");
        Console.WriteLine($"content_valid: {verification.ContentValid.ToString().ToLowerInvariant()}");
        Console.WriteLine($"payload_valid: {verification.PayloadValid.ToString().ToLowerInvariant()}");
        Console.WriteLine($"included: {verification.Included}");
        Console.WriteLine($"missing: {verification.Missing}");
        Console.WriteLine($"changed: {verification.Changed}");
        Console.WriteLine($"redacted: {verification.Redacted}");
        foreach (var message in verification.Messages)
        {
            Console.WriteLine(message);
        }

        return verification.Ok ? 0 : 1;
    }
    finally
    {
        store?.Dispose();
    }
}

static int Verify(Dictionary<string, string?> options)
{
    var schemaRoot = ResolveSchemaRoot(Get(options, "schema"));
    var validator = new OcsfSchemaValidator(schemaRoot);
    using var store = OpenStore(options);
    store.Initialize();
    var errors = new List<string>();
    var rawPurged = 0;
    foreach (var value in store.ReadEvents())
    {
        if (value.ContractVersion != SentinelVersions.ContractVersion)
        {
            errors.Add($"{value.SourceKey}: contract_version '{value.ContractVersion}'.");
        }

        if (value.RawPurgedAt is not null)
        {
            rawPurged++;
            if (value.RawXml.Length != 0)
            {
                errors.Add($"{value.SourceKey}: raw_xml is present after raw_purged_at.");
            }
        }
        else
        {
            var recomputed = IngestionService.Sha256Hex(value.RawXml);
            if (!string.Equals(recomputed, value.RawSha256, StringComparison.Ordinal))
            {
                errors.Add($"{value.SourceKey}: raw_sha256 does not match raw_xml.");
            }
        }

        if (value.Kind != RecordKind.Data || value.OcsfJson is null)
        {
            continue;
        }

        var ocsf = JsonNode.Parse(value.OcsfJson) as JsonObject;
        if (ocsf is null)
        {
            errors.Add($"{value.SourceKey}: ocsf_json is not an object.");
            continue;
        }

        var classPath = value.OcsfClassUid switch
        {
            OcsfMapper.ProcessActivityClassUid => "events/system/process_activity.json",
            OcsfMapper.NetworkActivityClassUid => "events/network/network_activity.json",
            _ => null
        };
        if (classPath is null)
        {
            errors.Add($"{value.SourceKey}: unsupported OCSF class_uid {value.OcsfClassUid}.");
            continue;
        }

        errors.AddRange(validator.Validate(ocsf, classPath).Select(error => $"{value.SourceKey}: {error}"));
    }

    Console.WriteLine($"schema: {schemaRoot}");
    Console.WriteLine($"raw_purged: {rawPurged}");
    Console.WriteLine($"errors: {errors.Count}");
    foreach (var error in errors)
    {
        Console.WriteLine(error);
    }

    return errors.Count == 0 ? 0 : 1;
}

static SqliteEventStore OpenStore(Dictionary<string, string?> options) =>
    new(Get(options, "db") ?? "data/sentinel.db");

static string ResolveSchemaRoot(string? explicitPath)
{
    if (!string.IsNullOrWhiteSpace(explicitPath))
    {
        return Path.GetFullPath(explicitPath);
    }

    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "schemas", "ocsf-1.9.0");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    var local = Path.Combine(Directory.GetCurrentDirectory(), "schemas", "ocsf-1.9.0");
    if (Directory.Exists(local))
    {
        return local;
    }

    throw new DirectoryNotFoundException("OCSF schema directory not found; pass --schema.");
}

static Dictionary<string, string?> ParseOptions(string[] args)
{
    var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        var arg = args[index];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument '{arg}'.");
        }

        var key = arg[2..];
        if (key.Length == 0)
        {
            throw new ArgumentException("Empty option name.");
        }

        if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            options[key] = args[++index];
        }
        else
        {
            options[key] = null;
        }
    }

    return options;
}

static string? Get(Dictionary<string, string?> options, string key) =>
    options.TryGetValue(key, out var value) ? value : null;

static int Integer(Dictionary<string, string?> options, string key, int defaultValue)
{
    var text = Get(options, key);
    if (text is null)
    {
        return defaultValue;
    }

    return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value
        : throw new ArgumentException($"--{key} must be an integer.");
}

static long Long(Dictionary<string, string?> options, string key, long defaultValue)
{
    var text = Get(options, key);
    if (text is null)
    {
        return defaultValue;
    }

    return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value
        : throw new ArgumentException($"--{key} must be an integer.");
}

static void Usage()
{
    Console.WriteLine("sentinelctl <command> [options]");
    Console.WriteLine();
    Console.WriteLine("  init      --db <path> [--sensor-id <uuid>] [--host-id <uuid>]");
    Console.WriteLine("  enroll    --db <path> --sensor-id <id> --host-id <id> [--host-name <name>]");
    Console.WriteLine("            [--config-generation 1] [--clock-offset-ms 0] [--clock-uncertainty-ms 0]");
    Console.WriteLine("  fleet     --dbs <path;path> [--format table|json]");
    Console.WriteLine("  collector --listen <http://127.0.0.1:port/> --root <dir> [--rules <dir>]");
    Console.WriteLine("            [--token <token>] [--allow-sensors <id;id>]");
    Console.WriteLine("  push      --db <path> --collector <http://127.0.0.1:port/> [--token <token>] [--batch-size 100] [--retries 3] [--reset-ack]");
    Console.WriteLine("  ingest    --fixture <dir> [--db <path>] [--rules <dir>] [--sensor-id <id>] [--host-id <id>]");
    Console.WriteLine("  timeline  [--db <path>]");
    Console.WriteLine("  replay    [--db <path>] [--rules <dir>]");
    Console.WriteLine("  gaps      [--db <path>]");
    Console.WriteLine("  status    [--db <path>]");
    Console.WriteLine("  purge     [--db <path>] [--event-days 30] [--raw-days 7] [--alert-days 90]");
    Console.WriteLine("            [--health-days 90] [--gap-days 90] [--keep-events 1000] [--max-bytes 10737418240]");
    Console.WriteLine("            [--now <iso>] [--apply]");
    Console.WriteLine("  purges    [--db <path>] [--limit <n>]");
    Console.WriteLine("  backup    --db <path> --output <path>");
    Console.WriteLine("  restore   --backup <path> [--db <path>]");
    Console.WriteLine("  run       --db <path> --output <dir> [--rules <dir>] [--schema <dir>]");
    Console.WriteLine("            [--include-payload] [--source-keys <key;key>]");
    Console.WriteLine("  verify-run --manifest <path> [--db <path>]");
    Console.WriteLine("  verify    [--db <path>] [--schema <dir>]");
}
