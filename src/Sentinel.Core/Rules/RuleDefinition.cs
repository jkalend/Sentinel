using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sentinel.Core.Model;
using Sentinel.Core.Ocsf;
using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Rules;

public sealed record FieldCondition(
    string Field,
    string Operator,
    IReadOnlyList<string> Values,
    bool? Exists);

public sealed record RuleDefinition(
    string Id,
    string Version,
    string Title,
    IReadOnlyList<FieldCondition> Selection,
    string SourcePath);

public sealed class UnsupportedRuleException : Exception
{
    public UnsupportedRuleException(string message)
        : base(message)
    {
    }
}

public sealed class RuleSet
{
    private readonly Dictionary<string, RuleDefinition> _rules;

    public RuleSet(IEnumerable<RuleDefinition> rules)
    {
        var list = rules.ToList();
        var duplicate = list
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new UnsupportedRuleException($"Duplicate rule identity '{duplicate.Key}'.");
        }

        _rules = list.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<RuleDefinition> Rules => _rules.Values;
}

public sealed record RuleEventView(
    string? ProcessName,
    string? ProcessCmdLine,
    string? ProcessPath,
    string? ProcessFileName,
    int? ProcessPid,
    string? ProcessGuid,
    int? ActivityId,
    string? Protocol,
    string? DestinationIp,
    int? DestinationPort,
    string? SourceIp,
    int? SourcePort)
{
    public static RuleEventView From(LocalEvent value)
    {
        var path = value.Process?.ImagePath;
        var activityId = value.OcsfClassUid == OcsfMapper.NetworkActivityClassUid
            ? value.OcsfActivityId
            : null;
        return new RuleEventView(
            value.Process?.Name,
            value.Process?.CommandLine,
            path,
            path is null ? null : WindowsPath.FileName(path),
            value.Process?.Pid,
            value.Process?.Guid,
            activityId,
            value.Network?.Protocol,
            value.Network?.DestinationIp,
            value.Network?.DestinationPort,
            value.Network?.SourceIp,
            value.Network?.SourcePort);
    }

    public string? Resolve(string field) => field switch
    {
        "process.name" => ProcessName,
        "process.cmd_line" => ProcessCmdLine,
        "process.file.path" => ProcessPath,
        "process.file.name" => ProcessFileName,
        "process.pid" => ProcessPid?.ToString(CultureInfo.InvariantCulture),
        "process.uid" => ProcessGuid,
        "network.activity_id" => ActivityId?.ToString(CultureInfo.InvariantCulture),
        "network.protocol" => Protocol,
        "network.dst.ip" => DestinationIp,
        "network.dst.port" => DestinationPort?.ToString(CultureInfo.InvariantCulture),
        "network.src.ip" => SourceIp,
        "network.src.port" => SourcePort?.ToString(CultureInfo.InvariantCulture),
        _ => throw new UnsupportedRuleException($"Unsupported rule field '{field}'.")
    };
}

public static class RuleEvaluator
{
    public static IReadOnlyList<AlertCandidate> Evaluate(LocalEvent value, RuleSet rules)
    {
        var view = RuleEventView.From(value);
        var alerts = new List<AlertCandidate>();
        foreach (var rule in rules.Rules.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            var matched = new Dictionary<string, string?>(StringComparer.Ordinal);
            var all = true;
            foreach (var condition in rule.Selection)
            {
                var actual = view.Resolve(condition.Field);
                if (!Matches(condition, actual))
                {
                    all = false;
                    break;
                }

                matched[condition.Field] = actual;
            }

            if (!all)
            {
                continue;
            }

            var details = JsonSerializer.Serialize(new
            {
                rule = rule.Id,
                version = rule.Version,
                title = rule.Title,
                matched
            });

            alerts.Add(new AlertCandidate(
                AlertId(rule.Id, rule.Version, value.SourceKey),
                rule.Id,
                rule.Version,
                SentinelVersions.EngineVersion,
                value.SourceKey,
                DateTimeOffset.UtcNow,
                details));
        }

        return alerts;
    }

    public static string AlertId(string ruleId, string ruleVersion, string sourceKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{ruleId}\n{ruleVersion}\n{sourceKey}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool Matches(FieldCondition condition, string? actual)
    {
        if (condition.Operator == "exists")
        {
            return condition.Exists == (actual is not null);
        }

        if (actual is null)
        {
            return false;
        }

        return condition.Operator switch
        {
            "equals" => string.Equals(actual, condition.Values[0], StringComparison.OrdinalIgnoreCase),
            "contains" => actual.Contains(condition.Values[0], StringComparison.OrdinalIgnoreCase),
            "contains_any" => condition.Values.Any(v => actual.Contains(v, StringComparison.OrdinalIgnoreCase)),
            "starts_with" => condition.Values.Any(v => actual.StartsWith(v, StringComparison.OrdinalIgnoreCase)),
            "ends_with" => condition.Values.Any(v => actual.EndsWith(v, StringComparison.OrdinalIgnoreCase)),
            "in" => condition.Values.Any(v => string.Equals(actual, v, StringComparison.OrdinalIgnoreCase)),
            "not_in" => condition.Values.All(v => !string.Equals(actual, v, StringComparison.OrdinalIgnoreCase)),
            _ => throw new UnsupportedRuleException($"Unsupported rule operator '{condition.Operator}'.")
        };
    }
}
