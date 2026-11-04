using System.Text.Json;

namespace Sentinel.Core.Fixture;

public sealed record FleetHostFixture(
    string SensorId,
    string HostId,
    string? HostName,
    long ClockOffsetMs,
    long ClockUncertaintyMs,
    FixtureSet Fixture);

public sealed class FleetFixture
{
    public required string Format { get; init; }
    public required IReadOnlyList<FleetHostFixture> Hosts { get; init; }

    public static FleetFixture Load(string directory)
    {
        var root = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(root, "fleet.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"fleet manifest not found: {manifestPath}");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var rootElement = document.RootElement;
        var format = rootElement.GetProperty("format").GetString()
            ?? throw new InvalidDataException("fleet manifest has no format.");
        var hosts = new List<FleetHostFixture>();
        foreach (var host in rootElement.GetProperty("hosts").EnumerateArray())
        {
            var sensorId = host.GetProperty("sensor_id").GetString()
                ?? throw new InvalidDataException("fleet host has no sensor_id.");
            var hostId = host.GetProperty("host_id").GetString()
                ?? throw new InvalidDataException("fleet host has no host_id.");
            var fixtureDirectory = host.GetProperty("fixture").GetString()
                ?? throw new InvalidDataException("fleet host has no fixture.");
            hosts.Add(new FleetHostFixture(
                sensorId,
                hostId,
                host.TryGetProperty("host_name", out var hostName) ? hostName.GetString() : null,
                host.TryGetProperty("clock_offset_ms", out var offset) ? offset.GetInt64() : 0,
                host.TryGetProperty("clock_uncertainty_ms", out var uncertainty) ? uncertainty.GetInt64() : 0,
                FixtureSet.Load(Path.Combine(root, fixtureDirectory))));
        }

        return new FleetFixture { Format = format, Hosts = hosts };
    }
}
