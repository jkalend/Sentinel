using System.Text.Json;
using System.Text.Json.Serialization;
using Sentinel.Core.Runs;
using Sentinel.Core.Store;

namespace Sentinel.Core.Config;

public sealed record FleetConfig(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("config_id")] string ConfigId,
    [property: JsonPropertyName("sensor_id")] string? SensorId,
    [property: JsonPropertyName("host_id")] string? HostId,
    [property: JsonPropertyName("rule_pack_sha256")] string? RulePackSha256,
    [property: JsonPropertyName("retention")] RetentionPolicy Retention,
    [property: JsonPropertyName("allowed_collectors")] IReadOnlyList<string> AllowedCollectors,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("tenant_id")] string? TenantId = null)
{
    public const string CurrentFormat = "sentinel.fleet-config/v1";

    public string ComputeSha256() =>
        CanonicalJson.Hash(new
        {
            format = Format,
            config_id = ConfigId,
            sensor_id = SensorId,
            host_id = HostId,
            tenant_id = TenantId,
            rule_pack_sha256 = RulePackSha256,
            retention = new
            {
                event_days = Retention.EventDays,
                raw_days = Retention.RawDays,
                alert_days = Retention.AlertDays,
                source_health_days = Retention.SourceHealthDays,
                gap_days = Retention.GapDays,
                keep_events = Retention.KeepEvents,
                max_bytes = Retention.MaxBytes,
                extended_retention = Retention.ExtendedRetention
            },
            allowed_collectors = AllowedCollectors
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList()
        });

    public FleetConfig Validate()
    {
        if (!string.Equals(Format, CurrentFormat, StringComparison.Ordinal))
        {
            throw new ArgumentException($"unsupported config format '{Format}'.");
        }

        if (string.IsNullOrWhiteSpace(ConfigId))
        {
            throw new ArgumentException("config_id is required.");
        }

        if (TenantId is not null
            && (TenantId.Length > 128 || TenantId.Any(char.IsControl)))
        {
            throw new ArgumentException("tenant_id must be at most 128 printable characters.");
        }

        if (Retention is null)
        {
            throw new ArgumentException("retention is required.");
        }

        Retention.Validate();
        var collectors = AllowedCollectors ?? [];
        if (collectors.Count != collectors.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new ArgumentException("allowed_collectors contains duplicates.");
        }

        foreach (var collector in collectors)
        {
            if (!Uri.TryCreate(collector, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException($"allowed collector '{collector}' must be an absolute http(s) URI.");
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException($"allowed collector '{collector}' must not contain a query or fragment.");
            }
        }

        return this;
    }
}

public sealed record ConfigGenerationRecord(
    int Generation,
    string ConfigSha256,
    string DocumentJson,
    DateTimeOffset AppliedAt);

public sealed record ConfigApplyReport(
    int Generation,
    string ConfigSha256,
    FleetConfig Config,
    bool AlreadyActive);

public static class FleetConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static FleetConfig LoadFile(string path)
    {
        FleetConfig config;
        try
        {
            config = JsonSerializer.Deserialize<FleetConfig>(File.ReadAllText(path), JsonOptions)
                ?? throw new ArgumentException($"config file '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"config file '{path}' is not valid JSON: {ex.Message}");
        }

        return Normalize(config);
    }

    public static FleetConfig Parse(string json) => Normalize(
        JsonSerializer.Deserialize<FleetConfig>(json, JsonOptions)
        ?? throw new ArgumentException("config document is empty."));

    public static ConfigApplyReport Apply(SqliteEventStore store, FleetConfig config)
    {
        config = Normalize(config).Validate();
        var enrollment = store.GetEnrollment();
        if (config.SensorId is not null)
        {
            if (enrollment is null || !string.Equals(enrollment.SensorId, config.SensorId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"config sensor_id '{config.SensorId}' does not match the store enrollment.");
            }
        }

        if (config.HostId is not null)
        {
            if (enrollment is null || !string.Equals(enrollment.HostId, config.HostId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"config host_id '{config.HostId}' does not match the store enrollment.");
            }
        }

        if (config.RulePackSha256 is not null && store.GetRulePack(config.RulePackSha256) is null)
        {
            throw new ArgumentException($"rule pack '{config.RulePackSha256}' is not installed.");
        }

        var sha = config.ComputeSha256();
        var active = store.GetActiveConfig();
        if (active is not null && string.Equals(active.ConfigSha256, sha, StringComparison.OrdinalIgnoreCase))
        {
            return new ConfigApplyReport(active.Generation, sha, config, AlreadyActive: true);
        }

        var generation = store.InsertConfig(sha, JsonSerializer.Serialize(config, JsonOptions));
        if (config.RulePackSha256 is not null)
        {
            store.ActivateRulePack(config.RulePackSha256);
        }

        return new ConfigApplyReport(generation, sha, config, AlreadyActive: false);
    }

    public static ConfigApplyReport Rollback(SqliteEventStore store)
    {
        var previous = store.RollbackConfig();
        var config = Parse(previous.DocumentJson).Validate();
        if (config.RulePackSha256 is not null)
        {
            store.ActivateRulePack(config.RulePackSha256);
        }

        return new ConfigApplyReport(previous.Generation, previous.ConfigSha256, config, AlreadyActive: false);
    }

    public static IReadOnlyList<string> Drift(SqliteEventStore store)
    {
        var issues = new List<string>();
        var active = store.GetActiveConfig();
        if (active is null)
        {
            issues.Add("no active fleet configuration.");
            return issues;
        }

        FleetConfig config;
        try
        {
            config = Parse(active.DocumentJson).Validate();
        }
        catch (Exception ex)
        {
            issues.Add($"active config cannot be parsed: {ex.Message}");
            return issues;
        }

        var enrollment = store.GetEnrollment();
        if (enrollment is null)
        {
            issues.Add("store has no enrollment.");
        }
        else
        {
            if (config.SensorId is not null
                && !string.Equals(config.SensorId, enrollment.SensorId, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"sensor_id drift: config '{config.SensorId}', store '{enrollment.SensorId}'.");
            }

            if (config.HostId is not null
                && !string.Equals(config.HostId, enrollment.HostId, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"host_id drift: config '{config.HostId}', store '{enrollment.HostId}'.");
            }
        }

        if (config.RulePackSha256 is not null)
        {
            if (store.GetRulePack(config.RulePackSha256) is null)
            {
                issues.Add($"pinned rule pack '{config.RulePackSha256}' is not installed.");
            }

            var activePack = store.GetActiveRulePack();
            if (activePack is null
                || !string.Equals(activePack.PackSha256, config.RulePackSha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"pinned rule pack '{config.RulePackSha256}' is not the active pack.");
            }
        }

        return issues;
    }

    public static bool IsCollectorAllowed(FleetConfig config, string collector) =>
        config.AllowedCollectors.Count == 0
        || config.AllowedCollectors.Any(value =>
            string.Equals(NormalizeCollector(value), NormalizeCollector(collector), StringComparison.OrdinalIgnoreCase));

    private static FleetConfig Normalize(FleetConfig config) => config with
    {
        Retention = config.Retention ?? new RetentionPolicy(),
        AllowedCollectors = (config.AllowedCollectors ?? [])
            .Select(NormalizeCollector)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        CreatedAt = config.CreatedAt == default ? DateTimeOffset.UtcNow : config.CreatedAt
    };

    private static string NormalizeCollector(string value) =>
        value.EndsWith('/') ? value : value + "/";
}
