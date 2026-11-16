using Sentinel.Core.Model;
using Sentinel.Core.Store;
using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Tests;

public class RetentionTests
{
    [Fact]
    public void Purge_deletes_sealed_data_keeps_newest_and_bookmark()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            var (sensorId, hostId) = store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            var service = new IngestionService(store, sensorId, hostId);
            service.Ingest(
                fixture.Records,
                TestSupport.Rules(),
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);
            var bookmarkBefore = store.GetBookmark(fixture.Channel);

            var policy = new RetentionPolicy(
                EventDays: 30,
                RawDays: 7,
                AlertDays: 365,
                SourceHealthDays: 365,
                GapDays: 365,
                KeepEvents: 2);
            var report = store.Purge(policy, DateTimeOffset.Parse("2026-12-31T00:00:00Z"), apply: true);

            Assert.True(report.Applied);
            Assert.Equal(10, report.Before.Events);
            Assert.Equal(2, report.After.Events);
            Assert.Equal(8, report.DataEventsDeleted);
            Assert.Equal(0, report.SourceHealthDeleted);
            Assert.Equal(2, report.RawXmlPurged);
            Assert.Equal(0, report.AlertsDeleted);
            Assert.Equal(1L, report.Range?.FirstSequence);
            Assert.Equal(8L, report.Range?.LastSequence);

            var events = store.ReadEvents();
            Assert.Equal(new long[] { 9, 10 }, events.Select(value => value.SensorSequence).ToArray());
            Assert.All(events, value => Assert.NotNull(value.RawPurgedAt));
            Assert.All(events, value => Assert.Empty(value.RawXml));
            Assert.Equal(bookmarkBefore, store.GetBookmark(fixture.Channel));
            Assert.Equal(3, store.ReadAlerts().Count);

            var audit = Assert.Single(store.ReadRetentionAudits());
            Assert.Equal(policy.Sha256(), audit.PolicySha256);
            Assert.Equal(8, audit.DataEventsDeleted);
            Assert.Equal(2, audit.RawXmlPurged);
            Assert.Equal(8, audit.Range?.EventCount);
            Assert.Equal(1, Assert.Single(store.ReadPurgedRanges()).FirstSequence);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Purge_dry_run_changes_nothing()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            var (sensorId, hostId) = store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            new IngestionService(store, sensorId, hostId).Ingest(
                fixture.Records,
                TestSupport.Rules(),
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);

            var policy = new RetentionPolicy(KeepEvents: 2, RawDays: 365);
            var report = store.Purge(policy, DateTimeOffset.Parse("2026-12-31T00:00:00Z"), apply: false);

            Assert.False(report.Applied);
            Assert.Equal(8, report.DataEventsDeleted);
            Assert.Equal(10, store.ReadEvents().Count);
            Assert.Empty(store.ReadRetentionAudits());
            Assert.Empty(store.ReadPurgedRanges());
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Purge_honors_the_byte_cap_while_preserving_the_newest_event()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            var (sensorId, hostId) = store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            new IngestionService(store, sensorId, hostId).Ingest(
                fixture.Records,
                TestSupport.Rules(),
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);

            var report = store.Purge(
                new RetentionPolicy(
                    EventDays: 365,
                    RawDays: 365,
                    AlertDays: 365,
                    SourceHealthDays: 365,
                    GapDays: 365,
                    KeepEvents: 1,
                    MaxBytes: 12_000),
                DateTimeOffset.Parse("2026-12-31T00:00:00Z"),
                apply: true);

            Assert.True(report.DataEventsDeleted > 0);
            Assert.True(report.RetainedBytes <= 12_000);
            Assert.False(report.OverLimit);
            Assert.Contains(store.ReadEvents(), value => value.Source.EventRecordId == 1010);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Purge_preserves_event_exactly_at_the_cutoff()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            var cutoff = DateTimeOffset.Parse("2026-12-01T00:00:00Z");
            var records = fixture.Records
                .Where(record => record.EventRecordId is 1001 or 1002 or 1003)
                .ToArray();
            var times = new[]
            {
                cutoff.AddSeconds(-1),
                cutoff,
                cutoff.AddSeconds(1)
            };
            for (var index = 0; index < records.Length; index++)
            {
                var value = TestSupport.LocalEvent(fixture, records[index].EventRecordId) with { Time = times[index] };
                var bookmark = new BookmarkUpdate(
                    fixture.Channel,
                    fixture.IncarnationId,
                    fixture.ConfigGeneration,
                    records[index].EventRecordId,
                    records[0].EventRecordId);
                store.Append(value, [], bookmark);
            }

            var report = store.Purge(
                new RetentionPolicy(EventDays: 0, RawDays: 365, KeepEvents: 1),
                cutoff,
                apply: true);

            Assert.Equal(1, report.DataEventsDeleted);
            Assert.Equal(new long[] { 2, 3 }, store.ReadEvents().Select(value => value.SensorSequence).ToArray());
            Assert.Equal(1002, store.ReadEvents()[0].Source.EventRecordId);
            Assert.NotNull(store.GetBookmark(SysmonXmlReader.SysmonChannel));
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Compact_after_purge_preserves_counts_and_does_not_grow_the_file()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            var (sensorId, hostId) = store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            new IngestionService(store, sensorId, hostId).Ingest(
                fixture.Records,
                TestSupport.Rules(),
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);
            store.Purge(
                new RetentionPolicy(EventDays: 0, RawDays: 365, KeepEvents: 1),
                DateTimeOffset.Parse("2026-12-31T00:00:00Z"),
                apply: true);
            var before = new FileInfo(path).Length;
            var countsBefore = store.GetCounts();

            store.Compact();

            Assert.Equal(countsBefore, store.GetCounts());
            Assert.True(new FileInfo(path).Length <= before);
            Assert.NotNull(store.GetBookmark(SysmonXmlReader.SysmonChannel));
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }
}
