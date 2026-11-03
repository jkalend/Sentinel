using Sentinel.Core.Store;
using Sentinel.Core.Timeline;

namespace Sentinel.Core.Tests;

public class TimelineTests
{
    [Fact]
    public void Timeline_joins_by_process_guid_and_shows_ancestry()
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

            var lines = TimelineBuilder
                .Render(store.ReadEvents())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var egress = lines.Single(line => line.Contains("127.0.0.1:51515 -> 127.0.0.1:8443"));
            Assert.Contains("powershell.exe", egress);
            Assert.DoesNotContain("cmd.exe", egress);

            var unobserved = lines.Single(line => line.Contains("203.0.113.10:443"));
            Assert.Contains("[process event not observed]", unobserved);

            var temp = lines.Single(line => line.Contains("dropper.exe"));
            Assert.Contains("notepad.exe", temp);
            Assert.Contains("[parent not observed:", temp);

            var pidReuse = lines.Single(line => line.Contains("cmd.exe pid=4242"));
            Assert.Contains("{d4e5f6a7-4444-6444-0000-000000000008}", pidReuse);

            Assert.Contains(lines, line => line.Contains("source-health: EventID 4"));
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }
}
