using System.Text.Json;
using Sentinel.Core.Model;
using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Fixture;

public sealed class FixtureSet
{
    public required string Root { get; init; }
    public required string Channel { get; init; }
    public required string IncarnationId { get; init; }
    public required int ConfigGeneration { get; init; }
    public string? SensorId { get; init; }
    public string? HostId { get; init; }
    public required IReadOnlyList<SysmonRecord> Records { get; init; }
    public required IReadOnlyDictionary<long, NetworkOutcome> GroundTruth { get; init; }

    public static FixtureSet Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Fixture directory not found: {directory}");
        }

        var manifestPath = Path.Combine(directory, "fixture.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"Fixture manifest not found: {manifestPath}");
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;

        var channel = RequiredString(root, "channel");
        var incarnationId = RequiredString(root, "incarnation_id");
        var configGeneration = root.TryGetProperty("config_generation", out var generation)
            ? generation.GetInt32()
            : 1;

        var groundTruth = new Dictionary<long, NetworkOutcome>();
        if (root.TryGetProperty("network_ground_truth", out var truth))
        {
            foreach (var item in truth.EnumerateArray())
            {
                var recordId = item.GetProperty("event_record_id").GetInt64();
                var outcomeText = item.GetProperty("outcome").GetString()
                    ?? throw new InvalidDataException("network_ground_truth entry has no outcome.");
                groundTruth[recordId] = ParseOutcome(outcomeText);
            }
        }

        var eventDirectory = Path.Combine(directory, "events");
        if (!Directory.Exists(eventDirectory))
        {
            throw new InvalidDataException($"Fixture has no events directory: {eventDirectory}");
        }

        var records = Directory
            .EnumerateFiles(eventDirectory, "*.xml", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => SysmonXmlReader.Parse(File.ReadAllText(path)))
            .OrderBy(record => record.EventRecordId)
            .ToList();

        if (records.Count == 0)
        {
            throw new InvalidDataException($"Fixture has no XML records: {eventDirectory}");
        }

        foreach (var record in records)
        {
            if (!string.Equals(record.Channel, channel, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Record {record.EventRecordId} channel '{record.Channel}' does not match manifest '{channel}'.");
            }
        }

        return new FixtureSet
        {
            Root = Path.GetFullPath(directory),
            Channel = channel,
            IncarnationId = incarnationId,
            ConfigGeneration = configGeneration,
            SensorId = root.TryGetProperty("sensor_id", out var sensor) ? sensor.GetString() : null,
            HostId = root.TryGetProperty("host_id", out var host) ? host.GetString() : null,
            Records = records,
            GroundTruth = groundTruth
        };
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.GetString() is not { Length: > 0 } text)
        {
            throw new InvalidDataException($"Fixture manifest is missing '{name}'.");
        }

        return text;
    }

    private static NetworkOutcome ParseOutcome(string value) => value switch
    {
        "unknown" => NetworkOutcome.Unknown,
        "established_outbound" => NetworkOutcome.EstablishedOutbound,
        "inbound_listener" => NetworkOutcome.InboundListener,
        "refused" => NetworkOutcome.Refused,
        "no_event" => NetworkOutcome.NoEvent,
        _ => throw new InvalidDataException($"Unknown network ground-truth outcome '{value}'.")
    };
}
