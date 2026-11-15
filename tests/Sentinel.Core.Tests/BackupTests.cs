using System.Security.Cryptography;
using Sentinel.Core.Store;

namespace Sentinel.Core.Tests;

public class BackupTests
{
    [Fact]
    public void Backup_and_restore_preserve_the_database()
    {
        var source = TestSupport.NewDatabasePath();
        var backup = TestSupport.NewDatabasePath();
        var restored = TestSupport.NewDatabasePath();
        try
        {
            using var sourceStore = new SqliteEventStore(source);
            sourceStore.Initialize();
            var fixture = TestSupport.Fixture();
            var (sensorId, hostId) = sourceStore.EnsureIdentity(fixture.SensorId, fixture.HostId);
            new IngestionService(sourceStore, sensorId, hostId).Ingest(
                fixture.Records,
                TestSupport.Rules(),
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);

            var report = BackupService.Create(source, backup);
            Assert.True(File.Exists(backup));
            Assert.True(File.Exists(backup + ".manifest.json"));
            Assert.Equal(10, report.Manifest.Counts.Events);
            Assert.Equal(3, report.Manifest.Counts.Alerts);
            Assert.Equal(1001, report.Manifest.BookmarkCoverageStart);
            Assert.Equal(Hash(backup), report.Manifest.DatabaseSha256);

            var restore = BackupService.Restore(backup, restored);
            Assert.Equal(report.Manifest.DatabaseSha256, restore.Manifest.DatabaseSha256);
            using var restoredStore = new SqliteEventStore(restored, readOnly: true);
            var (counts, bookmark) = restoredStore.GetStatus("Microsoft-Windows-Sysmon/Operational");
            Assert.Equal(10, counts.Events);
            Assert.Equal(3, counts.Alerts);
            Assert.Equal(1010, bookmark!.EventRecordId);
        }
        finally
        {
            TestSupport.DeleteDatabase(source);
            TestSupport.DeleteDatabase(backup);
            TestSupport.DeleteDatabase(restored);
            DeleteIfExists(backup + ".manifest.json");
        }
    }

    [Fact]
    public void Corrupt_backup_fails_closed_and_leaves_the_target_untouched()
    {
        var source = TestSupport.NewDatabasePath();
        var backup = TestSupport.NewDatabasePath();
        var target = TestSupport.NewDatabasePath();
        try
        {
            using (var store = new SqliteEventStore(source))
            {
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
            }

            BackupService.Create(source, backup);
            using (var stream = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Position = stream.Length / 2;
                var value = stream.ReadByte();
                stream.Position--;
                stream.WriteByte((byte)(value ^ 0xff));
            }

            File.Copy(source, target);
            var targetHash = Hash(target);
            var error = Assert.Throws<InvalidDataException>(() => BackupService.Restore(backup, target));
            Assert.Contains("hash", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(targetHash, Hash(target));
        }
        finally
        {
            TestSupport.DeleteDatabase(source);
            TestSupport.DeleteDatabase(backup);
            TestSupport.DeleteDatabase(target);
            DeleteIfExists(backup + ".manifest.json");
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
