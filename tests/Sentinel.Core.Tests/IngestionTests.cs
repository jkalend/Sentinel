using Sentinel.Core.Model;
using Sentinel.Core.Store;

namespace Sentinel.Core.Tests;

public class IngestionTests
{
    [Fact]
    public void Full_fixture_produces_expected_counts_and_bookmark()
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

            var report = service.Ingest(
                fixture.Records,
                rules,
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);

            Assert.Equal(10, report.Accepted);
            Assert.Equal(0, report.Duplicates);
            Assert.Equal(1, report.SourceHealth);
            Assert.Equal(3, report.Alerts);
            Assert.Equal(0, report.Gaps);
            Assert.Equal(10, store.ReadEvents().Count);
            Assert.Equal(3, store.ReadAlerts().Count);

            var bookmark = store.GetBookmark(fixture.Channel);
            Assert.NotNull(bookmark);
            Assert.Equal(1010, bookmark!.EventRecordId);
            Assert.Equal(1001, bookmark.CoverageStartRecordId);

            var second = service.Ingest(
                fixture.Records,
                rules,
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);
            Assert.Equal(0, second.Accepted);
            Assert.Equal(10, second.Duplicates);
            Assert.Equal(3, store.ReadAlerts().Count);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Missing_record_id_creates_a_gap_and_links_it()
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

            var records = fixture.Records
                .Where(record => record.EventRecordId is 1001 or 1003)
                .ToList();
            var report = service.Ingest(
                records,
                rules,
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);

            Assert.Equal(2, report.Accepted);
            Assert.Equal(1, report.Gaps);
            var gap = Assert.Single(store.ReadGaps());
            Assert.Equal(GapKind.MissingRecordsReported, gap.Kind);
            Assert.Equal(CauseConfidence.Known, gap.Confidence);
            Assert.Equal(1002, gap.StartRecordId);
            Assert.Equal(1002, gap.EndRecordId);

            var linked = store.ReadEvents().Single(eventValue => eventValue.Source.EventRecordId == 1003);
            Assert.Equal(gap.GapId, linked.GapId);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Out_of_order_delivery_keeps_max_bookmark_and_expands_coverage()
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

            var records = fixture.Records
                .Where(record => record.EventRecordId is 1002 or 1003)
                .OrderByDescending(record => record.EventRecordId)
                .ToList();
            var report = service.Ingest(
                records,
                rules,
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);

            Assert.Equal(2, report.Accepted);
            Assert.Equal(0, report.Gaps);
            var bookmark = store.GetBookmark(fixture.Channel);
            Assert.Equal(1003, bookmark!.EventRecordId);
            Assert.Equal(1002, bookmark.CoverageStartRecordId);

            var sequences = store.ReadEvents().Select(value => value.Source.EventRecordId).ToList();
            Assert.Equal([1003, 1002], sequences);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }
}
