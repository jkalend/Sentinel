using System.Globalization;
using System.Text;
using System.Text.Json;
using Sentinel.Core.Cli;
using Sentinel.Core.Config;
using Sentinel.Core.Rules;
using Sentinel.Core.Sources;
using Sentinel.Core.Store;
using Sentinel.Core.Sysmon;
using Sentinel.Windows;

var commandOptions = new Dictionary<string, IReadOnlyDictionary<string, bool>>(StringComparer.OrdinalIgnoreCase)
{
    ["run"] = Spec(
        ("db", true), ("rules", true), ("pack", true), ("max-records", true), ("channel", true),
        ("sensor-id", true), ("host-id", true)),
    ["capture"] = Spec(
        ("output", true), ("max-records", true), ("sensor-id", true), ("host-id", true), ("channel", true)),
    ["channel"] = Spec(("channel", true))
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

    var channel = Get(options, "channel") ?? SysmonXmlReader.SysmonChannel;

    switch (command)
    {
        case "run":
            Run(options, channel);
            break;
        case "capture":
            Capture(options, channel);
            break;
        case "channel":
            Channel(options, channel);
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
catch (OperationCanceledException)
{
    Console.WriteLine("cancelled");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

return exitCode;

static void Run(Dictionary<string, string?> options, string channel)
{
    var databasePath = Get(options, "db") ?? "data/sentinel.db";
    var rulesPath = Get(options, "rules") ?? "rules";
    var maxRecords = ParseInt(Get(options, "max-records"));

    using var store = new SqliteEventStore(databasePath);
    store.Initialize();
    if (Get(options, "sensor-id") is not null || Get(options, "host-id") is not null)
    {
        store.EnsureIdentity(Get(options, "sensor-id"), Get(options, "host-id"));
    }

    var activeConfig = store.GetActiveConfig();
    var config = activeConfig is null
        ? null
        : FleetConfigService.Parse(activeConfig.DocumentJson).Validate();
    var rules = LoadRules(options, store, config, rulesPath);
    using var source = new WindowsEventLogSource(channel);
    var runner = new AgentRunner(source, store, rules, channel);

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    var report = runner.Run(
        new AgentRunOptions(
            channel,
            InitialConfigGeneration: activeConfig?.Generation ?? 1,
            MaxRecords: maxRecords),
        cancellation.Token);

    Console.WriteLine($"database: {databasePath}");
    Console.WriteLine($"channel: {channel}");
    Console.WriteLine($"channel_max_bytes: {report.Configuration.MaximumSizeInBytes.ToString(CultureInfo.InvariantCulture)}");
    Console.WriteLine($"channel_log_mode: {report.Configuration.LogMode}");
    Console.WriteLine($"channel_enabled: {report.Configuration.IsEnabled.ToString().ToLowerInvariant()}");
    Console.WriteLine($"accepted: {report.Ingestion.Accepted}");
    Console.WriteLine($"duplicates: {report.Ingestion.Duplicates}");
    Console.WriteLine($"source_health: {report.Ingestion.SourceHealth}");
    Console.WriteLine($"alerts: {report.Ingestion.Alerts}");
    Console.WriteLine($"gaps: {report.Ingestion.Gaps}");
    Console.WriteLine($"bookmark_record_id: {report.Bookmark?.EventRecordId.ToString(CultureInfo.InvariantCulture) ?? "none"}");
    Console.WriteLine($"bookmark_generation: {report.Bookmark?.ConfigGeneration.ToString(CultureInfo.InvariantCulture) ?? "none"}");
}

static void Capture(Dictionary<string, string?> options, string channel)
{
    var output = Get(options, "output")
        ?? throw new ArgumentException("--output is required.");
    var maxRecords = ParseInt(Get(options, "max-records")) ?? 1000;
    var eventsDirectory = Path.Combine(output, "events");
    Directory.CreateDirectory(eventsDirectory);

    using var source = new WindowsEventLogSource(channel);
    var configuration = source.ReadConfiguration();
    var sensorId = Get(options, "sensor-id") ?? Guid.NewGuid().ToString("D");
    var hostId = Get(options, "host-id") ?? Guid.NewGuid().ToString("D");
    var incarnationId = Guid.NewGuid().ToString("D");

    var snapshotRecordId = source.GetLastRecordId();
    var count = 0;
    foreach (var delivery in source.Read(
                 new SourceStart(SourceStartMode.OldestRetained, null, null, snapshotRecordId),
                 maxRecords,
                 CancellationToken.None))
    {
        var record = delivery.Record;
        var path = Path.Combine(
            eventsDirectory,
            $"{record.EventRecordId.ToString(CultureInfo.InvariantCulture)}-event{record.EventId.ToString(CultureInfo.InvariantCulture)}.xml");
        File.WriteAllText(path, record.RawXml, new UTF8Encoding(false));
        count++;
    }

    var manifest = new
    {
        channel,
        incarnation_id = incarnationId,
        config_generation = 1,
        sensor_id = sensorId,
        host_id = hostId,
        network_ground_truth = Array.Empty<object>(),
        captured_channel = new
        {
            maximum_size_bytes = configuration.MaximumSizeInBytes,
            log_mode = configuration.LogMode,
            is_enabled = configuration.IsEnabled,
            log_file_path = configuration.LogFilePath
        }
    };

    File.WriteAllText(
        Path.Combine(output, "fixture.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
        new UTF8Encoding(false));

    Console.WriteLine($"captured: {count}");
    Console.WriteLine($"output: {Path.GetFullPath(output)}");
    Console.WriteLine("network_ground_truth is empty; add only the outcomes independently observed for Event ID 3 probes.");
}

static void Channel(Dictionary<string, string?> options, string channel)
{
    using var source = new WindowsEventLogSource(channel);
    var configuration = source.ReadConfiguration();
    Console.WriteLine($"channel: {configuration.SourceId}");
    Console.WriteLine($"maximum_size_bytes: {configuration.MaximumSizeInBytes.ToString(CultureInfo.InvariantCulture)}");
    Console.WriteLine($"log_mode: {configuration.LogMode}");
    Console.WriteLine($"is_enabled: {configuration.IsEnabled.ToString().ToLowerInvariant()}");
    Console.WriteLine($"log_file_path: {configuration.LogFilePath ?? "unknown"}");
}

static RuleSet LoadRules(
    Dictionary<string, string?> options,
    SqliteEventStore store,
    FleetConfig? config,
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

    if (config?.RulePackSha256 is { } pinnedSha)
    {
        var pinned = store.GetRulePack(pinnedSha)
            ?? throw new InvalidOperationException($"active config pins missing rule pack '{pinnedSha}'.");
        return pinned.ToRuleSet();
    }

    var active = store.GetActiveRulePack();
    return active is not null ? active.ToRuleSet() : RuleLoader.LoadDirectory(defaultDirectory);
}

static int? ParseInt(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
    {
        throw new ArgumentException($"'{value}' is not a positive integer.");
    }

    return parsed;
}

static Dictionary<string, string?> ParseOptions(string[] args) => CliOptions.Parse(args);

static Dictionary<string, bool> Spec(params (string Key, bool TakesValue)[] entries) =>
    entries.ToDictionary(entry => entry.Key, entry => entry.TakesValue, StringComparer.OrdinalIgnoreCase);

static string? Get(Dictionary<string, string?> options, string key) => CliOptions.Get(options, key);

static void Usage()
{
    Console.WriteLine("sentinel-agent <command> [options]");
    Console.WriteLine();
    Console.WriteLine("  run      --db <path> [--rules <dir>|--pack <ref>] [--channel <name>] [--max-records <n>]");
    Console.WriteLine("           [--sensor-id <uuid>] [--host-id <uuid>]");
    Console.WriteLine("  capture  --output <dir> [--channel <name>] [--max-records <n>] [--sensor-id <uuid>] [--host-id <uuid>]");
    Console.WriteLine("  channel  [--channel <name>]");
}
