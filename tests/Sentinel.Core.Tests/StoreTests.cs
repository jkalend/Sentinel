using Microsoft.Data.Sqlite;
using Sentinel.Core.Model;
using Sentinel.Core.Store;

namespace Sentinel.Core.Tests;

public class StoreTests
{
    [Fact]
    public void Append_persists_event_and_bookmark()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            var (sensorId, hostId) = store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            Assert.Equal(fixture.SensorId, sensorId);
            Assert.Equal(fixture.HostId, hostId);

            var value = TestSupport.ToLocal(fixture, fixture.Records.Single(r => r.EventRecordId == 1001));
            var bookmark = new BookmarkUpdate(
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                1001,
                1001);

            var result = store.Append(value, [], bookmark);

            Assert.Equal(AppendStatus.Accepted, result.Status);
            Assert.Equal(1, result.SensorSequence);
            var stored = Assert.Single(store.ReadEvents());
            Assert.Equal(1001, stored.Source.EventRecordId);
            Assert.Equal(value.RawXml, stored.RawXml);
            Assert.Equal(value.RawSha256, stored.RawSha256);
            Assert.Equal(1007, stored.OcsfClassUid);
            Assert.Equal("powershell.exe", stored.Process?.Name);
            Assert.Equal(
                "{a1b2c3d4-1111-6111-0000-000000000001}",
                stored.Process?.Guid);

            var saved = store.GetBookmark(fixture.Channel);
            Assert.NotNull(saved);
            Assert.Equal(1001, saved!.EventRecordId);
            Assert.Equal(1001, saved.CoverageStartRecordId);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Duplicate_source_key_is_idempotent()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            var value = TestSupport.ToLocal(fixture, fixture.Records.Single(r => r.EventRecordId == 1001));
            var bookmark = new BookmarkUpdate(fixture.Channel, fixture.IncarnationId, 1, 1001, 1001);

            Assert.Equal(AppendStatus.Accepted, store.Append(value, [], bookmark).Status);
            Assert.Equal(AppendStatus.Duplicate, store.Append(value, [], bookmark).Status);
            Assert.Single(store.ReadEvents());
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Storage_full_rolls_back_event_and_bookmark()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            var value = TestSupport.ToLocal(fixture, fixture.Records.Single(r => r.EventRecordId == 1001));
            var bookmark = new BookmarkUpdate(fixture.Channel, fixture.IncarnationId, 1, 1001, 1001);

            store.SetMaxPageCount(store.PageCount);
            var oversized = value with
            {
                RawXml = new string('x', 1_000_000),
                RawSha256 = IngestionService.Sha256Hex(new string('x', 1_000_000))
            };

            var error = Assert.Throws<SqliteException>(() => store.Append(oversized, [], bookmark));
            Assert.Equal(13, error.SqliteErrorCode);
            Assert.Empty(store.ReadEvents());
            Assert.Null(store.GetBookmark(fixture.Channel));
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Gap_round_trips()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            var gap = IngestionService.CreateGap(
                fixture.Channel,
                fixture.IncarnationId,
                GapKind.MissingRecordsReported,
                CauseConfidence.Known,
                1002,
                1002,
                "test gap");

            store.InsertGap(gap);
            var stored = Assert.Single(store.ReadGaps());
            Assert.Equal(gap.GapId, stored.GapId);
            Assert.Equal(GapKind.MissingRecordsReported, stored.Kind);
            Assert.Equal(1002, stored.StartRecordId);
            Assert.Equal(1002, stored.EndRecordId);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }
}
