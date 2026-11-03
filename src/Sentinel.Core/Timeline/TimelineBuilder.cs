using System.Globalization;
using System.Text;
using Sentinel.Core.Model;

namespace Sentinel.Core.Timeline;

public static class TimelineBuilder
{
    public static string Render(IReadOnlyList<LocalEvent> events)
    {
        var processIndex = events
            .Where(e => e.Kind == RecordKind.Data
                && e.OcsfClassUid == Ocsf.OcsfMapper.ProcessActivityClassUid
                && e.Process?.Guid is not null)
            .GroupBy(e => (e.HostId, e.Process!.Guid!))
            .ToDictionary(g => g.Key, g => g.First());

        var builder = new StringBuilder();
        builder.AppendLine("SEQ  EVENT_TIME(UTC)          KIND          SUMMARY");
        foreach (var value in events.OrderBy(e => e.SensorSequence))
        {
            var time = value.Time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var summary = value.Kind switch
            {
                RecordKind.SourceHealth => $"source-health: EventID {value.Source.EventId}; raw preserved",
                _ => value.OcsfClassUid switch
                {
                    Ocsf.OcsfMapper.ProcessActivityClassUid => ProcessSummary(value, processIndex),
                    Ocsf.OcsfMapper.NetworkActivityClassUid => NetworkSummary(value, processIndex),
                    _ => $"unclassified data event: class {value.OcsfClassUid?.ToString(CultureInfo.InvariantCulture) ?? "none"}"
                }
            };

            builder.AppendLine($"{value.SensorSequence,4} {time}  {value.Kind,-13} {summary}");
        }

        return builder.ToString();
    }

    private static string ProcessSummary(
        LocalEvent value,
        IReadOnlyDictionary<(string HostId, string Guid), LocalEvent> processIndex)
    {
        var process = value.Process;
        if (process is null)
        {
            return "process: [no process context]";
        }

        var text = new StringBuilder();
        text.Append($"{process.Name ?? "[unknown]"} pid={Format(process.Pid)} guid={process.Guid ?? "[missing]"}");
        if (process.CommandLine is not null)
        {
            text.Append($" cmd={Truncate(process.CommandLine, 80)}");
        }

        var ancestry = Ancestry(value, processIndex);
        if (ancestry.Count > 0)
        {
            text.Append(" | ancestry: ");
            text.Append(string.Join(" <- ", ancestry));
        }

        return text.ToString();
    }

    private static string NetworkSummary(
        LocalEvent value,
        IReadOnlyDictionary<(string HostId, string Guid), LocalEvent> processIndex)
    {
        var network = value.Network;
        if (network is null)
        {
            return "network: [no network context]";
        }

        var processName = value.Process?.Name ?? "[process not observed]";
        var guid = value.Process?.Guid;
        var procText = guid is null ? processName : $"{processName} guid={guid}";
        if (guid is not null && !processIndex.ContainsKey((value.HostId, guid)))
        {
            procText += " [process event not observed]";
        }

        var activity = value.OcsfActivityId == Ocsf.OcsfMapper.NetworkOpenActivityId
            ? "Open"
            : "Unknown";
        return $"{procText} {Endpoint(network.SourceIp, network.SourcePort)} -> {Endpoint(network.DestinationIp, network.DestinationPort)} [{activity}]";
    }

    private static IReadOnlyList<string> Ancestry(
        LocalEvent value,
        IReadOnlyDictionary<(string HostId, string Guid), LocalEvent> processIndex)
    {
        var chain = new List<string>();
        var guid = value.Process?.ParentGuid;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; guid is not null && depth < 16 && seen.Add(guid); depth++)
        {
            if (!processIndex.TryGetValue((value.HostId, guid), out var parent))
            {
                chain.Add($"[parent not observed: {guid}]");
                break;
            }

            chain.Add($"{parent.Process?.Name ?? "[unknown]"} pid={Format(parent.Process?.Pid)} guid={guid}");
            guid = parent.Process?.ParentGuid;
        }

        return chain;
    }

    private static string Endpoint(string? ip, int? port)
    {
        var address = ip ?? "[unknown]";
        return port.HasValue ? $"{address}:{port.Value.ToString(CultureInfo.InvariantCulture)}" : address;
    }

    private static string Format(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "[unknown]";

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";
}
