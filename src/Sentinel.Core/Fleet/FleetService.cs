using System.Globalization;
using System.Text;
using System.Text.Json;
using Sentinel.Core.Config;
using Sentinel.Core.Model;
using Sentinel.Core.Store;

namespace Sentinel.Core.Fleet;

public sealed record FleetHost(
    string Database,
    string? SensorId,
    string? HostId,
    string? HostName,
    string? EnrolledAt,
    int ConfigGeneration,
    long ClockOffsetMs,
    long ClockUncertaintyMs,
    int EventCount,
    int AlertCount,
    int GapCount,
    long? BookmarkRecordId,
    long? CoverageStartRecordId,
    string? FirstEventTime,
    string? LastEventTime,
    string? LastProcessedTime,
    string CoverageStatus,
    string OrderStatus,
    string ClockStatus,
    bool MissingTelemetry,
    bool NoActivityClaim,
    string? Error,
    string? TenantId = null);

public sealed record FleetReport(IReadOnlyList<FleetHost> Hosts);

public static class FleetService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static FleetReport Inspect(IEnumerable<string> databasePaths)
    {
        var hosts = new List<FleetHost>();
        foreach (var path in databasePaths
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            hosts.Add(InspectOne(path));
        }

        return new FleetReport(hosts.OrderBy(value => value.HostId ?? string.Empty, StringComparer.Ordinal).ToList());
    }

    public static string RenderTable(FleetReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("TENANT       HOST            SENSOR            EVENTS ALERTS GAPS COVERAGE ORDER     CLOCK     LAST_EVENT");
        foreach (var host in report.Hosts)
        {
            if (host.Error is not null)
            {
                builder.AppendLine($"{Short(host.Database),-15} error: {host.Error}");
                continue;
            }

            builder.AppendLine(
                $"{(host.TenantId ?? "-"),-12} " +
                $"{Short(host.HostName ?? host.HostId ?? "?"),-15} " +
                $"{Short(host.SensorId ?? "?"),-17} " +
                $"{host.EventCount,6} {host.AlertCount,6} {host.GapCount,4} " +
                $"{host.CoverageStatus,-8} {host.OrderStatus,-9} {host.ClockStatus,-9} " +
                $"{host.LastEventTime ?? "unknown"}");
        }

        builder.AppendLine("No-activity claims: false. Missing telemetry is reported as partial/unknown, not absence of activity.");
        return builder.ToString();
    }

    public static string RenderJson(FleetReport report) => JsonSerializer.Serialize(report, JsonOptions);

    private static FleetHost InspectOne(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"database not found: {path}");
            }

            using var store = new SqliteEventStore(path, readOnly: true);
            var enrollment = store.GetEnrollment();
            var activeConfig = store.GetActiveConfig();
            var tenantId = activeConfig is null
                ? null
                : FleetConfigService.Parse(activeConfig.DocumentJson).TenantId;
            var events = store.ReadEvents();
            var alerts = store.ReadAlerts();
            var gaps = store.ReadGaps();
            var bookmarks = store.ReadBookmarks();
            var bookmark = bookmarks.FirstOrDefault();
            var ordered = events.OrderBy(value => value.SensorSequence).ToList();
            var order = OrderStatus(ordered, enrollment);
            var coverage = CoverageStatus(ordered, gaps.Count, bookmark);
            var clock = enrollment?.ClockUncertain == true ? "uncertain" : "nominal";
            var missing = coverage != "complete";
            return new FleetHost(
                path,
                enrollment?.SensorId ?? ordered.FirstOrDefault()?.SensorId,
                enrollment?.HostId ?? ordered.FirstOrDefault()?.HostId,
                enrollment?.HostName,
                enrollment is null ? null : Timestamp(enrollment.EnrolledAt),
                enrollment?.ConfigGeneration ?? 0,
                enrollment?.ClockOffsetMs ?? 0,
                enrollment?.ClockUncertaintyMs ?? 0,
                events.Count,
                alerts.Count,
                gaps.Count,
                bookmark?.EventRecordId,
                bookmark?.CoverageStartRecordId,
                ordered.Count == 0 ? null : Timestamp(ordered.Min(value => value.Time)),
                ordered.Count == 0 ? null : Timestamp(ordered.Max(value => value.Time)),
                ordered.Count == 0 ? null : Timestamp(ordered.Max(value => value.ProcessedTime)),
                coverage,
                order,
                clock,
                missing,
                false,
                null,
                tenantId);
        }
        catch (Exception error)
        {
            return new FleetHost(
                path, null, null, null, null, 0, 0, 0, 0, 0, 0, null, null,
                null, null, null, "unknown", "unknown", "unknown", true, false, error.Message, null);
        }
    }

    private static string OrderStatus(IReadOnlyList<LocalEvent> ordered, Enrollment? enrollment)
    {
        if (ordered.Count == 0)
        {
            return "unknown";
        }

        for (var index = 1; index < ordered.Count; index++)
        {
            if (ordered[index].Time < ordered[index - 1].Time)
            {
                return "uncertain";
            }
        }

        return enrollment?.ClockUncertain == true ? "uncertain" : "ordered";
    }

    private static string CoverageStatus(
        IReadOnlyList<LocalEvent> ordered,
        int gapCount,
        Bookmark? bookmark)
    {
        if (ordered.Count == 0 || bookmark is null)
        {
            return "unknown";
        }

        if (gapCount > 0)
        {
            return "partial";
        }

        if (bookmark.CoverageStartRecordId is not null
            && ordered.Count > 0
            && bookmark.CoverageStartRecordId > ordered.Min(value => value.Source.EventRecordId))
        {
            return "partial";
        }

        return "complete";
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Short(string value) =>
        value.Length <= 15 ? value : value[..12] + "...";
}
