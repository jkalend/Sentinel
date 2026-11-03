using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sentinel.Core.Fixture;
using Sentinel.Core.Model;
using Sentinel.Core.Ocsf;
using Sentinel.Core.Rules;
using Sentinel.Core.Store;
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

static void Ingest(Dictionary<string, string?> options)
{
    var fixturePath = Get(options, "fixture")
        ?? throw new ArgumentException("--fixture is required.");
    var rulesPath = Get(options, "rules") ?? "rules";

    var fixture = FixtureSet.Load(fixturePath);
    var rules = RuleLoader.LoadDirectory(rulesPath);
    using var store = OpenStore(options);
    store.Initialize();
    var (sensorId, hostId) = store.EnsureIdentity(fixture.SensorId, fixture.HostId);
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
    Console.Write(TimelineBuilder.Render(store.ReadEvents()));
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
    using var store = OpenStore(options);
    store.Initialize();
    var events = store.ReadEvents();
    var alerts = store.ReadAlerts();
    var gaps = store.ReadGaps();
    var bookmark = store.GetBookmark(SysmonXmlReader.SysmonChannel);

    Console.WriteLine($"events: {events.Count}");
    Console.WriteLine($"alerts: {alerts.Count}");
    Console.WriteLine($"gaps: {gaps.Count}");
    if (bookmark is null)
    {
        Console.WriteLine("bookmark: none");
        return;
    }

    var coverageStart = bookmark.CoverageStartRecordId?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
    Console.WriteLine($"bookmark: incarnation={bookmark.IncarnationId} record={bookmark.EventRecordId} generation={bookmark.ConfigGeneration} coverage_start={coverageStart}");
}

static int Verify(Dictionary<string, string?> options)
{
    var schemaRoot = ResolveSchemaRoot(Get(options, "schema"));
    var validator = new OcsfSchemaValidator(schemaRoot);
    using var store = OpenStore(options);
    store.Initialize();
    var errors = new List<string>();
    foreach (var value in store.ReadEvents())
    {
        if (value.ContractVersion != SentinelVersions.ContractVersion)
        {
            errors.Add($"{value.SourceKey}: contract_version '{value.ContractVersion}'.");
        }

        var recomputed = IngestionService.Sha256Hex(value.RawXml);
        if (!string.Equals(recomputed, value.RawSha256, StringComparison.Ordinal))
        {
            errors.Add($"{value.SourceKey}: raw_sha256 does not match raw_xml.");
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

static void Usage()
{
    Console.WriteLine("sentinelctl <command> [options]");
    Console.WriteLine();
    Console.WriteLine("  init      --db <path> [--sensor-id <uuid>] [--host-id <uuid>]");
    Console.WriteLine("  ingest    --fixture <dir> [--db <path>] [--rules <dir>]");
    Console.WriteLine("  timeline  [--db <path>]");
    Console.WriteLine("  replay    [--db <path>] [--rules <dir>]");
    Console.WriteLine("  gaps      [--db <path>]");
    Console.WriteLine("  status    [--db <path>]");
    Console.WriteLine("  verify    [--db <path>] [--schema <dir>]");
}
