using System.Security.Cryptography;
using System.Text;
using Sentinel.Core.Model;
using Sentinel.Core.Ocsf;
using Sentinel.Core.Rules;
using Sentinel.Core.Sources;
using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Store;

public sealed record IngestionReport(
    int Accepted,
    int Duplicates,
    int SourceHealth,
    int Alerts,
    int Gaps,
    long? FirstSequence,
    long? LastSequence);

public sealed class IngestionService
{
    private readonly SqliteEventStore _store;
    private readonly string _sensorId;
    private readonly string _hostId;

    public IngestionService(SqliteEventStore store, string sensorId, string hostId)
    {
        _store = store;
        _sensorId = sensorId;
        _hostId = hostId;
    }

    public IngestionReport Ingest(
        IEnumerable<SysmonRecord> records,
        RuleSet rules,
        string sourceId,
        string incarnationId,
        int configGeneration,
        IReadOnlyDictionary<long, NetworkOutcome>? groundTruth = null) =>
        Ingest(
            records.Select(record => new SourceDelivery(record, null)),
            rules,
            sourceId,
            incarnationId,
            configGeneration,
            groundTruth);

    public IngestionReport Ingest(
        IEnumerable<SourceDelivery> deliveries,
        RuleSet rules,
        string sourceId,
        string incarnationId,
        int configGeneration,
        IReadOnlyDictionary<long, NetworkOutcome>? groundTruth = null)
    {
        var bookmark = _store.GetBookmark(sourceId);
        var coverageStart = bookmark?.CoverageStartRecordId;
        var generation = bookmark?.ConfigGeneration ?? configGeneration;
        var accepted = 0;
        var duplicates = 0;
        var sourceHealth = 0;
        var alerts = 0;
        var gaps = 0;
        long? firstSequence = null;
        long? lastSequence = null;
        groundTruth ??= new Dictionary<long, NetworkOutcome>();

        foreach (var delivery in deliveries)
        {
            var record = delivery.Record;
            groundTruth.TryGetValue(record.EventRecordId, out var outcome);

            var advances = bookmark is null || record.EventRecordId > bookmark.EventRecordId;
            var recordGeneration = record.EventId == 16 && advances
                ? generation + 1
                : generation;

            var processed = DateTimeOffset.UtcNow;
            var mapped = OcsfMapper.Map(
                record,
                new MappingContext(_sensorId, _hostId, incarnationId, processed),
                outcome);

            CoverageGap? gapBefore = null;
            if (bookmark is not null
                && string.Equals(bookmark.IncarnationId, incarnationId, StringComparison.OrdinalIgnoreCase)
                && record.EventRecordId > bookmark.EventRecordId + 1)
            {
                var start = bookmark.EventRecordId + 1;
                var end = record.EventRecordId - 1;
                gapBefore = CreateGap(
                    sourceId,
                    incarnationId,
                    GapKind.MissingRecordsReported,
                    CauseConfidence.Known,
                    start,
                    end,
                    $"EventRecordID jumped from {bookmark.EventRecordId} to {record.EventRecordId}.");
            }

            var local = new LocalEvent(
                SentinelVersions.ContractVersion,
                _sensorId,
                _hostId,
                new SourceIdentity(
                    record.Channel,
                    record.Provider,
                    record.ProviderGuid,
                    incarnationId,
                    recordGeneration,
                    record.EventId,
                    record.EventRecordId),
                0,
                mapped.Time,
                record.TimeCreated,
                processed,
                "wall_utc",
                mapped.Kind,
                mapped.OcsfJson,
                mapped.ClassUid,
                mapped.ActivityId,
                mapped.Unmapped,
                record.RawXml,
                Sha256Hex(record.RawXml),
                gapBefore?.GapId,
                mapped.Process,
                mapped.Network,
                mapped.HostName,
                mapped.UserName);

            var eventAlerts = RuleEvaluator.Evaluate(local, rules);

            BookmarkUpdate? nextBookmark = null;
            if (bookmark is null || record.EventRecordId > bookmark.EventRecordId)
            {
                nextBookmark = new BookmarkUpdate(
                    sourceId,
                    incarnationId,
                    recordGeneration,
                    record.EventRecordId,
                    coverageStart ?? record.EventRecordId,
                    delivery.BookmarkXml);
            }
            else if (coverageStart is null || record.EventRecordId < coverageStart)
            {
                nextBookmark = new BookmarkUpdate(
                    sourceId,
                    incarnationId,
                    generation,
                    bookmark.EventRecordId,
                    record.EventRecordId,
                    null);
            }

            var result = _store.Append(local, eventAlerts, nextBookmark, gapBefore);
            if (result.Status == AppendStatus.Duplicate)
            {
                duplicates++;
                continue;
            }

            accepted++;
            alerts += eventAlerts.Count;
            if (mapped.Kind == RecordKind.SourceHealth)
            {
                sourceHealth++;
            }

            if (gapBefore is not null)
            {
                gaps++;
            }

            firstSequence ??= result.SensorSequence;
            lastSequence = result.SensorSequence;

            coverageStart = coverageStart is null
                ? record.EventRecordId
                : Math.Min(coverageStart.Value, record.EventRecordId);
            generation = recordGeneration;
            bookmark = new Bookmark(
                sourceId,
                incarnationId,
                generation,
                Math.Max(bookmark?.EventRecordId ?? 0, record.EventRecordId),
                coverageStart,
                result.SensorSequence,
                DateTimeOffset.UtcNow,
                nextBookmark?.BookmarkXml ?? bookmark?.BookmarkXml);
        }

        return new IngestionReport(accepted, duplicates, sourceHealth, alerts, gaps, firstSequence, lastSequence);
    }

    public static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static CoverageGap CreateGap(
        string sourceId,
        string incarnationId,
        GapKind kind,
        CauseConfidence confidence,
        long? startRecordId,
        long? endRecordId,
        string details)
    {
        var identity = $"{sourceId}|{incarnationId}|{kind}|{startRecordId}|{endRecordId}|{details}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new CoverageGap(
            Convert.ToHexString(hash).ToLowerInvariant(),
            sourceId,
            incarnationId,
            kind,
            confidence,
            startRecordId,
            endRecordId,
            DateTimeOffset.UtcNow,
            details);
    }
}
