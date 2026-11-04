using System.Text.Json.Nodes;
using Sentinel.Core.Model;
using Sentinel.Core.Runs;
using Sentinel.Core.Store;

namespace Sentinel.Core.Tests;

public class RunTests
{
    [Fact]
    public void Same_store_produces_same_run_content_and_unique_manifest()
    {
        var database = TestSupport.NewDatabasePath();
        var first = NewDirectory();
        var second = NewDirectory();
        try
        {
            using var store = CreateStore(database);
            var rules = TestSupport.Rules();
            var manifest1 = RunService.Create(store, rules, TestSupport.SchemaPath, first);
            var manifest2 = RunService.Create(store, rules, TestSupport.SchemaPath, second);

            Assert.Equal(manifest1.ContentSha256, manifest2.ContentSha256);
            Assert.NotEqual(manifest1.RunId, manifest2.RunId);
            Assert.Equal(10, manifest1.Coverage.EventCount);
            Assert.Equal(3, manifest1.Coverage.AlertCount);
            Assert.False(manifest1.Export.PayloadIncluded);

            var loaded = RunService.ReadManifest(Path.Combine(first, "manifest.json"));
            Assert.Equal(manifest1.ManifestSha256, loaded.ManifestSha256);
            var verification = RunService.Verify(loaded, first);
            Assert.True(verification.Ok);
            Assert.Equal(10, verification.Included);
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
            DeleteDirectory(first);
            DeleteDirectory(second);
        }
    }

    [Fact]
    public void Fresh_ingest_of_the_same_fixture_has_the_same_content_hash()
    {
        var firstDb = TestSupport.NewDatabasePath();
        var secondDb = TestSupport.NewDatabasePath();
        var first = NewDirectory();
        var second = NewDirectory();
        try
        {
            using var firstStore = CreateStore(firstDb);
            using var secondStore = CreateStore(secondDb);
            var firstManifest = RunService.Create(firstStore, TestSupport.Rules(), TestSupport.SchemaPath, first);
            var secondManifest = RunService.Create(secondStore, TestSupport.Rules(), TestSupport.SchemaPath, second);

            Assert.Equal(firstManifest.ContentSha256, secondManifest.ContentSha256);
            Assert.NotEqual(firstManifest.ManifestSha256, secondManifest.ManifestSha256);
        }
        finally
        {
            TestSupport.DeleteDatabase(firstDb);
            TestSupport.DeleteDatabase(secondDb);
            DeleteDirectory(first);
            DeleteDirectory(second);
        }
    }

    [Fact]
    public void Payload_export_verifies_and_detects_tampering()
    {
        var database = TestSupport.NewDatabasePath();
        var output = NewDirectory();
        try
        {
            using var store = CreateStore(database);
            var manifest = RunService.Create(store, TestSupport.Rules(), TestSupport.SchemaPath, output, includePayload: true);
            Assert.True(manifest.Export.PayloadIncluded);
            Assert.Equal("selected-payload", manifest.Export.Mode);
            Assert.True(File.Exists(Path.Combine(output, "payload.jsonl")));
            Assert.True(RunService.Verify(manifest, output).Ok);

            var payloadPath = Path.Combine(output, "payload.jsonl");
            var original = File.ReadAllBytes(payloadPath);
            var text = File.ReadAllText(payloadPath).Replace("powershell", "power$hell", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(payloadPath, text);
            var changed = RunService.Verify(manifest, output);
            Assert.False(changed.Ok);
            Assert.False(changed.PayloadValid);
            Assert.True(changed.Changed > 0);

            File.WriteAllBytes(payloadPath, original.Take(original.Length / 2).ToArray());
            var truncated = RunService.Verify(manifest, output);
            Assert.False(truncated.Ok);
            Assert.True(truncated.Missing > 0);
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
            DeleteDirectory(output);
        }
    }

    [Fact]
    public void Payload_export_can_select_one_source_key()
    {
        var database = TestSupport.NewDatabasePath();
        var output = NewDirectory();
        try
        {
            using var store = CreateStore(database);
            var fixture = TestSupport.Fixture();
            var key = $"{fixture.SensorId}|{fixture.Channel}|{fixture.IncarnationId}|1001";
            var manifest = RunService.Create(
                store,
                TestSupport.Rules(),
                TestSupport.SchemaPath,
                output,
                includePayload: true,
                selectedSourceKeys: [key]);

            Assert.Single(manifest.Export.SelectedSourceKeys!);
            Assert.Equal(key, manifest.Export.SelectedSourceKeys![0]);
            Assert.Single(File.ReadAllLines(Path.Combine(output, "payload.jsonl")));
            Assert.True(manifest.Events.Single(value => value.SourceKey == key).PayloadIncluded);
            Assert.All(manifest.Events.Where(value => value.SourceKey != key), value => Assert.False(value.PayloadIncluded));
            Assert.True(RunService.Verify(manifest, output).Ok);
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
            DeleteDirectory(output);
        }
    }

    [Fact]
    public void Manifest_property_order_does_not_change_its_hash()
    {
        var database = TestSupport.NewDatabasePath();
        var output = NewDirectory();
        try
        {
            using var store = CreateStore(database);
            var manifest = RunService.Create(store, TestSupport.Rules(), TestSupport.SchemaPath, output);
            var path = Path.Combine(output, "manifest.json");
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var reordered = new JsonObject();
            foreach (var property in document.Reverse())
            {
                reordered[property.Key] = property.Value?.DeepClone();
            }

            File.WriteAllText(path, reordered.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            var loaded = RunService.ReadManifest(path);
            Assert.Equal(manifest.ManifestSha256, loaded.ManifestSha256);

            loaded = loaded with { RunId = Guid.NewGuid().ToString("D") };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(loaded));
            Assert.Throws<InvalidDataException>(() => RunService.ReadManifest(path));
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
            DeleteDirectory(output);
        }
    }

    [Fact]
    public void Verify_reports_events_expired_after_purge()
    {
        var database = TestSupport.NewDatabasePath();
        var output = NewDirectory();
        try
        {
            using var store = CreateStore(database);
            var manifest = RunService.Create(store, TestSupport.Rules(), TestSupport.SchemaPath, output);
            store.Purge(
                new RetentionPolicy(EventDays: 0, RawDays: 365, SourceHealthDays: 0, KeepEvents: 1),
                DateTimeOffset.Parse("2026-12-31T00:00:00Z"),
                apply: true);
            using var readOnly = new SqliteEventStore(database, readOnly: true);
            var verification = RunService.Verify(manifest, output, readOnly);
            Assert.False(verification.Ok);
            Assert.True(verification.Missing > 0);
            Assert.Equal(9, verification.Missing);
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
            DeleteDirectory(output);
        }
    }

    [Fact]
    public void Changed_rule_version_changes_run_content()
    {
        var database = TestSupport.NewDatabasePath();
        var rulesCopy = NewDirectory();
        var first = NewDirectory();
        var second = NewDirectory();
        try
        {
            Directory.CreateDirectory(rulesCopy);
            using var store = CreateStore(database);
            var original = TestSupport.Rules();
            var baseline = RunService.Create(store, original, TestSupport.SchemaPath, first);

            foreach (var file in Directory.EnumerateFiles(TestSupport.RulesPath, "*.yaml"))
            {
                File.Copy(file, Path.Combine(rulesCopy, Path.GetFileName(file)));
            }

            var changedFile = Path.Combine(rulesCopy, "encoded-powershell.yaml");
            File.WriteAllText(changedFile, File.ReadAllText(changedFile).Replace("version: 1", "version: 99"));
            var changedRules = Sentinel.Core.Rules.RuleLoader.LoadDirectory(rulesCopy);
            var changed = RunService.Create(store, changedRules, TestSupport.SchemaPath, second);

            Assert.NotEqual(baseline.Identity.RulesSha256, changed.Identity.RulesSha256);
            Assert.NotEqual(baseline.ContentSha256, changed.ContentSha256);
        }
        finally
        {
            TestSupport.DeleteDatabase(database);
            DeleteDirectory(rulesCopy);
            DeleteDirectory(first);
            DeleteDirectory(second);
        }
    }

    private static SqliteEventStore CreateStore(string path)
    {
        var store = new SqliteEventStore(path);
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
        return store;
    }

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), $"sentinel-run-{Guid.NewGuid():N}");

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
