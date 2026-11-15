using Sentinel.Core.Model;

namespace Sentinel.Core.Transport;

public sealed record TransportBatch(
    string Format,
    string BatchId,
    string CreatedAt,
    string SensorId,
    string HostId,
    string IncarnationId,
    string Channel,
    int ConfigGeneration,
    long ClockOffsetMs,
    long ClockUncertaintyMs,
    long FirstSensorSequence,
    long LastSensorSequence,
    BookmarkUpdate? Bookmark,
    IReadOnlyList<LocalEvent> Events,
    IReadOnlyList<CoverageGap> Gaps,
    string PayloadSha256);

public sealed record TransportAck(
    string Format,
    string BatchId,
    int Accepted,
    int Duplicates,
    int Gaps,
    long LastSensorSequence,
    string Status);

public sealed record PushResult(
    int Batches,
    int Accepted,
    int Duplicates,
    long LastAckedSequence);

public sealed class TransportException : Exception
{
    public TransportException(string message)
        : base(message)
    {
    }

    public TransportException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
