using System.Net;
using System.Net.Sockets;
using Sentinel.Core.Fleet;
using Sentinel.Core.Model;
using Sentinel.Core.Store;
using Sentinel.Core.Transport;

namespace Sentinel.Core.Tests;

public class TransportTests
{
    [Fact]
    public async Task Push_delivers_a_batch_and_acknowledges_after_commit()
    {
        var source = TestSupport.NewDatabasePath();
        var root = NewDirectory();
        using var sourceStore = CreateStore(source, "sensor-a", "host-a");
        sourceStore.Enroll("sensor-a", "host-a", "alpha", clockOffsetMs: 1200, clockUncertaintyMs: 300);
        var port = FreePort();
        using var collector = new TransportCollector($"http://127.0.0.1:{port}/", root, TestSupport.Rules());
        collector.Start();
        try
        {
            var sender = new TransportSender(source, $"http://127.0.0.1:{port}/", batchSize: 4);
            var result = await sender.PushAllAsync();
            Assert.Equal(3, result.Batches);
            Assert.Equal(10, result.Accepted);
            Assert.Equal(10, result.LastAckedSequence);
            Assert.Single(Directory.GetFiles(root, "*.db"));
            using var target = new SqliteEventStore(Directory.GetFiles(root, "*.db").Single(), readOnly: true);
            Assert.Equal(10, target.ReadEvents().Count);
            Assert.Equal(3, target.ReadAlerts().Count);
            Assert.Equal("sensor-a", target.GetEnrollment()!.SensorId);
            Assert.Equal(1200, target.GetEnrollment()!.ClockOffsetMs);
            Assert.Equal(300, target.GetEnrollment()!.ClockUncertaintyMs);

            var again = await sender.PushAllAsync();
            Assert.Equal(0, again.Batches);
        }
        finally
        {
            await collector.StopAsync();
            sourceStore.Dispose();
            TestSupport.DeleteDatabase(source);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Duplicate_batch_is_idempotent_after_lost_acknowledgement()
    {
        var source = TestSupport.NewDatabasePath();
        var root = NewDirectory();
        using var sourceStore = CreateStore(source, "sensor-a", "host-a");
        var port = FreePort();
        using var collector = new TransportCollector($"http://127.0.0.1:{port}/", root, TestSupport.Rules());
        collector.Start();
        try
        {
            var url = $"http://127.0.0.1:{port}/";
            var sender = new TransportSender(source, url);
            await sender.PushAllAsync();
            var targetPath = Directory.GetFiles(root, "*.db").Single();

            sender.ResetAck();
            var retry = await sender.PushAllAsync();
            Assert.Equal(10, retry.Duplicates);
            using var target = new SqliteEventStore(targetPath, readOnly: true);
            Assert.Equal(10, target.ReadEvents().Count);
        }
        finally
        {
            await collector.StopAsync();
            sourceStore.Dispose();
            TestSupport.DeleteDatabase(source);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Two_sensors_deliver_to_separate_collector_stores_and_fleet_sees_both()
    {
        var first = TestSupport.NewDatabasePath();
        var second = TestSupport.NewDatabasePath();
        var root = NewDirectory();
        using var firstStore = CreateStore(first, "sensor-a", "host-a");
        using var secondStore = CreateStore(second, "sensor-b", "host-b");
        var port = FreePort();
        using var collector = new TransportCollector($"http://127.0.0.1:{port}/", root, TestSupport.Rules());
        collector.Start();
        try
        {
            await new TransportSender(first, $"http://127.0.0.1:{port}/").PushAllAsync();
            await new TransportSender(second, $"http://127.0.0.1:{port}/").PushAllAsync();
            var paths = Directory.GetFiles(root, "*.db").OrderBy(value => value).ToArray();
            Assert.Equal(2, paths.Length);
            var report = FleetService.Inspect(paths);
            Assert.Equal(2, report.Hosts.Count);
            Assert.Contains(report.Hosts, host => host.SensorId == "sensor-a" && host.EventCount == 10);
            Assert.Contains(report.Hosts, host => host.SensorId == "sensor-b" && host.EventCount == 10);
        }
        finally
        {
            await collector.StopAsync();
            firstStore.Dispose();
            secondStore.Dispose();
            TestSupport.DeleteDatabase(first);
            TestSupport.DeleteDatabase(second);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Offline_collector_fails_without_advancing_state()
    {
        var source = TestSupport.NewDatabasePath();
        using var sourceStore = CreateStore(source, "sensor-a", "host-a");
        var port = FreePort();
        var sender = new TransportSender(source, $"http://127.0.0.1:{port}/", retries: 0);
        try
        {
            var error = await Assert.ThrowsAsync<TransportException>(() => sender.PushAsync());
            Assert.Contains("transport failed", error.Message, StringComparison.OrdinalIgnoreCase);
            using var store = new SqliteEventStore(source);
            store.Initialize();
            Assert.Equal(0, store.GetTransportAck(new Uri(new Uri($"http://127.0.0.1:{port}/"), "v1/batches").ToString()));
        }
        finally
        {
            sourceStore.Dispose();
            TestSupport.DeleteDatabase(source);
        }
    }

    [Fact]
    public async Task Collector_rejects_a_sensor_outside_the_enrollment_allowlist()
    {
        var source = TestSupport.NewDatabasePath();
        var root = NewDirectory();
        using var sourceStore = CreateStore(source, "sensor-a", "host-a");
        var port = FreePort();
        using var collector = new TransportCollector(
            $"http://127.0.0.1:{port}/", root, allowedSensors: ["sensor-other"]);
        collector.Start();
        try
        {
            var sender = new TransportSender(source, $"http://127.0.0.1:{port}/", retries: 0);
            await Assert.ThrowsAsync<TransportException>(() => sender.PushAsync());
            Assert.Empty(Directory.GetFiles(root, "*.db"));
        }
        finally
        {
            await collector.StopAsync();
            sourceStore.Dispose();
            TestSupport.DeleteDatabase(source);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Invalid_token_is_rejected()
    {
        var source = TestSupport.NewDatabasePath();
        var root = NewDirectory();
        using var sourceStore = CreateStore(source, "sensor-a", "host-a");
        var port = FreePort();
        using var collector = new TransportCollector($"http://127.0.0.1:{port}/", root, token: "correct");
        collector.Start();
        try
        {
            var sender = new TransportSender(source, $"http://127.0.0.1:{port}/", token: "wrong", retries: 0);
            await Assert.ThrowsAsync<TransportException>(() => sender.PushAsync());
            Assert.Empty(Directory.GetFiles(root, "*.db"));
        }
        finally
        {
            await collector.StopAsync();
            sourceStore.Dispose();
            TestSupport.DeleteDatabase(source);
            DeleteDirectory(root);
        }
    }

    private static SqliteEventStore CreateStore(string path, string sensorId, string hostId)
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

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), $"sentinel-transport-{Guid.NewGuid():N}");

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
