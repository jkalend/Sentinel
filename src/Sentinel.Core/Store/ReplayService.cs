using Sentinel.Core.Model;
using Sentinel.Core.Rules;

namespace Sentinel.Core.Store;

public sealed record ReplayReport(
    int LiveAlerts,
    int ReplayAlerts,
    IReadOnlyList<AlertCandidate> ReplayOnly,
    IReadOnlyList<StoredAlert> LiveOnly)
{
    public bool Matches => ReplayOnly.Count == 0 && LiveOnly.Count == 0;
}

public static class ReplayService
{
    public static ReplayReport Replay(SqliteEventStore store, RuleSet rules)
    {
        var live = store.ReadAlerts();
        var replay = store
            .ReadEvents()
            .SelectMany(value => RuleEvaluator.Evaluate(value, rules))
            .ToList();

        var liveKeys = live
            .Select(alert => Key(alert.RuleId, alert.RuleVersion, alert.SourceKey))
            .ToHashSet(StringComparer.Ordinal);
        var replayKeys = replay
            .Select(alert => Key(alert.RuleId, alert.RuleVersion, alert.SourceKey))
            .ToHashSet(StringComparer.Ordinal);

        var replayOnly = replay
            .Where(alert => !liveKeys.Contains(Key(alert.RuleId, alert.RuleVersion, alert.SourceKey)))
            .ToList();
        var liveOnly = live
            .Where(alert => !replayKeys.Contains(Key(alert.RuleId, alert.RuleVersion, alert.SourceKey)))
            .ToList();

        return new ReplayReport(live.Count, replay.Count, replayOnly, liveOnly);
    }

    private static string Key(string ruleId, string ruleVersion, string sourceKey) =>
        $"{ruleId}@{ruleVersion}|{sourceKey}";
}
