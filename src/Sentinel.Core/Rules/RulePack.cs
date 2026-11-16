using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sentinel.Core.Model;
using Sentinel.Core.Runs;

namespace Sentinel.Core.Rules;

public sealed record RulePackEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record RulePackManifest(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("pack_id")] string PackId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("engine_version")] string EngineVersion,
    [property: JsonPropertyName("ocsf_version")] string OcsfVersion,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("rules")] IReadOnlyList<RulePackEntry> Rules)
{
    public const string CurrentFormat = "sentinel.rule-pack/v1";

    public string ComputeSha256() =>
        CanonicalJson.Hash(new
        {
            format = Format,
            pack_id = PackId,
            version = Version,
            engine_version = EngineVersion,
            ocsf_version = OcsfVersion,
            rules = Rules
                .OrderBy(rule => rule.Id, StringComparer.Ordinal)
                .Select(rule => new
                {
                    id = rule.Id,
                    version = rule.Version,
                    file = rule.File,
                    sha256 = rule.Sha256
                })
                .ToList()
        });
}

public sealed record RulePack(
    string PackSha256,
    RulePackManifest Manifest,
    IReadOnlyDictionary<string, string> YamlFiles)
{
    public RuleSet ToRuleSet() => RuleLoader.LoadPack(this);
}

public sealed record RulePackSummary(
    string PackSha256,
    string PackId,
    string Version,
    DateTimeOffset InstalledAt,
    string Source,
    bool Active,
    int? ActivationGeneration);

public sealed record RulePackActivation(
    int Generation,
    string PackSha256,
    DateTimeOffset ActivatedAt,
    bool Active);

public static class RulePackBuilder
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true
    };

    public static RulePack Build(
        string rulesDirectory,
        string packId,
        string version,
        DateTimeOffset? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(packId))
        {
            throw new ArgumentException("Pack id is required.", nameof(packId));
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Pack version is required.", nameof(version));
        }

        var ruleSet = RuleLoader.LoadDirectory(rulesDirectory);
        var entries = new List<RulePackEntry>();
        var yaml = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in ruleSet.Rules.OrderBy(value => value.Id, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(rule.SourcePath);
            if (!yaml.TryAdd(fileName, File.ReadAllText(rule.SourcePath)))
            {
                throw new UnsupportedRuleException($"Duplicate rule file name '{fileName}'.");
            }

            entries.Add(new RulePackEntry(
                rule.Id,
                rule.Version,
                fileName,
                Sha256Text(yaml[fileName])));
        }

        var manifest = new RulePackManifest(
            RulePackManifest.CurrentFormat,
            packId,
            version,
            SentinelVersions.EngineVersion,
            SentinelVersions.OcsfVersion,
            createdAt ?? DateTimeOffset.UtcNow,
            entries);

        var pack = new RulePack(manifest.ComputeSha256(), manifest, yaml);
        RuleLoader.LoadPack(pack);
        return pack;
    }

    public static void Write(RulePack pack, string outputDirectory)
    {
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, "manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(pack.Manifest, ManifestJson) + Environment.NewLine,
            new UTF8Encoding(false));

        foreach (var (fileName, text) in pack.YamlFiles)
        {
            File.WriteAllText(Path.Combine(directory, fileName), text, new UTF8Encoding(false));
        }
    }

    public static RulePack LoadDirectory(string packDirectory)
    {
        if (!Directory.Exists(packDirectory))
        {
            throw new DirectoryNotFoundException($"Rule pack directory not found: {packDirectory}");
        }

        var manifestPath = Path.Combine(packDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new UnsupportedRuleException($"Rule pack has no manifest.json: {packDirectory}");
        }

        RulePackManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<RulePackManifest>(File.ReadAllText(manifestPath))
                ?? throw new UnsupportedRuleException($"Rule pack manifest is empty: {manifestPath}");
        }
        catch (JsonException ex)
        {
            throw new UnsupportedRuleException($"Rule pack manifest is not valid JSON: {ex.Message}");
        }

        if (!string.Equals(manifest.Format, RulePackManifest.CurrentFormat, StringComparison.Ordinal))
        {
            throw new UnsupportedRuleException($"Unsupported rule pack format '{manifest.Format}'.");
        }

        var yaml = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Rules)
        {
            if (string.IsNullOrWhiteSpace(entry.File)
                || !string.Equals(Path.GetFileName(entry.File), entry.File, StringComparison.Ordinal))
            {
                throw new UnsupportedRuleException($"Rule pack entry '{entry.Id}' has an invalid file name.");
            }

            var path = Path.Combine(packDirectory, entry.File);
            if (!File.Exists(path))
            {
                throw new UnsupportedRuleException($"Rule pack file is missing: {entry.File}");
            }

            var text = File.ReadAllText(path);
            var actual = Sha256Text(text);
            if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnsupportedRuleException(
                    $"Rule pack file '{entry.File}' hash does not match the manifest.");
            }

            if (!yaml.TryAdd(entry.File, text))
            {
                throw new UnsupportedRuleException($"Rule pack lists '{entry.File}' more than once.");
            }
        }

        var extra = Directory
            .EnumerateFiles(packDirectory, "*.y*ml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null && name != "manifest.json" && !yaml.ContainsKey(name))
            .ToList();
        if (extra.Count > 0)
        {
            throw new UnsupportedRuleException(
                $"Rule pack contains unlisted rule files: {string.Join(", ", extra)}.");
        }

        var pack = new RulePack(manifest.ComputeSha256(), manifest, yaml);
        RuleLoader.LoadPack(pack);
        return pack;
    }

    public static string Sha256Text(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
