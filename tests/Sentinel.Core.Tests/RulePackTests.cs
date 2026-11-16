using Sentinel.Core.Rules;
using Sentinel.Core.Store;

namespace Sentinel.Core.Tests;

public class RulePackTests
{
    [Fact]
    public void Pack_hash_is_stable_and_content_verified()
    {
        var first = RulePackBuilder.Build(
            TestSupport.RulesPath,
            "sentinel.base",
            "1",
            DateTimeOffset.Parse("2026-11-16T00:00:00Z"));
        var second = RulePackBuilder.Build(
            TestSupport.RulesPath,
            "sentinel.base",
            "1",
            DateTimeOffset.Parse("2026-11-17T00:00:00Z"));

        Assert.Equal(first.PackSha256, second.PackSha256);
        Assert.Equal(3, first.Manifest.Rules.Count);
    }

    [Fact]
    public void Pack_hash_is_independent_of_line_endings()
    {
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var file in Directory.EnumerateFiles(TestSupport.RulesPath, "*.yaml"))
            {
                var text = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
                File.WriteAllText(
                    Path.Combine(directory, Path.GetFileName(file)),
                    text.Replace("\n", "\r\n", StringComparison.Ordinal));
            }

            var lf = RulePackBuilder.Build(TestSupport.RulesPath, "sentinel.base", "1");
            var crlf = RulePackBuilder.Build(directory, "sentinel.base", "1");
            Assert.Equal(lf.PackSha256, crlf.PackSha256);

            RulePackBuilder.Write(crlf, Path.Combine(directory, "pack"));
            var loaded = RulePackBuilder.LoadDirectory(Path.Combine(directory, "pack"));
            Assert.Equal(lf.PackSha256, loaded.PackSha256);
            Assert.Equal(3, loaded.ToRuleSet().Rules.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Tampered_pack_file_is_rejected()
    {
        var directory = NewDirectory();
        try
        {
            var pack = RulePackBuilder.Build(TestSupport.RulesPath, "sentinel.base", "1");
            RulePackBuilder.Write(pack, directory);
            var entry = pack.Manifest.Rules.Single(rule => rule.File == "encoded-powershell.yaml");
            var file = Path.Combine(directory, entry.File);
            File.WriteAllText(file, File.ReadAllText(file).Replace("version: 1", "version: 2"));
            Assert.NotEqual(entry.Sha256, RulePackBuilder.Sha256Text(File.ReadAllText(file)));

            Assert.Throws<UnsupportedRuleException>(() => RulePackBuilder.LoadDirectory(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Activated_pack_records_alert_provenance_and_replays()
    {
        var database = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(database);
            store.Initialize();
            var pack = RulePackBuilder.Build(TestSupport.RulesPath, "sentinel.base", "1");
            store.InstallRulePack(pack);
            var generation = store.ActivateRulePack(pack.PackSha256);
            Assert.Equal(1, generation);
            Assert.Equal(pack.PackSha256, store.GetActiveRulePack()!.PackSha256);

            var fixture = TestSupport.Fixture();
            var (sensorId, hostId) = store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            new IngestionService(store, sensorId, hostId).Ingest(
                fixture.Records,
                store.GetActiveRulePack()!.ToRuleSet(),
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);

            var alerts = store.ReadAlerts();
            Assert.Equal(3, alerts.Count);
            Assert.All(alerts, alert =>
            {
                Assert.Equal("sentinel.base", alert.PackId);
                Assert.Equal(pack.PackSha256, alert.PackSha256);
            });
            Assert.True(ReplayService.Replay(store, pack.ToRuleSet()).Matches);
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
        }
    }

    [Fact]
    public void Activation_history_and_rollback_are_audited()
    {
        var database = TestSupport.NewDatabasePath();
        var rulesCopy = NewDirectory();
        Directory.CreateDirectory(rulesCopy);
        try
        {
            using var store = new SqliteEventStore(database);
            store.Initialize();
            var first = RulePackBuilder.Build(TestSupport.RulesPath, "sentinel.base", "1");
            foreach (var file in Directory.EnumerateFiles(TestSupport.RulesPath, "*.yaml"))
            {
                File.Copy(file, Path.Combine(rulesCopy, Path.GetFileName(file)));
            }

            var changed = Path.Combine(rulesCopy, "encoded-powershell.yaml");
            File.WriteAllText(changed, File.ReadAllText(changed).Replace("version: 1", "version: 99"));
            var second = RulePackBuilder.Build(rulesCopy, "sentinel.base", "2");

            store.InstallRulePack(first);
            store.InstallRulePack(second);
            store.ActivateRulePack(first.PackSha256);
            store.ActivateRulePack(second.PackSha256);
            var rolled = store.RollbackRulePack();

            Assert.Equal(first.PackSha256, rolled!.PackSha256);
            Assert.Equal(first.PackSha256, store.GetActiveRulePack()!.PackSha256);
            var history = store.ReadRulePackActivations();
            Assert.Equal(3, history.Count);
            Assert.True(history[0].Active);
            Assert.Equal(first.PackSha256, history[0].PackSha256);
            Assert.Equal(2, store.ListRulePacks().Count);
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
            Directory.Delete(rulesCopy, recursive: true);
        }
    }

    [Fact]
    public void Run_manifest_pins_pack_and_alert_provenance()
    {
        var database = TestSupport.NewDatabasePath();
        var output = NewDirectory();
        try
        {
            using var store = new SqliteEventStore(database);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            var (sensorId, hostId) = store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            var pack = RulePackBuilder.Build(TestSupport.RulesPath, "sentinel.base", "1");
            new IngestionService(store, sensorId, hostId).Ingest(
                fixture.Records,
                pack.ToRuleSet(),
                fixture.Channel,
                fixture.IncarnationId,
                fixture.ConfigGeneration,
                fixture.GroundTruth);

            var manifest = Sentinel.Core.Runs.RunService.Create(
                store,
                pack.ToRuleSet(),
                TestSupport.SchemaPath,
                output);

            Assert.Equal("sentinel.base", manifest.Identity.PackId);
            Assert.Equal(pack.PackSha256, manifest.Identity.PackSha256);
            Assert.All(manifest.Alerts, alert => Assert.Equal(pack.PackSha256, alert.PackSha256));
            Assert.True(Sentinel.Core.Runs.RunService.Verify(manifest, output).Ok);
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
            Directory.Delete(output, recursive: true);
        }
    }

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), $"sentinel-pack-{Guid.NewGuid():N}");
}
