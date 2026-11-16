using Sentinel.Core.Config;
using Sentinel.Core.Rules;
using Sentinel.Core.Store;

namespace Sentinel.Core.Tests;

public class FleetConfigTests
{
    [Fact]
    public void Apply_records_generation_pins_pack_and_is_idempotent()
    {
        var database = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(database);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            var pack = RulePackBuilder.Build(TestSupport.RulesPath, "sentinel.base", "1");
            store.InstallRulePack(pack);

            var config = NewConfig(fixture.SensorId, fixture.HostId, pack.PackSha256);
            var first = FleetConfigService.Apply(store, config);
            var second = FleetConfigService.Apply(store, config);

            Assert.Equal(1, first.Generation);
            Assert.False(first.AlreadyActive);
            Assert.True(second.AlreadyActive);
            Assert.Equal(first.Generation, second.Generation);
            Assert.Equal(pack.PackSha256, store.GetActiveRulePack()!.PackSha256);
            Assert.Empty(FleetConfigService.Drift(store));
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
        }
    }

    [Fact]
    public void Rollback_restores_previous_document_and_pack()
    {
        var database = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(database);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            var first = RulePackBuilder.Build(TestSupport.RulesPath, "sentinel.base", "1");
            store.InstallRulePack(first);
            var second = RulePackBuilder.Build(
                TestSupport.RulesPath,
                "sentinel.base",
                "2",
                DateTimeOffset.Parse("2026-11-17T00:00:00Z"));
            store.InstallRulePack(second);

            var firstConfig = NewConfig(fixture.SensorId, fixture.HostId, first.PackSha256);
            var secondConfig = firstConfig with
            {
                ConfigId = "local-two",
                RulePackSha256 = second.PackSha256,
                CreatedAt = DateTimeOffset.Parse("2026-11-17T00:00:00Z")
            };
            FleetConfigService.Apply(store, firstConfig);
            FleetConfigService.Apply(store, secondConfig);
            Assert.Equal(second.PackSha256, store.GetActiveRulePack()!.PackSha256);

            var rolled = FleetConfigService.Rollback(store);

            Assert.Equal(3, rolled.Generation);
            Assert.Equal(firstConfig.ComputeSha256(), rolled.ConfigSha256);
            Assert.Equal(first.PackSha256, store.GetActiveRulePack()!.PackSha256);
            Assert.Equal(3, store.ReadConfigGenerations().Count);
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
        }
    }

    [Fact]
    public void Drift_reports_sensor_mismatch_and_missing_pack()
    {
        var database = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(database);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            store.EnsureIdentity(fixture.SensorId, fixture.HostId);

            var config = NewConfig("other-sensor", fixture.HostId, new string('a', 64));
            var sha = config.ComputeSha256();
            store.InsertConfig(sha, System.Text.Json.JsonSerializer.Serialize(config));

            var issues = FleetConfigService.Drift(store);

            Assert.Contains(issues, issue => issue.Contains("sensor_id drift", StringComparison.Ordinal));
            Assert.Contains(issues, issue => issue.Contains("not installed", StringComparison.Ordinal));
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
        }
    }

    [Fact]
    public void Invalid_collector_and_missing_pack_are_rejected()
    {
        var database = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(database);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            store.EnsureIdentity(fixture.SensorId, fixture.HostId);

            var badCollector = NewConfig(fixture.SensorId, fixture.HostId, null) with
            {
                AllowedCollectors = ["not-a-uri"]
            };
            Assert.Throws<ArgumentException>(() => FleetConfigService.Apply(store, badCollector));

            var missingPack = NewConfig(fixture.SensorId, fixture.HostId, new string('b', 64));
            Assert.Throws<ArgumentException>(() => FleetConfigService.Apply(store, missingPack));
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
        }
    }

    [Fact]
    public void Collector_allowlist_is_normalized()
    {
        var config = NewConfig(null, null, null) with
        {
            AllowedCollectors = ["http://127.0.0.1:18099"]
        };

        Assert.True(FleetConfigService.IsCollectorAllowed(config, "http://127.0.0.1:18099/"));
        Assert.False(FleetConfigService.IsCollectorAllowed(config, "http://127.0.0.1:18100/"));
    }

    private static FleetConfig NewConfig(string? sensorId, string? hostId, string? packSha) =>
        new(
            FleetConfig.CurrentFormat,
            "local-default",
            sensorId,
            hostId,
            packSha,
            new RetentionPolicy(30, 7, 90, 90, 90, 1000, 10L * 1024 * 1024 * 1024),
            ["http://127.0.0.1:18099/"],
            DateTimeOffset.Parse("2026-11-16T00:00:00Z"));
}
