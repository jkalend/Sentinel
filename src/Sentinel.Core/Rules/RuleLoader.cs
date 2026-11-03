using YamlDotNet.Serialization;

namespace Sentinel.Core.Rules;

public static class RuleLoader
{
    private static readonly HashSet<string> SupportedOperators = new(StringComparer.Ordinal)
    {
        "equals",
        "contains",
        "contains_any",
        "starts_with",
        "ends_with",
        "in",
        "not_in",
        "exists"
    };

    public static RuleSet LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Rule directory not found: {directory}");
        }

        var files = Directory
            .EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            throw new UnsupportedRuleException($"No rule files found in '{directory}'.");
        }

        return new RuleSet(files.Select(LoadFile));
    }

    public static RuleDefinition LoadFile(string path)
    {
        var deserializer = new DeserializerBuilder().Build();
        Dictionary<object, object> root;
        try
        {
            root = deserializer.Deserialize<Dictionary<object, object>>(File.ReadAllText(path))
                ?? throw new UnsupportedRuleException($"Rule file '{path}' is empty.");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new UnsupportedRuleException($"Rule file '{path}' is not valid YAML: {ex.Message}");
        }

        var id = RequiredString(root, "id", path);
        var version = RequiredString(root, "version", path);
        var title = RequiredString(root, "title", path);

        if (!root.TryGetValue("detection", out var detectionValue)
            || detectionValue is not Dictionary<object, object> detection)
        {
            throw new UnsupportedRuleException($"Rule '{id}' has no detection map.");
        }

        var condition = RequiredString(detection, "condition", path);
        if (!string.Equals(condition, "selection", StringComparison.Ordinal))
        {
            throw new UnsupportedRuleException(
                $"Rule '{id}' uses unsupported condition '{condition}'; V0.1 supports only 'selection'.");
        }

        if (!detection.TryGetValue("selection", out var selectionValue)
            || selectionValue is not Dictionary<object, object> selection
            || selection.Count == 0)
        {
            throw new UnsupportedRuleException($"Rule '{id}' has no non-empty selection map.");
        }

        var conditions = selection
            .Select(entry => ParseCondition(id, entry.Key, entry.Value))
            .ToList();

        return new RuleDefinition(id, version, title, conditions, path);
    }

    private static FieldCondition ParseCondition(string ruleId, object key, object value)
    {
        var field = Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new UnsupportedRuleException($"Rule '{ruleId}' has an empty field name.");
        if (field is not ("process.name"
            or "process.cmd_line"
            or "process.file.path"
            or "process.file.name"
            or "process.pid"
            or "process.uid"
            or "network.activity_id"
            or "network.protocol"
            or "network.dst.ip"
            or "network.dst.port"
            or "network.src.ip"
            or "network.src.port"))
        {
            throw new UnsupportedRuleException($"Rule '{ruleId}' uses unsupported field '{field}'.");
        }

        if (value is string scalar)
        {
            return new FieldCondition(field, "equals", [scalar], null);
        }

        if (value is not Dictionary<object, object> operators || operators.Count != 1)
        {
            throw new UnsupportedRuleException(
                $"Rule '{ruleId}' field '{field}' must use exactly one operator or a scalar equality.");
        }

        var (operatorKey, operand) = operators.Single();
        var op = Convert.ToString(operatorKey, System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new UnsupportedRuleException($"Rule '{ruleId}' has an empty operator.");

        if (!SupportedOperators.Contains(op))
        {
            throw new UnsupportedRuleException($"Rule '{ruleId}' uses unsupported operator '{op}'.");
        }

        if (op == "exists")
        {
            var exists = operand switch
            {
                bool booleanValue => booleanValue,
                string text when bool.TryParse(text, out var parsed) => parsed,
                _ => throw new UnsupportedRuleException(
                    $"Rule '{ruleId}' operator 'exists' requires a boolean.")
            };

            return new FieldCondition(field, op, [], exists);
        }

        List<string> values = operand switch
        {
            string single => new List<string> { single },
            List<object> list => list
                .Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
                .Where(item => item.Length > 0)
                .ToList(),
            _ => throw new UnsupportedRuleException(
                $"Rule '{ruleId}' operator '{op}' requires a scalar or a list of scalars.")
        };

        if (values.Count == 0)
        {
            throw new UnsupportedRuleException($"Rule '{ruleId}' operator '{op}' has no values.");
        }

        return new FieldCondition(field, op, values, null);
    }

    private static string RequiredString(Dictionary<object, object> map, string key, string path)
    {
        if (!map.TryGetValue(key, out var value)
            || Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) is not { Length: > 0 } text)
        {
            throw new UnsupportedRuleException($"Rule file '{path}' is missing '{key}'.");
        }

        return text;
    }
}
