using Sentinel.Core.Model;
using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Sources;

public enum SourceStartMode
{
    OldestRetained,
    Bookmark
}

public sealed record SourceStart(
    SourceStartMode Mode,
    string? BookmarkXml,
    long? ExpectedEventRecordId,
    long? MaxEventRecordId = null);

public sealed record SourceDelivery(
    SysmonRecord Record,
    string? BookmarkXml);

public interface IEventSource : IDisposable
{
    ChannelConfig ReadConfiguration();

    /// <summary>
    /// Last record currently retained in the channel. Used to pin a bounded
    /// read to a snapshot instead of following the live tail.
    /// </summary>
    long? GetLastRecordId();

    IEnumerable<SourceDelivery> Read(
        SourceStart start,
        int? maxRecords,
        CancellationToken cancellationToken);
}

public sealed class SourceBookmarkException : Exception
{
    public SourceBookmarkException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed class SourceRecordRegressionException : Exception
{
    public SourceRecordRegressionException(string message)
        : base(message)
    {
    }
}

public sealed class SourceUnavailableException : Exception
{
    public SourceUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
