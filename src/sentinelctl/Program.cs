using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sentinel.Core.Cli;
using Sentinel.Core.Config;
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

var commandOptions = new Dictionary<string, IReadOnlyDictionary<string, bool>>(StringComparer.OrdinalIgnoreCase)
{
    ["init"] = Spec(("db", true), ("sensor-id", true), ("host-id", true)),
    ["sigma-import"] = Spec(("input", true), ("output", true), ("force", false)),
    ["rule-pack-build"] = Spec(("rules", true), ("id", true), ("version", true), ("output", true)),
    ["rule-pack-install"] = Spec(("pack", true), ("db", true)),
    ["rule-pack-activate"] = Spec(("pack", true), ("db", true)),
    ["rule-pack-rollback"] = Spec(("db", true)),
    ["rule-pack-list"] = Spec(("db", true), ("format", true)),
    ["rule-pack-show"] = Spec(("pack", true), ("db", true), ("format", true)),
    ["config-apply"] = Spec(("db", true), ("dbs", true), ("file", true)),
    ["config-show"] = Spec(("db", true), ("dbs", true), ("format", true)),
    ["config-history"] = Spec(("db", true), ("limit", true), ("format", true)),
    ["config-rollback"] = Spec(("db", true), ("dbs", true)),
    ["config-drift"] = Spec(("db", true), ("dbs", true)),
    ["identity-rotate"] = Spec(("db", true), ("source", true), ("reason", true)),
    ["identity-revoke"] = Spec(("db", true), ("incarnation", true), ("reason", true)),
    ["identity-list"] = Spec(("db", true), ("limit", true), ("format", true)),
    ["enroll"] = Spec(
        ("db", true), ("sensor-id", true), ("host-id", true), ("host-name", true),
        ("config-generation", true), ("clock-offset-ms", true), ("clock-uncertainty-ms", true)),
    ["fleet"] = Spec(("dbs", true), ("db", true), ("format", true)),
    ["collector"] = Spec(("listen", true), ("root", true), ("rules", true), ("token", true), ("allow-sensors", true)),
    ["push"] = Spec(
        ("db", true), ("collector", true), ("token", true), ("batch-size", true), ("retries", true),
        ("reset-ack", false)),
    ["ingest"] = Spec(
        ("fixture", true), ("db", true), ("rules", true), ("pack", true), ("sensor-id", true), ("host-id", true)),
    ["timeline"] = Spec(("db", true)),
    ["replay"] = Spec(("db", true), ("rules", true), ("pack", true)),
    ["gaps"] = Spec(("db", true)),
    ["status"] = Spec(("db", true)),
    ["purge"] = Spec(
        ("db", true), ("event-days", true), ("raw-days", true), ("alert-days", true), ("health-days", true),
        ("gap-days", true), ("keep-events", true), ("max-bytes", true), ("now", true),
        ("apply", false), ("compact", false), ("extended-retention", false)),
    ["purges"] = Spec(("db", true), ("limit", true)),
    ["run"] = Spec(
        ("db", true), ("output", true), ("rules", true), ("pack", true), ("schema", true),
        ("source-keys", true), ("redact", true), ("signing-key", true), ("custody-ref", true),
        ("include-payload", false)),
    ["verify-run"] = Spec(("manifest", true), ("db", true)),
    ["backup"] = Spec(("db", true), ("output", true), ("signing-key", true)),
    ["backup-keygen"] = Spec(("output", true)),
    ["restore"] = Spec(("backup", true), ("db", true)),
    ["verify"] = Spec(("db", true), ("schema", true))
};

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
    if (commandOptions.TryGetValue(command, out var allowedOptions))
    {
        CliOptions.Validate(command, options, allowedOptions);
    }

    switch (command)
    {
        case "init":
            Init(options);
            break;
        case "sigma-import":
            SigmaImport(options);
            break;
        case "rule-pack-build":
            RulePackBuild(options);
            break;
        case "rule-pack-install":
            RulePackInstall(options);
            break;
        case "rule-pack-activate":
            RulePackActivate(options);
            break;
        case "rule-pack-rollback":
            RulePackRollback(options);
            break;
        case "rule-pack-list":
            RulePackList(options);
            break;
        case "rule-pack-show":
            RulePackShow(options);
            break;
        case "config-apply":
            ConfigApply(options);
            break;
        case "config-show":
            ConfigShow(options);
            break;
        case "config-history":
            ConfigHistory(options);
            break;
        case "config-rollback":
            ConfigRollback(options);
            break;
        case "config-drift":
            exitCode = ConfigDrift(options);
            break;
        case "identity-rotate":
            IdentityRotate(options);
            break;
        case "identity-revoke":
            IdentityRevoke(options);
            break;
        case "identity-list":
            IdentityList(options);
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
        case "backup-keygen":
            BackupKeygen(options);
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

static void SigmaImport(Dictionary<string, string?> options)
{
    var input = Get(options, "input")
        ?? throw new ArgumentException("--input is required.");
    var output = Get(options, "output")
        ?? throw new ArgumentException("--output is required.");
    var results = SigmaImporter.ImportDirectory(input, output, options.ContainsKey("force"));
    foreach (var result in results)
    {
        Console.WriteLine($"{Path.GetFileName(result.Source)} -> {Path.GetFileName(result.Output)} ({result.RuleId})");
    }

    Console.WriteLine($"imported: {results.Count}");
}

static void RulePackBuild(Dictionary<string, string?> options)
{
    var rules = Get(options, "rules") ?? "rules";
    var packId = Get(options, "id")
        ?? throw new ArgumentException("--id is required.");
    var version = Get(options, "version")
        ?? throw new ArgumentException("--version is required.");
    var output = Get(options, "output")
        ?? throw new ArgumentException("--output is required.");

    var pack = RulePackBuilder.Build(rules, packId, version);
    RulePackBuilder.Write(pack, output);
    Console.WriteLine($"pack: {pack.Manifest.PackId}@{pack.Manifest.Version}");
    Console.WriteLine($"pack_sha256: {pack.PackSha256}");
    Console.WriteLine($"rules: {pack.Manifest.Rules.Count}");
    Console.WriteLine($"output: {Path.GetFullPath(output)}");
}

static void RulePackInstall(Dictionary<string, string?> options)
{
    var directory = Get(options, "pack")
        ?? throw new ArgumentException("--pack is required.");
    var pack = RulePackBuilder.LoadDirectory(directory);
    using var store = OpenStore(options);
    store.Initialize();
    store.InstallRulePack(pack, Path.GetFullPath(directory));
    Console.WriteLine($"installed: {pack.Manifest.PackId}@{pack.Manifest.Version}");
    Console.WriteLine($"pack_sha256: {pack.PackSha256}");
}

static void RulePackActivate(Dictionary<string, string?> options)
{
    var reference = Get(options, "pack")
        ?? throw new ArgumentException("--pack is required.");
    using var store = OpenStore(options);
    store.Initialize();
    var pack = store.ResolveRulePack(reference)
        ?? throw new ArgumentException($"rule pack '{reference}' is not installed.");
    var generation = store.ActivateRulePack(pack.PackSha256);
    Console.WriteLine($"active: {pack.Manifest.PackId}@{pack.Manifest.Version}");
    Console.WriteLine($"pack_sha256: {pack.PackSha256}");
    Console.WriteLine($"activation_generation: {generation}");
}

static void RulePackRollback(Dictionary<string, string?> options)
{
    using var store = OpenStore(options);
    store.Initialize();
    var pack = store.RollbackRulePack()
        ?? throw new InvalidOperationException("no previous rule pack activation exists.");
    var latest = store.ReadRulePackActivations(1);
    Console.WriteLine($"active: {pack.Manifest.PackId}@{pack.Manifest.Version}");
    Console.WriteLine($"pack_sha256: {pack.PackSha256}");
    if (latest.Count > 0)
    {
        Console.WriteLine($"activation_generation: {latest[0].Generation}");
    }
}

static void RulePackList(Dictionary<string, string?> options)
{
    using var store = OpenStore(options);
    store.Initialize();
    var packs = store.ListRulePacks();
    var format = (Get(options, "format") ?? "table").ToLowerInvariant();
    if (format == "json")
    {
        Console.Write(JsonSerializer.Serialize(packs, new JsonSerializerOptions { WriteIndented = true }));
        return;
    }

    if (format != "table")
    {
        throw new ArgumentException("--format must be table or json.");
    }

    Console.WriteLine("PACK                                    ID                       VERSION  ACTIVE  INSTALLED");
    foreach (var pack in packs)
    {
        Console.WriteLine(
            $"{pack.PackSha256[..12],-12} {pack.PackId,-24} {pack.Version,-7} "
            + $"{(pack.Active ? "yes" : "no"),-7} {pack.InstalledAt:O}");
    }
}

static void RulePackShow(Dictionary<string, string?> options)
{
    var reference = Get(options, "pack")
        ?? throw new ArgumentException("--pack is required.");
    using var store = OpenStore(options);
    store.Initialize();
    var pack = store.ResolveRulePack(reference)
        ?? throw new ArgumentException($"rule pack '{reference}' is not installed.");
    var format = (Get(options, "format") ?? "table").ToLowerInvariant();
    if (format == "json")
    {
        Console.Write(JsonSerializer.Serialize(pack.Manifest, new JsonSerializerOptions { WriteIndented = true }));
        return;
    }

    if (format != "table")
    {
        throw new ArgumentException("--format must be table or json.");
    }

    Console.WriteLine($"pack: {pack.Manifest.PackId}@{pack.Manifest.Version}");
    Console.WriteLine($"pack_sha256: {pack.PackSha256}");
    Console.WriteLine($"engine: {pack.Manifest.EngineVersion}");
    Console.WriteLine($"ocsf: {pack.Manifest.OcsfVersion}");
    Console.WriteLine($"created: {pack.Manifest.CreatedAt:O}");
    foreach (var rule in pack.Manifest.Rules.OrderBy(value => value.Id, StringComparer.Ordinal))
    {
        Console.WriteLine($"{rule.Id}@{rule.Version} {rule.File} {rule.Sha256}");
    }
}

static void ConfigApply(Dictionary<string, string?> options)
{
    var file = Get(options, "file")
        ?? throw new ArgumentException("--file is required.");
    var config = FleetConfigService.LoadFile(file);
    foreach (var path in StorePaths(options))
    {
        using var store = new SqliteEventStore(path);
        store.Initialize();
        var report = FleetConfigService.Apply(store, config);
        Console.WriteLine($"db: {Path.GetFullPath(path)}");
        Console.WriteLine($"generation: {report.Generation}");
        Console.WriteLine($"config_sha256: {report.ConfigSha256}");
        Console.WriteLine($"config_id: {report.Config.ConfigId}");
        Console.WriteLine($"tenant_id: {report.Config.TenantId ?? "none"}");
        Console.WriteLine($"already_active: {report.AlreadyActive.ToString().ToLowerInvariant()}");
        Console.WriteLine($"rule_pack_sha256: {report.Config.RulePackSha256 ?? "none"}");
    }
}

static void ConfigShow(Dictionary<string, string?> options)
{
    var format = (Get(options, "format") ?? "table").ToLowerInvariant();
    if (format is not ("table" or "json"))
    {
        throw new ArgumentException("--format must be table or json.");
    }

    var paths = StorePaths(options);
    if (paths.Count > 1)
    {
        if (format != "table")
        {
            throw new ArgumentException("--format json is available for a single --db.");
        }

        Console.WriteLine("DB                                      GEN  SHA256        TENANT        CONFIG_ID");
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"{ShortPath(path),-39} missing");
                continue;
            }

            try
            {
                using var store = new SqliteEventStore(path, readOnly: true);
                var active = store.GetActiveConfig();
                if (active is null)
                {
                    Console.WriteLine($"{ShortPath(path),-39} none");
                    continue;
                }

                var config = FleetConfigService.Parse(active.DocumentJson).Validate();
                Console.WriteLine(
                    $"{ShortPath(path),-39} {active.Generation,-4} {active.ConfigSha256[..12]} "
                    + $"{config.TenantId ?? "-",-12} {config.ConfigId}");
            }
            catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                Console.WriteLine($"{ShortPath(path),-39} error: {error.Message}");
            }
        }

        return;
    }

    if (!File.Exists(paths[0]))
    {
        throw new FileNotFoundException($"database not found: {paths[0]}");
    }

    using var single = new SqliteEventStore(paths[0], readOnly: true);
    var record = single.GetActiveConfig()
        ?? throw new InvalidOperationException("no active fleet configuration.");
    if (format == "json")
    {
        Console.Write(record.DocumentJson);
        return;
    }

    var current = FleetConfigService.Parse(record.DocumentJson).Validate();
    Console.WriteLine($"generation: {record.Generation}");
    Console.WriteLine($"config_sha256: {record.ConfigSha256}");
    Console.WriteLine($"config_id: {current.ConfigId}");
    Console.WriteLine($"tenant_id: {current.TenantId ?? "none"}");
    Console.WriteLine($"sensor_id: {current.SensorId ?? "any"}");
    Console.WriteLine($"host_id: {current.HostId ?? "any"}");
    Console.WriteLine($"rule_pack_sha256: {current.RulePackSha256 ?? "none"}");
    Console.WriteLine($"retention: {current.Retention.CanonicalJson()}");
    Console.WriteLine($"allowed_collectors: {(current.AllowedCollectors.Count == 0 ? "any" : string.Join(";", current.AllowedCollectors))}");
    Console.WriteLine($"applied_at: {record.AppliedAt:O}");
}

static void ConfigHistory(Dictionary<string, string?> options)
{
    using var store = OpenStore(options);
    store.Initialize();
    var records = store.ReadConfigGenerations(Integer(options, "limit", 50));
    var format = (Get(options, "format") ?? "table").ToLowerInvariant();
    if (format == "json")
    {
        Console.Write(JsonSerializer.Serialize(
            records.Select(value => new
            {
                generation = value.Generation,
                config_sha256 = value.ConfigSha256,
                applied_at = value.AppliedAt
            }),
            new JsonSerializerOptions { WriteIndented = true }));
        return;
    }

    if (format != "table")
    {
        throw new ArgumentException("--format must be table or json.");
    }

    Console.WriteLine("GENERATION  SHA256        APPLIED");
    foreach (var record in records)
    {
        Console.WriteLine($"{record.Generation,-11} {record.ConfigSha256[..12]}  {record.AppliedAt:O}");
    }
}

static void ConfigRollback(Dictionary<string, string?> options)
{
    foreach (var path in StorePaths(options))
    {
        using var store = new SqliteEventStore(path);
        store.Initialize();
        var report = FleetConfigService.Rollback(store);
        Console.WriteLine($"db: {Path.GetFullPath(path)}");
        Console.WriteLine($"generation: {report.Generation}");
        Console.WriteLine($"config_sha256: {report.ConfigSha256}");
        Console.WriteLine($"config_id: {report.Config.ConfigId}");
    }
}

static int ConfigDrift(Dictionary<string, string?> options)
{
    var total = 0;
    foreach (var path in StorePaths(options))
    {
        using var store = new SqliteEventStore(path);
        store.Initialize();
        var issues = FleetConfigService.Drift(store);
        total += issues.Count;
        Console.WriteLine($"db: {Path.GetFullPath(path)} drift: {issues.Count}");
        foreach (var issue in issues)
        {
            Console.WriteLine(issue);
        }
    }

    return total == 0 ? 0 : 1;
}

static IReadOnlyList<string> StorePaths(Dictionary<string, string?> options)
{
    var raw = (Get(options, "dbs") ?? Get(options, "db") ?? "data/sentinel.db")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (raw.Length == 0)
    {
        throw new ArgumentException("at least one database path is required in --dbs/--db.");
    }

    var comparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    return raw
        .Select(Path.GetFullPath)
        .Distinct(comparer)
        .ToList();
}

static string ShortPath(string path) =>
    Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) + "/" + Path.GetFileName(path);

static void IdentityRotate(Dictionary<string, string?> options)
{
    using var store = OpenStore(options);
    store.Initialize();
    var source = Get(options, "source") ?? SysmonXmlReader.SysmonChannel;
    var rotated = store.RotateIncarnation(source, Get(options, "reason"));
    Console.WriteLine($"incarnation: {rotated.IncarnationId}");
    Console.WriteLine($"sensor_id: {rotated.SensorId}");
    Console.WriteLine($"host_id: {rotated.HostId}");
    Console.WriteLine($"created_at: {rotated.CreatedAt:O}");
    var bookmark = store.GetBookmark(source);
    if (bookmark is not null)
    {
        Console.WriteLine($"bookmark_incarnation: {bookmark.IncarnationId}");
        Console.WriteLine($"bookmark_record_id: {bookmark.EventRecordId}");
    }
}

static void IdentityRevoke(Dictionary<string, string?> options)
{
    var incarnation = Get(options, "incarnation")
        ?? throw new ArgumentException("--incarnation is required.");
    using var store = OpenStore(options);
    store.Initialize();
    if (!store.RevokeIncarnation(incarnation, Get(options, "reason")))
    {
        throw new ArgumentException($"incarnation '{incarnation}' was not found or is already revoked.");
    }

    Console.WriteLine($"revoked: {incarnation}");
    Console.WriteLine($"currently_revoked: {store.IsIncarnationRevoked(incarnation).ToString().ToLowerInvariant()}");
}

static void IdentityList(Dictionary<string, string?> options)
{
    using var store = OpenStore(options);
    store.Initialize();
    var incarnations = store.ReadIncarnations(Integer(options, "limit", 50));
    var format = (Get(options, "format") ?? "table").ToLowerInvariant();
    if (format == "json")
    {
        Console.Write(JsonSerializer.Serialize(incarnations, new JsonSerializerOptions { WriteIndented = true }));
        return;
    }

    if (format != "table")
    {
        throw new ArgumentException("--format must be table or json.");
    }

    Console.WriteLine("INCARNATION                           SENSOR            CREATED                  ENDED                    REVOKED                  REASON");
    foreach (var item in incarnations)
    {
        Console.WriteLine(
            $"{item.IncarnationId,-36} {item.SensorId,-16} {item.CreatedAt:O}  "
            + $"{(item.EndedAt?.ToString("O") ?? "-"),-24} {(item.RevokedAt?.ToString("O") ?? "-"),-24} {item.Reason ?? ""}");
    }
}

static RetentionPolicy? ActiveRetentionPolicy(SqliteEventStore store)
{
    var active = store.GetActiveConfig();
    return active is null
        ? null
        : FleetConfigService.Parse(active.DocumentJson).Validate().Retention;
}

static bool HasRetentionOverride(Dictionary<string, string?> options) =>
    options.ContainsKey("event-days")
    || options.ContainsKey("raw-days")
    || options.ContainsKey("alert-days")
    || options.ContainsKey("health-days")
    || options.ContainsKey("gap-days")
    || options.ContainsKey("keep-events")
    || options.ContainsKey("max-bytes")
    || options.ContainsKey("extended-retention");

static RuleSet LoadRules(
    Dictionary<string, string?> options,
    SqliteEventStore store,
    string defaultDirectory)
{
    if (Get(options, "pack") is { } reference)
    {
        var pack = store.ResolveRulePack(reference)
            ?? throw new ArgumentException($"rule pack '{reference}' is not installed.");
        return pack.ToRuleSet();
    }

    if (Get(options, "rules") is { } directory)
    {
        return RuleLoader.LoadDirectory(directory);
    }

    var activeConfig = store.GetActiveConfig();
    if (activeConfig is not null)
    {
        var config = FleetConfigService.Parse(activeConfig.DocumentJson).Validate();
        if (config.RulePackSha256 is not null)
        {
            var pinned = store.GetRulePack(config.RulePackSha256)
                ?? throw new InvalidOperationException(
                    $"active config pins missing rule pack '{config.RulePackSha256}'.");
            return pinned.ToRuleSet();
        }
    }

    var activePack = store.GetActiveRulePack();
    return activePack is not null
        ? activePack.ToRuleSet()
        : RuleLoader.LoadDirectory(defaultDirectory);
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
    var database = Get(options, "db") ?? "data/sentinel.db";
    using (var store = new SqliteEventStore(database))
    {
        store.Initialize();
        var activeConfig = store.GetActiveConfig();
        if (activeConfig is not null)
        {
            var config = FleetConfigService.Parse(activeConfig.DocumentJson).Validate();
            if (!FleetConfigService.IsCollectorAllowed(config, collector))
            {
                throw new ArgumentException($"collector '{collector}' is not allowed by the active config.");
            }
        }
    }

    var sender = new TransportSender(
        database,
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

    var fixture = FixtureSet.Load(fixturePath);
    using var store = OpenStore(options);
    store.Initialize();
    var rules = LoadRules(options, store, "rules");
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
    using var store = OpenStore(options);
    store.Initialize();
    var rules = LoadRules(options, store, "rules");
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
    using var store = OpenStore(options);
    store.Initialize();

    var configPolicy = ActiveRetentionPolicy(store);
    var policy = HasRetentionOverride(options) || configPolicy is null
        ? new RetentionPolicy(
            Integer(options, "event-days", configPolicy?.EventDays ?? 30),
            Integer(options, "raw-days", configPolicy?.RawDays ?? 7),
            Integer(options, "alert-days", configPolicy?.AlertDays ?? 90),
            Integer(options, "health-days", configPolicy?.SourceHealthDays ?? 90),
            Integer(options, "gap-days", configPolicy?.GapDays ?? 90),
            Integer(options, "keep-events", configPolicy?.KeepEvents ?? 1000),
            Long(options, "max-bytes", configPolicy?.MaxBytes ?? 10L * 1024 * 1024 * 1024),
            options.ContainsKey("extended-retention") || (configPolicy?.ExtendedRetention ?? false))
        : configPolicy;
    var now = Get(options, "now") is { } text
        ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        : DateTimeOffset.UtcNow;
    var apply = options.ContainsKey("apply");
    var compact = options.ContainsKey("compact");

    var report = store.Purge(policy, now, apply);
    if (apply && compact)
    {
        store.Compact();
    }
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
    var signingKey = Get(options, "signing-key") is { } signingKeyPath
        ? File.ReadAllText(signingKeyPath)
        : null;
    var report = BackupService.Create(databasePath, output, null, signingKey);
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

static void BackupKeygen(Dictionary<string, string?> options)
{
    var output = Get(options, "output")
        ?? throw new ArgumentException("--output is required.");
    var publicKey = BackupService.GenerateSigningKey(output);
    Console.WriteLine($"private_key: {Path.GetFullPath(output)}");
    Console.WriteLine("public_key:");
    Console.WriteLine(publicKey);
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
    var schema = ResolveSchemaRoot(Get(options, "schema"));
    using var store = OpenStore(options);
    store.Initialize();
    var rules = LoadRules(options, store, "rules");
    var selected = Get(options, "source-keys")?
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var redacted = Get(options, "redact")?
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var includePayload = options.ContainsKey("include-payload")
        || selected is { Length: > 0 }
        || redacted is { Length: > 0 };
    var signingKey = Get(options, "signing-key") is { } signingKeyPath
        ? File.ReadAllText(signingKeyPath)
        : null;
    var custodyRefs = Get(options, "custody-ref")?
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var manifest = RunService.Create(
        store,
        rules,
        schema,
        output,
        includePayload,
        selected,
        redacted,
        signingKey,
        custodyRefs);
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

static Dictionary<string, string?> ParseOptions(string[] args) => CliOptions.Parse(args);

static Dictionary<string, bool> Spec(params (string Key, bool TakesValue)[] entries) =>
    entries.ToDictionary(entry => entry.Key, entry => entry.TakesValue, StringComparer.OrdinalIgnoreCase);

static string? Get(Dictionary<string, string?> options, string key) => CliOptions.Get(options, key);

static int Integer(Dictionary<string, string?> options, string key, int defaultValue) =>
    CliOptions.Integer(options, key, defaultValue);

static long Long(Dictionary<string, string?> options, string key, long defaultValue) =>
    CliOptions.Long(options, key, defaultValue);

static void Usage()
{
    Console.WriteLine("sentinelctl <command> [options]");
    Console.WriteLine();
    Console.WriteLine("  init      --db <path> [--sensor-id <uuid>] [--host-id <uuid>]");
    Console.WriteLine("  sigma-import       --input <dir> --output <dir> [--force]");
    Console.WriteLine("  rule-pack-build    --rules <dir> --id <pack-id> --version <v> --output <dir>");
    Console.WriteLine("  rule-pack-install  --pack <dir> --db <path>");
    Console.WriteLine("  rule-pack-activate --pack <sha|id@version> --db <path>");
    Console.WriteLine("  rule-pack-rollback --db <path>");
    Console.WriteLine("  rule-pack-list     --db <path> [--format table|json]");
    Console.WriteLine("  rule-pack-show     --pack <sha|id@version> --db <path> [--format table|json]");
    Console.WriteLine("  config-apply       --db <path> --file <config.json>");
    Console.WriteLine("  config-show        --db <path> [--format table|json]");
    Console.WriteLine("  config-history     --db <path> [--limit <n>] [--format table|json]");
    Console.WriteLine("  config-rollback    --db <path>");
    Console.WriteLine("  config-drift       --db <path>");
    Console.WriteLine("  identity-rotate    --db <path> [--source <channel>] [--reason <text>]");
    Console.WriteLine("  identity-revoke    --db <path> --incarnation <uuid> [--reason <text>]");
    Console.WriteLine("  identity-list      --db <path> [--limit <n>] [--format table|json]");
    Console.WriteLine("  enroll    --db <path> --sensor-id <id> --host-id <id> [--host-name <name>]");
    Console.WriteLine("            [--config-generation 1] [--clock-offset-ms 0] [--clock-uncertainty-ms 0]");
    Console.WriteLine("  fleet     --dbs <path;path> [--format table|json]");
    Console.WriteLine("  collector --listen <http://127.0.0.1:port/> --root <dir> [--rules <dir>]");
    Console.WriteLine("            [--token <token>] [--allow-sensors <id;id>]");
    Console.WriteLine("  push      --db <path> --collector <http://127.0.0.1:port/> [--token <token>] [--batch-size 100] [--retries 3] [--reset-ack]");
    Console.WriteLine("  ingest    --fixture <dir> [--db <path>] [--rules <dir>|--pack <ref>] [--sensor-id <id>] [--host-id <id>]");
    Console.WriteLine("  timeline  [--db <path>]");
    Console.WriteLine("  replay    [--db <path>] [--rules <dir>|--pack <ref>]");
    Console.WriteLine("  gaps      [--db <path>]");
    Console.WriteLine("  status    [--db <path>]");
    Console.WriteLine("  purge     [--db <path>] [--event-days 30] [--raw-days 7] [--alert-days 90]");
    Console.WriteLine("            [--health-days 90] [--gap-days 90] [--keep-events 1000] [--max-bytes 10737418240]");
    Console.WriteLine("            [--now <iso>] [--apply] [--compact] [--extended-retention]");
    Console.WriteLine("  purges    [--db <path>] [--limit <n>]");
    Console.WriteLine("  backup    --db <path> --output <path> [--signing-key <pem>]");
    Console.WriteLine("  backup-keygen --output <pem>");
    Console.WriteLine("  restore   --backup <path> [--db <path>]");
    Console.WriteLine("  run       --db <path> --output <dir> [--rules <dir>|--pack <ref>] [--schema <dir>]");
    Console.WriteLine("            [--include-payload] [--source-keys <key;key>]");
    Console.WriteLine("            [--redact raw_xml;process.cmd_line;process.path;user.name]");
    Console.WriteLine("            [--signing-key <pem>] [--custody-ref <uri;uri>]");
    Console.WriteLine("  verify-run --manifest <path> [--db <path>]");
    Console.WriteLine("  verify    [--db <path>] [--schema <dir>]");
}
