using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sentinel.Core.Store;

public sealed record RetentionPolicy(
    int EventDays = 30,
    int RawDays = 7,
    int AlertDays = 90,
    int SourceHealthDays = 90,
    int GapDays = 90,
    int KeepEvents = 1000,
    long MaxBytes = 10L * 1024 * 1024 * 1024)
{
    public RetentionPolicy Validate()
    {
        if (EventDays < 0 || RawDays < 0 || AlertDays < 0 || SourceHealthDays < 0 || GapDays < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RetentionPolicy), "Retention ages cannot be negative.");
        }

        if (KeepEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(KeepEvents), "At least one newest event must be retained.");
        }

        if (MaxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBytes), "Retention byte cap cannot be negative.");
        }

        return this;
    }

    public string CanonicalJson() => JsonSerializer.Serialize(this);

    public string Sha256() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson()))).ToLowerInvariant();
}

public sealed record PurgedRange(
    string PurgeId,
    long FirstSequence,
    long LastSequence,
    long EventCount,
    DateTimeOffset FirstTime,
    DateTimeOffset LastTime);

public sealed record RetentionAudit(
    string PurgeId,
    DateTimeOffset CreatedAt,
    string PolicyJson,
    string PolicySha256,
    long DataEventsDeleted,
    long SourceHealthDeleted,
    long RawXmlPurged,
    long AlertsDeleted,
    long GapsDeleted,
    PurgedRange? Range);

public sealed record RetentionReport(
    string PurgeId,
    bool Applied,
    DateTimeOffset Now,
    DateTimeOffset EventCutoff,
    DateTimeOffset RawCutoff,
    DateTimeOffset AlertCutoff,
    DateTimeOffset SourceHealthCutoff,
    DateTimeOffset GapCutoff,
    long KeepFromSequence,
    StoreCounts Before,
    StoreCounts After,
    long DataEventsDeleted,
    long SourceHealthDeleted,
    long RawXmlPurged,
    long AlertsDeleted,
    long GapsDeleted,
    long RetainedBytes,
    bool OverLimit,
    PurgedRange? Range);

public sealed record BackupManifest(
    string Format,
    string DatabaseSha256,
    long DatabaseBytes,
    string ContractVersion,
    string OcsfVersion,
    string EngineVersion,
    StoreCounts Counts,
    string? BookmarkSource,
    string? BookmarkIncarnation,
    long? BookmarkRecordId,
    long? BookmarkCoverageStart,
    long? BookmarkSensorSequence,
    DateTimeOffset CreatedAt,
    string? SigningPublicKeyPem = null,
    string? Signature = null);

public sealed record BackupReport(string BackupPath, string ManifestPath, BackupManifest Manifest);

public sealed record RestoreReport(string BackupPath, string DatabasePath, BackupManifest Manifest);

internal static class RetentionClock
{
    public static string Text(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
