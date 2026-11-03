namespace Sentinel.Core.Model;

public enum RecordKind
{
    Data,
    SourceHealth
}

public enum NetworkOutcome
{
    Unknown,
    EstablishedOutbound,
    InboundListener,
    Refused,
    NoEvent
}

public enum GapKind
{
    ChannelCleared,
    RecordIdRegression,
    BookmarkMissing,
    MissingRecordsReported,
    ReaderDowntime,
    StorePaused,
    RetentionExpired,
    SourceFilterUnknown,
    Unknown
}

public enum CauseConfidence
{
    Known,
    Unknown
}

public sealed record UnmappedField(string SourceField, string Reason);

public sealed record SourceIdentity(
    string Channel,
    string Provider,
    string? ProviderGuid,
    string IncarnationId,
    int ConfigGeneration,
    int EventId,
    long EventRecordId)
{
    public string SourceKey(string sensorId) =>
        $"{sensorId}|{Channel}|{IncarnationId}|{EventRecordId}";
}

public sealed record ProcessContext(
    string? Guid,
    int? Pid,
    string? Name,
    string? CommandLine,
    string? ImagePath,
    string? ParentGuid,
    int? ParentPid,
    string? ParentName,
    string? ParentCommandLine,
    string? User);

public sealed record NetworkContext(
    string? Protocol,
    bool? Initiated,
    string? SourceIp,
    int? SourcePort,
    string? SourceHostname,
    string? DestinationIp,
    int? DestinationPort,
    string? DestinationHostname,
    NetworkOutcome Outcome);

public sealed record LocalEvent(
    string ContractVersion,
    string SensorId,
    string HostId,
    SourceIdentity Source,
    long SensorSequence,
    DateTimeOffset Time,
    DateTimeOffset? LoggedTime,
    DateTimeOffset ProcessedTime,
    string ClockStatus,
    RecordKind Kind,
    string? OcsfJson,
    int? OcsfClassUid,
    int? OcsfActivityId,
    IReadOnlyList<UnmappedField> Unmapped,
    string RawXml,
    string RawSha256,
    string? GapId,
    ProcessContext? Process,
    NetworkContext? Network,
    string? HostName,
    string? UserName)
{
    public string SourceKey => Source.SourceKey(SensorId);
}

public sealed record CoverageGap(
    string GapId,
    string SourceId,
    string IncarnationId,
    GapKind Kind,
    CauseConfidence Confidence,
    long? StartRecordId,
    long? EndRecordId,
    DateTimeOffset NotedAt,
    string Details);

public sealed record AlertCandidate(
    string AlertId,
    string RuleId,
    string RuleVersion,
    string EngineVersion,
    string SourceKey,
    DateTimeOffset CreatedAt,
    string DetailsJson);

public sealed record StoredAlert(
    string AlertId,
    string RuleId,
    string RuleVersion,
    string EngineVersion,
    string SourceKey,
    long SensorSequence,
    DateTimeOffset CreatedAt,
    string DetailsJson);

public sealed record Bookmark(
    string SourceId,
    string IncarnationId,
    int ConfigGeneration,
    long EventRecordId,
    long? CoverageStartRecordId,
    long SensorSequence,
    DateTimeOffset UpdatedAt,
    string? BookmarkXml = null);

public sealed record BookmarkUpdate(
    string SourceId,
    string IncarnationId,
    int ConfigGeneration,
    long EventRecordId,
    long? CoverageStartRecordId,
    string? BookmarkXml = null);

public sealed record ChannelConfig(
    string SourceId,
    long MaximumSizeInBytes,
    string LogMode,
    bool IsEnabled,
    string? LogFilePath,
    DateTimeOffset CapturedAt);

public enum AppendStatus
{
    Accepted,
    Duplicate
}

public sealed record AppendResult(AppendStatus Status, long SensorSequence);

public static class SentinelVersions
{
    public const string ContractVersion = "sentinel.local-event/v1";
    public const string OcsfVersion = "1.9.0";
    public const string EngineVersion = "sentinel.engine/0.1.0";
    public const string MapperVersion = "sentinel.sysmon-mapper/0.1.0";
}
