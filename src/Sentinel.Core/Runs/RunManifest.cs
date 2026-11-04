using System.Text.Json.Serialization;

namespace Sentinel.Core.Runs;

public sealed record RunRulePin(string Id, string Version, string Sha256);

public sealed record RunIdentity(
    string Engine,
    string Contract,
    string Ocsf,
    string Mapper,
    string SchemaSha256,
    string RulesSha256,
    IReadOnlyList<RunRulePin> Rules);

public sealed record RunCoverage(
    int SourceCount,
    int EventCount,
    int AlertCount,
    int GapCount,
    long? FirstSensorSequence,
    long? LastSensorSequence,
    string? FirstTimeUtc,
    string? LastTimeUtc);

public sealed record RunSourcePin(
    string SensorId,
    string HostId,
    string Channel,
    string IncarnationId,
    int ConfigGeneration,
    long? BookmarkRecordId,
    long? CoverageStartRecordId,
    long? BookmarkSensorSequence,
    int EventCount,
    long FirstSensorSequence,
    long LastSensorSequence);

public sealed record RunEventPin(
    string SourceKey,
    long SensorSequence,
    string RecordKind,
    long EventRecordId,
    string TimeUtc,
    string? LoggedTimeUtc,
    string ClockStatus,
    int? OcsfClassUid,
    int? OcsfActivityId,
    string RawSha256,
    string? OcsfSha256,
    string? RawPurgedAt,
    bool PayloadIncluded,
    string? PayloadSha256);

public sealed record RunAlertPin(
    string AlertId,
    string RuleId,
    string RuleVersion,
    string EngineVersion,
    string SourceKey,
    long SensorSequence,
    string CreatedAt,
    string DetailsSha256);

public sealed record RunGapPin(
    string GapId,
    string SourceId,
    string IncarnationId,
    string Kind,
    string Confidence,
    long? StartRecordId,
    long? EndRecordId,
    string NotedAt,
    string DetailsSha256);

public sealed record RunExport(
    string Mode,
    bool PayloadIncluded,
    string? PayloadFile,
    string? PayloadSha256);

public sealed record RunContent(
    RunIdentity Identity,
    RunCoverage Coverage,
    IReadOnlyList<RunSourcePin> Sources,
    IReadOnlyList<RunEventPin> Events,
    IReadOnlyList<RunAlertPin> Alerts,
    IReadOnlyList<RunGapPin> Gaps,
    RunExport Export);

public sealed record RunManifest(
    string Format,
    string RunId,
    string CreatedAt,
    RunIdentity Identity,
    RunCoverage Coverage,
    IReadOnlyList<RunSourcePin> Sources,
    IReadOnlyList<RunEventPin> Events,
    IReadOnlyList<RunAlertPin> Alerts,
    IReadOnlyList<RunGapPin> Gaps,
    RunExport Export,
    string ContentSha256,
    string ManifestSha256);

public sealed record RunPayload(
    string SourceKey,
    long SensorSequence,
    string RecordKind,
    string TimeUtc,
    string? LoggedTimeUtc,
    string ClockStatus,
    string? RawXml,
    string? OcsfJson,
    string? UnmappedJson,
    string? RawPurgedAt);

public sealed record RunVerification(
    bool ManifestValid,
    bool ContentValid,
    bool PayloadValid,
    int Included,
    int Missing,
    int Changed,
    int Redacted,
    IReadOnlyList<string> Messages)
{
    public bool Ok => ManifestValid && ContentValid && PayloadValid && Missing == 0 && Changed == 0;
}
