using Sentinel.Core.Model;
using Sentinel.Core.Rules;
using Sentinel.Core.Store;
using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Sources;

public sealed record AgentRunOptions(
    string SourceId,
    int InitialConfigGeneration = 1,
    int? MaxRecords = null);

public sealed record AgentRunReport(
    string SensorId,
    string HostId,
    ChannelConfig Configuration,
    IngestionReport Ingestion,
    Bookmark? Bookmark);

public sealed class AgentRunner
{
    private readonly IEventSource _source;
    private readonly SqliteEventStore _store;
    private readonly RuleSet _rules;
    private readonly string _sourceId;

    public AgentRunner(IEventSource source, SqliteEventStore store, RuleSet rules, string sourceId)
    {
        _source = source;
        _store = store;
        _rules = rules;
        _sourceId = sourceId;
    }

    public AgentRunReport Run(AgentRunOptions options, CancellationToken cancellationToken = default)
    {
        var (sensorId, hostId) = _store.EnsureIdentity(null, null);
        var bookmark = _store.GetBookmark(_sourceId);
        var latestIncarnation = _store.GetLatestIncarnation();
        if (bookmark is not null && _store.IsIncarnationRevoked(bookmark.IncarnationId))
        {
            throw new SourceUnavailableException(
                $"sensor incarnation '{bookmark.IncarnationId}' is revoked; refusing to resume.");
        }

        if (bookmark is null && latestIncarnation?.RevokedAt is not null)
        {
            throw new SourceUnavailableException(
                $"sensor incarnation '{latestIncarnation.IncarnationId}' is revoked; refusing to start.");
        }

        if (bookmark is not null && string.IsNullOrEmpty(bookmark.BookmarkXml))
        {
            InsertGap(
                bookmark.IncarnationId,
                GapKind.BookmarkMissing,
                CauseConfidence.Unknown,
                bookmark.EventRecordId,
                null,
                "The stored bookmark has no serialized Event Log bookmark; refusing to resume at now.");
            throw new SourceBookmarkException(
                "The stored bookmark has no serialized Event Log bookmark; refusing to resume at now.");
        }

        ChannelConfig configuration;
        long? snapshotRecordId;
        try
        {
            configuration = _source.ReadConfiguration();
            snapshotRecordId = _source.GetLastRecordId();
        }
        catch (SourceUnavailableException ex)
        {
            InsertGap(
                bookmark?.IncarnationId ?? "unknown",
                GapKind.Unknown,
                CauseConfidence.Unknown,
                bookmark?.EventRecordId,
                null,
                ex.Message);
            throw;
        }

        _store.SetSourceState(configuration);

        var incarnationId = bookmark?.IncarnationId
            ?? _store.GetActiveIncarnation()?.IncarnationId
            ?? Guid.NewGuid().ToString("D");
        var start = bookmark is null
            ? new SourceStart(SourceStartMode.OldestRetained, null, null, snapshotRecordId)
            : new SourceStart(SourceStartMode.Bookmark, bookmark.BookmarkXml, bookmark.EventRecordId, snapshotRecordId);

        var ingestion = new IngestionService(_store, sensorId, hostId);
        try
        {
            var report = ingestion.Ingest(
                _source.Read(start, options.MaxRecords, cancellationToken),
                _rules,
                _sourceId,
                incarnationId,
                bookmark?.ConfigGeneration ?? options.InitialConfigGeneration,
                groundTruth: null);

            return new AgentRunReport(
                sensorId,
                hostId,
                configuration,
                report,
                _store.GetBookmark(_sourceId));
        }
        catch (SourceBookmarkException ex)
        {
            InsertGap(
                incarnationId,
                GapKind.BookmarkMissing,
                CauseConfidence.Unknown,
                bookmark?.EventRecordId,
                null,
                ex.Message);
            throw;
        }
        catch (SourceRecordRegressionException ex)
        {
            InsertGap(
                incarnationId,
                GapKind.RecordIdRegression,
                CauseConfidence.Known,
                bookmark?.EventRecordId,
                null,
                ex.Message);
            throw;
        }
        catch (SourceUnavailableException ex)
        {
            InsertGap(
                incarnationId,
                GapKind.Unknown,
                CauseConfidence.Unknown,
                bookmark?.EventRecordId,
                null,
                ex.Message);
            throw;
        }
        catch (SourceRecordException ex)
        {
            InsertGap(
                incarnationId,
                GapKind.Unknown,
                CauseConfidence.Unknown,
                bookmark?.EventRecordId,
                null,
                ex.Message);
            throw;
        }
    }

    private void InsertGap(
        string incarnationId,
        GapKind kind,
        CauseConfidence confidence,
        long? startRecordId,
        long? endRecordId,
        string details)
    {
        _store.InsertGap(IngestionService.CreateGap(
            _sourceId,
            incarnationId,
            kind,
            confidence,
            startRecordId,
            endRecordId,
            details));
    }
}
