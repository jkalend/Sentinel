using Sentinel.Core.Fixture;
using Sentinel.Core.Model;
using Sentinel.Core.Ocsf;
using Sentinel.Core.Rules;
using Sentinel.Core.Store;
using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Tests;

internal static class TestSupport
{
    public static string Root { get; } = FindRoot();

    public static string FixturePath => Path.Combine(Root, "fixtures", "sysmon-v01");
    public static string RulesPath => Path.Combine(Root, "rules");
    public static string SchemaPath => Path.Combine(Root, "schemas", "ocsf-1.9.0");

    public static FixtureSet Fixture() => FixtureSet.Load(FixturePath);

    public static RuleSet Rules() => RuleLoader.LoadDirectory(RulesPath);

    public static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"sentinel-test-{Guid.NewGuid():N}.db");

    public static LocalEvent ToLocal(FixtureSet fixture, SysmonRecord record, long sequence = 0)
    {
        var sensorId = fixture.SensorId ?? throw new InvalidOperationException("Fixture has no sensor id.");
        var hostId = fixture.HostId ?? throw new InvalidOperationException("Fixture has no host id.");
        var processed = DateTimeOffset.Parse("2026-11-02T11:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var outcome = fixture.GroundTruth.TryGetValue(record.EventRecordId, out var value)
            ? value
            : NetworkOutcome.Unknown;
        var mapped = OcsfMapper.Map(record, new MappingContext(sensorId, hostId, fixture.IncarnationId, processed), outcome);

        return new LocalEvent(
            SentinelVersions.ContractVersion,
            sensorId,
            hostId,
            new SourceIdentity(
                record.Channel,
                record.Provider,
                record.ProviderGuid,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                record.EventId,
                record.EventRecordId),
            sequence,
            mapped.Time,
            record.TimeCreated,
            processed,
            "wall_utc",
            mapped.Kind,
            mapped.OcsfJson,
            mapped.ClassUid,
            mapped.ActivityId,
            mapped.Unmapped,
            record.RawXml,
            IngestionService.Sha256Hex(record.RawXml),
            null,
            mapped.Process,
            mapped.Network,
            mapped.HostName,
            mapped.UserName);
    }

    public static LocalEvent LocalEvent(FixtureSet fixture, long eventRecordId, long sequence = 0) =>
        ToLocal(fixture, fixture.Records.Single(record => record.EventRecordId == eventRecordId), sequence);

    public static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-journal", "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sentinel.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the Sentinel repository root.");
    }
}
