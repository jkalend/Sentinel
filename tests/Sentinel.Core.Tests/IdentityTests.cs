using Sentinel.Core.Store;
using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Tests;

public class IdentityTests
{
    private const string Channel = SysmonXmlReader.SysmonChannel;

    [Fact]
    public void Ingestion_records_an_incarnation_and_rotation_moves_the_bookmark()
    {
        var database = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(database);
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

            var old = store.GetActiveIncarnation();
            Assert.NotNull(old);
            Assert.Equal(fixture.IncarnationId, old!.IncarnationId);
            Assert.Equal(fixture.IncarnationId, store.GetBookmark(Channel)!.IncarnationId);

            var rotated = store.RotateIncarnation(Channel, "test rotation");

            Assert.NotEqual(old.IncarnationId, rotated.IncarnationId);
            Assert.Equal(rotated.IncarnationId, store.GetBookmark(Channel)!.IncarnationId);
            Assert.Equal(rotated.IncarnationId, store.GetActiveIncarnation()!.IncarnationId);
            var ended = store.ReadIncarnations().Single(item => item.IncarnationId == old.IncarnationId);
            Assert.NotNull(ended.EndedAt);
            Assert.Equal("test rotation", ended.Reason);
            Assert.Equal(10, store.GetCounts().Events);
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
        }
    }

    [Fact]
    public void Revocation_removes_active_incarnation_without_deleting_evidence()
    {
        var database = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(database);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            var active = store.RotateIncarnation(Channel, "initial");
            var (sensorId, hostId) = store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            new IngestionService(store, sensorId, hostId).Ingest(
                fixture.Records,
                TestSupport.Rules(),
                fixture.Channel,
                active.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);
            var events = store.GetCounts().Events;

            Assert.True(store.RevokeIncarnation(active.IncarnationId, "lost host"));
            Assert.True(store.IsIncarnationRevoked(active.IncarnationId));
            Assert.Null(store.GetActiveIncarnation());
            Assert.Equal(events, store.GetCounts().Events);
            Assert.NotNull(store.GetLatestIncarnation()!.RevokedAt);
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
        }
    }
}
