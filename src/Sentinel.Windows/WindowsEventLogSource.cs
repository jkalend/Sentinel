using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using Sentinel.Core.Model;
using Sentinel.Core.Sources;
using Sentinel.Core.Sysmon;

namespace Sentinel.Windows;

/// <summary>
/// Reads a Windows Event Log channel in forward order. First enrollment starts
/// at the oldest retained record; resumes use the serialized Event Log bookmark.
/// This adapter is compiled on Windows but is not exercised without a live
/// Sysmon channel.
/// </summary>
public sealed class WindowsEventLogSource : IEventSource
{
    private readonly string _channel;

    public WindowsEventLogSource(string channel = SysmonXmlReader.SysmonChannel)
    {
        _channel = channel;
    }

    public ChannelConfig ReadConfiguration()
    {
        try
        {
            using var configuration = new EventLogConfiguration(_channel);
            return new ChannelConfig(
                _channel,
                configuration.MaximumSizeInBytes,
                configuration.LogMode.ToString(),
                configuration.IsEnabled,
                configuration.LogFilePath,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is EventLogException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new SourceUnavailableException(
                $"Cannot read configuration for channel '{_channel}': {ex.Message}",
                ex);
        }
    }

    public long? GetLastRecordId()
    {
        try
        {
            var query = new EventLogQuery(_channel, PathType.LogName)
            {
                ReverseDirection = true
            };
            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent();
            return record?.RecordId;
        }
        catch (Exception ex) when (ex is EventLogException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new SourceUnavailableException(
                $"Cannot read the last record of channel '{_channel}': {ex.Message}",
                ex);
        }
    }

    public IEnumerable<SourceDelivery> Read(
        SourceStart start,
        int? maxRecords,
        CancellationToken cancellationToken)
    {
        EventLogReader reader;
        try
        {
            reader = new EventLogReader(new EventLogQuery(_channel, PathType.LogName));
        }
        catch (Exception ex) when (ex is EventLogException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new SourceUnavailableException(
                $"Cannot open channel '{_channel}': {ex.Message}",
                ex);
        }

        using (reader)
        {
            if (start.Mode == SourceStartMode.Bookmark)
            {
                if (string.IsNullOrEmpty(start.BookmarkXml))
                {
                    throw new SourceBookmarkException("Bookmark resume requested without bookmark XML.");
                }

                try
                {
                    reader.Seek(new EventBookmark(start.BookmarkXml), 1);
                }
                catch (Exception ex) when (ex is EventLogException or ArgumentException or InvalidOperationException)
                {
                    throw new SourceBookmarkException(
                        $"The stored bookmark cannot be used for channel '{_channel}': {ex.Message}",
                        ex);
                }
            }

            var count = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (maxRecords.HasValue && count >= maxRecords.Value)
                {
                    yield break;
                }

                EventRecord? record;
                try
                {
                    record = reader.ReadEvent();
                }
                catch (Exception ex) when (ex is EventLogException or InvalidOperationException)
                {
                    throw new SourceUnavailableException(
                        $"Reading channel '{_channel}' failed: {ex.Message}",
                        ex);
                }

                if (record is null)
                {
                    yield break;
                }

                using (record)
                {
                    string xml;
                    try
                    {
                        xml = record.ToXml();
                    }
                    catch (Exception ex) when (ex is EventLogException or InvalidOperationException)
                    {
                        throw new SourceUnavailableException(
                            $"Event {record.RecordId} could not be rendered as XML: {ex.Message}",
                            ex);
                    }

                    var parsed = SysmonXmlReader.Parse(xml);
                    if (start.MaxEventRecordId is long snapshot
                        && parsed.EventRecordId > snapshot)
                    {
                        yield break;
                    }

                    if (start.ExpectedEventRecordId is long expected
                        && parsed.EventRecordId < expected)
                    {
                        throw new SourceRecordRegressionException(
                            $"EventRecordID regressed from bookmark {expected} to {parsed.EventRecordId}.");
                    }

                    var bookmarkXml = record.Bookmark?.BookmarkXml;
                    if (string.IsNullOrEmpty(bookmarkXml))
                    {
                        throw new SourceBookmarkException(
                            $"Event {parsed.EventRecordId} has no serialized Event Log bookmark.");
                    }

                    yield return new SourceDelivery(parsed, bookmarkXml);
                }

                count++;
            }
        }
    }

    public void Dispose()
    {
    }
}
