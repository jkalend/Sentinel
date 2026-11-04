using Sentinel.Core.Fleet;
using Sentinel.Core.Model;
using Sentinel.Core.Store;

namespace Sentinel.Core.Tests;

public class FleetTests
{
    [Fact]
    public void Fleet_reports_independent_hosts_clock_uncertainty_and_gaps()
    {
        var aPath = TestSupport.NewDatabasePath();
        var bPath = TestSupport.NewDatabasePath();
        try
        {
            using var a = Ingest(aPath, "sensor-a", "host-a");
            a.Enroll("sensor-a", "host-a", "alpha");
            using var b = Ingest(bPath, "sensor-b", "host-b");
            b.Enroll("sensor-b", "host-b", "beta", clockOffsetMs: 1500, clockUncertaintyMs: 250);
            b.InsertGap(new CoverageGap(
                Guid.NewGuid().ToString("D"),
                "Microsoft-Windows-Sysmon/Operational",
                "c1d2e3f4-5a6b-4c7d-8e9f-333333333333",
                GapKind.MissingRecordsReported,
                CauseConfidence.Known,
                1005,
                1005,
                DateTimeOffset.Parse("2026-11-02T10:00:05Z"),
                "synthetic fleet gap"));

            var report = FleetService.Inspect([aPath, bPath]);
            Assert.Equal(2, report.Hosts.Count);
            var alpha = report.Hosts.Single(host => host.SensorId == "sensor-a");
            var beta = report.Hosts.Single(host => host.SensorId == "sensor-b");
            Assert.Equal("alpha", alpha.HostName);
            Assert.Equal("complete", alpha.CoverageStatus);
            Assert.Equal("ordered", alpha.OrderStatus);
            Assert.Equal("nominal", alpha.ClockStatus);
            Assert.False(alpha.MissingTelemetry);
            Assert.Equal("partial", beta.CoverageStatus);
            Assert.Equal("uncertain", beta.OrderStatus);
            Assert.Equal("uncertain", beta.ClockStatus);
            Assert.True(beta.MissingTelemetry);
            Assert.False(beta.NoActivityClaim);
            Assert.Equal(1, beta.GapCount);
            var table = FleetService.RenderTable(report);
            Assert.Contains("partial", table);
            Assert.Contains("uncertain", table);
            Assert.Contains("No-activity claims: false", table);
        }
        finally
        {
            TestSupport.DeleteDatabase(aPath);
            TestSupport.DeleteDatabase(bPath);
        }
    }

    [Fact]
    public void Fleet_marks_out_of_order_source_time_as_uncertain()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            var (sensorId, hostId) = store.EnsureIdentity("sensor-c", "host-c");
            store.Enroll(sensorId, hostId, "gamma");
            var first = TestSupport.LocalEvent(fixture, 1001) with
            {
                SensorId = sensorId,
                HostId = hostId,
                Time = DateTimeOffset.Parse("2026-11-02T10:00:10Z")
            };
            var second = TestSupport.LocalEvent(fixture, 1002) with
            {
                SensorId = sensorId,
                HostId = hostId,
                Time = DateTimeOffset.Parse("2026-11-02T10:00:00Z")
            };
            store.Append(first, [], new BookmarkUpdate(
                fixture.Channel, fixture.IncarnationId, fixture.ConfigGeneration, 1001, 1001));
            store.Append(second, [], new BookmarkUpdate(
                fixture.Channel, fixture.IncarnationId, fixture.ConfigGeneration, 1002, 1001));

            var host = Assert.Single(FleetService.Inspect([path]).Hosts);
            Assert.Equal("uncertain", host.OrderStatus);
            Assert.False(host.NoActivityClaim);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    private static SqliteEventStore Ingest(string path, string sensorId, string hostId)
    {
        var store = new SqliteEventStore(path);
        store.Initialize();
        store.EnsureIdentity(sensorId, hostId);
        var fixture = TestSupport.Fixture();
        new IngestionService(store, sensorId, hostId).Ingest(
            fixture.Records,
            TestSupport.Rules(),
            fixture.Channel,
            fixture.IncarnationId,
            fixture.ConfigGeneration,
            fixture.GroundTruth);
        return store;
    }
}

public class FleetFixtureTests
{
    [Fact]
    public void Fleet_fixture_loads_independent_hosts_and_clock_metadata()
    {
        var fleet = Sentinel.Core.Fixture.FleetFixture.Load(TestSupport.FleetFixturePath);
        Assert.Equal(2, fleet.Hosts.Count);
        Assert.Equal("sensor-a", fleet.Hosts[0].SensorId);
        Assert.Equal("host-b", fleet.Hosts[1].HostId);
        Assert.Equal(1500, fleet.Hosts[1].ClockOffsetMs);
        Assert.Equal(250, fleet.Hosts[1].ClockUncertaintyMs);
        Assert.Equal(10, fleet.Hosts[0].Fixture.Records.Count);
        Assert.Equal(10, fleet.Hosts[1].Fixture.Records.Count);
    }
}
