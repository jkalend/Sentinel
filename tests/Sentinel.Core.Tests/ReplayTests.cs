using Sentinel.Core.Rules;
using Sentinel.Core.Store;

namespace Sentinel.Core.Tests;

public class ReplayTests
{
    [Fact]
    public void Replay_matches_live_under_the_same_rules()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            var rules = TestSupport.Rules();
            var (sensorId, hostId) = store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            var service = new IngestionService(store, sensorId, hostId);
            service.Ingest(
                fixture.Records,
                rules,
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);

            var report = ReplayService.Replay(store, rules);

            Assert.True(report.Matches);
            Assert.Equal(3, report.LiveAlerts);
            Assert.Equal(3, report.ReplayAlerts);
            Assert.Empty(report.ReplayOnly);
            Assert.Empty(report.LiveOnly);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Replay_detects_a_changed_rule_set()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            var rules = TestSupport.Rules();
            var (sensorId, hostId) = store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            var service = new IngestionService(store, sensorId, hostId);
            service.Ingest(
                fixture.Records,
                rules,
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);

            var subset = new RuleSet(rules.Rules.Where(rule => rule.Id != "sentinel.process.temp-executable"));
            var report = ReplayService.Replay(store, subset);

            Assert.False(report.Matches);
            Assert.Single(report.LiveOnly);
            Assert.Equal("sentinel.process.temp-executable", report.LiveOnly[0].RuleId);
            Assert.Empty(report.ReplayOnly);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }
}
