using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Serialization;

namespace Sentinel.Core.Rules;

public sealed record SigmaImportResult(string Source, string Output, string RuleId);

/// <summary>
/// Converts a bounded, single-selection Sigma subset into Sentinel rule YAML.
/// Unsupported fields, modifiers, and correlation forms are rejected rather
/// than approximated.
/// </summary>
public static class SigmaImporter
{
    private static readonly Dictionary<string, string> FieldMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Image"] = "process.file.path",
        ["CommandLine"] = "process.cmd_line",
        ["DestinationIp"] = "network.dst.ip",
        ["DestinationPort"] = "network.dst.port",
        ["SourceIp"] = "network.src.ip",
        ["SourcePort"] = "network.src.port",
        ["Protocol"] = "network.protocol"
    };

    public static IReadOnlyList<SigmaImportResult> ImportDirectory(
        string inputDirectory,
        string outputDirectory,
        bool overwrite = false)
    {
        if (!Directory.Exists(inputDirectory))
        {
            throw new DirectoryNotFoundException($"Sigma input directory not found: {inputDirectory}");
        }

        var files = Directory
            .EnumerateFiles(inputDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            throw new UnsupportedRuleException($"No Sigma files found in '{inputDirectory}'.");
        }

        if (Directory.Exists(outputDirectory) && !overwrite)
        {
            var existing = Directory
                .EnumerateFiles(outputDirectory, "*.*", SearchOption.TopDirectoryOnly)
                .Any(path => path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
            if (existing)
            {
                throw new UnsupportedRuleException(
                    $"output directory '{outputDirectory}' already contains YAML rules; use an empty directory or --force.");
            }
        }

        var converted = new List<(string Source, string Yaml, string RuleId, string Output)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var (yaml, ruleId) = ConvertRule(File.ReadAllText(file), Path.GetFileName(file));
            var name = SafeName(ruleId) + ".yaml";
            if (!names.Add(name))
            {
                throw new UnsupportedRuleException(
                    $"Sigma inputs produce the same output file '{name}'; refine the rule ids.");
            }

            converted.Add((file, yaml, ruleId, Path.Combine(outputDirectory, name)));
        }

        Directory.CreateDirectory(outputDirectory);
        var results = new List<SigmaImportResult>();
        foreach (var item in converted)
        {
            try
            {
                using var stream = new FileStream(
                    item.Output,
                    overwrite ? FileMode.Create : FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(item.Yaml);
            }
            catch (IOException error) when (!overwrite)
            {
                throw new UnsupportedRuleException(
                    $"output file '{item.Output}' already exists; use --force to overwrite.",
                    error);
            }

            results.Add(new SigmaImportResult(item.Source, item.Output, item.RuleId));
        }

        return results;
    }

    public static (string Yaml, string RuleId) ConvertRule(string sigmaYaml, string sourceName)
    {
        var deserializer = new DeserializerBuilder().Build();
        Dictionary<object, object> root;
        try
        {
            root = deserializer.Deserialize<Dictionary<object, object>>(sigmaYaml)
                ?? throw new UnsupportedRuleException($"Sigma rule '{sourceName}' is empty.");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new UnsupportedRuleException($"Sigma rule '{sourceName}' is not valid YAML: {ex.Message}");
        }

        var title = RequiredString(root, "title", sourceName);
        if (!root.TryGetValue("detection", out var detectionValue)
            || detectionValue is not Dictionary<object, object> detection)
        {
            throw new UnsupportedRuleException($"Sigma rule '{sourceName}' has no detection map.");
        }

        var condition = RequiredString(detection, "condition", sourceName);
        if (!string.Equals(condition.Trim(), "selection", StringComparison.Ordinal))
        {
            throw new UnsupportedRuleException(
                $"Sigma rule '{sourceName}' uses unsupported condition '{condition}'; only 'selection' is supported.");
        }

        if (!detection.TryGetValue("selection", out var selectionValue)
            || selectionValue is not Dictionary<object, object> selection
            || selection.Count == 0)
        {
            throw new UnsupportedRuleException($"Sigma rule '{sourceName}' has no non-empty selection.");
        }

        var nativeSelection = new Dictionary<object, object>();
        foreach (var (key, value) in selection)
        {
            var fieldText = Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture)
                ?? throw new UnsupportedRuleException($"Sigma rule '{sourceName}' has an empty field.");
            var parts = fieldText.Split('|');
            if (parts.Length > 2)
            {
                throw new UnsupportedRuleException(
                    $"Sigma rule '{sourceName}' uses unsupported modifier chain '{fieldText}'.");
            }

            var field = parts[0];
            if (!FieldMap.TryGetValue(field, out var mapped))
            {
                throw new UnsupportedRuleException(
                    $"Sigma rule '{sourceName}' uses unsupported field '{field}'.");
            }

            var modifier = parts.Length == 2 ? parts[1].ToLowerInvariant() : null;
            if (!nativeSelection.TryAdd(mapped, ConvertValue(sourceName, mapped, modifier, value)))
            {
                throw new UnsupportedRuleException(
                    $"Sigma rule '{sourceName}' maps more than one condition to '{mapped}'.");
            }
        }

        if (!root.TryGetValue("logsource", out var logsourceValue)
            || logsourceValue is not Dictionary<object, object> logsource)
        {
            throw new UnsupportedRuleException($"Sigma rule '{sourceName}' has no logsource map.");
        }

        var product = OptionalString(logsource, "product");
        if (product is not null && !string.Equals(product, "windows", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedRuleException(
                $"Sigma rule '{sourceName}' targets unsupported product '{product}'.");
        }

        var service = OptionalString(logsource, "service");
        if (!string.IsNullOrWhiteSpace(service))
        {
            throw new UnsupportedRuleException(
                $"Sigma rule '{sourceName}' uses unsupported logsource service '{service}'.");
        }

        var category = RequiredString(logsource, "category", sourceName).ToLowerInvariant();
        var eventId = category switch
        {
            "process_creation" => "1",
            "network_connection" => "3",
            "process_termination" => "5",
            _ => throw new UnsupportedRuleException(
                $"Sigma rule '{sourceName}' uses unsupported logsource category '{category}'.")
        };
        nativeSelection["event.id"] = new Dictionary<object, object> { ["equals"] = eventId };

        var sigmaId = OptionalString(root, "id") ?? StableId(title, nativeSelection);
        var ruleId = sigmaId.StartsWith("sigma.", StringComparison.Ordinal) ? sigmaId : "sigma." + sigmaId;
        var document = new Dictionary<object, object>
        {
            ["id"] = ruleId,
            ["version"] = "1",
            ["title"] = title,
            ["status"] = "experimental",
            ["description"] = $"Imported Sigma subset from {sourceName}.",
            ["author"] = OptionalString(root, "author") ?? "sigma-import",
            ["license"] = OptionalString(root, "license") ?? string.Empty,
            ["sigma_id"] = sigmaId,
            ["logsource"] = logsource,
            ["detection"] = new Dictionary<object, object>
            {
                ["selection"] = nativeSelection,
                ["condition"] = "selection"
            }
        };

        var serializer = new SerializerBuilder().Build();
        return (serializer.Serialize(document), ruleId);
    }

    private static Dictionary<object, object> ConvertValue(
        string sourceName,
        string field,
        string? modifier,
        object value)
    {
        if (value is string scalar)
        {
            return new Dictionary<object, object>
            {
                [Operator(modifier, sourceName)] = Literal(sourceName, field, scalar)
            };
        }

        if (value is List<object> list)
        {
            var values = list
                .Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture))
                .Where(item => item is not null)
                .Cast<string>()
                .Select(item => Literal(sourceName, field, item))
                .ToList();
            if (values.Count == 0 || values.Any(string.IsNullOrEmpty))
            {
                throw new UnsupportedRuleException(
                    $"Sigma rule '{sourceName}' field '{field}' has an empty value list or alternative.");
            }

            return modifier switch
            {
                null => new Dictionary<object, object> { ["in"] = values },
                "contains" => new Dictionary<object, object> { ["contains_any"] = values },
                "startswith" => new Dictionary<object, object> { ["starts_with"] = values },
                "endswith" => new Dictionary<object, object> { ["ends_with"] = values },
                _ => throw new UnsupportedRuleException(
                    $"Sigma rule '{sourceName}' uses unsupported modifier '{modifier}' on a value list.")
            };
        }

        throw new UnsupportedRuleException(
            $"Sigma rule '{sourceName}' field '{field}' has an unsupported value type.");
    }

    private static string Literal(string sourceName, string field, string value)
    {
        if (value.Contains('*') || value.Contains('?'))
        {
            throw new UnsupportedRuleException(
                $"Sigma rule '{sourceName}' uses an unsupported wildcard pattern for '{field}'.");
        }

        if (field.EndsWith(".ip", StringComparison.Ordinal) && value.Contains('/'))
        {
            throw new UnsupportedRuleException(
                $"Sigma rule '{sourceName}' uses an unsupported CIDR pattern for '{field}'.");
        }

        return value;
    }

    private static string Operator(string? modifier, string sourceName) => modifier switch
    {
        null => "equals",
        "contains" => "contains",
        "startswith" => "starts_with",
        "endswith" => "ends_with",
        _ => throw new UnsupportedRuleException(
            $"Sigma rule '{sourceName}' uses unsupported modifier '{modifier}'.")
    };

    private static string StableId(string title, Dictionary<object, object> selection)
    {
        var serializer = new SerializerBuilder().Build();
        var text = title + "\n" + serializer.Serialize(selection);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sigma." + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string SafeName(string ruleId)
    {
        var builder = new StringBuilder();
        foreach (var character in ruleId)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-');
        }

        return builder.ToString().Trim('-', '.');
    }

    private static string RequiredString(Dictionary<object, object> map, string key, string source)
    {
        var value = OptionalString(map, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UnsupportedRuleException($"Sigma rule '{source}' is missing '{key}'.");
        }

        return value;
    }

    private static string? OptionalString(Dictionary<object, object> map, string key) =>
        map.TryGetValue(key, out var value)
            ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
}
