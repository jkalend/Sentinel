namespace Sentinel.Core.Sources;

/// <summary>
/// Resume and record-window rules shared by every event source. Keeping them
/// here means a new source cannot silently reimplement or skip bookmark
/// regression and snapshot-bound behavior.
/// </summary>
public static class SourceStartChecks
{
    public static void Validate(SourceStart start)
    {
        if (start.Mode == SourceStartMode.Bookmark && string.IsNullOrEmpty(start.BookmarkXml))
        {
            throw new SourceBookmarkException("Bookmark resume requested without bookmark XML.");
        }
    }

    /// <summary>Stopping at the snapshot keeps a bounded read from following the live tail.</summary>
    public static bool IsBeyondSnapshot(SourceStart start, long eventRecordId) =>
        start.MaxEventRecordId is long snapshot && eventRecordId > snapshot;

    public static void EnsureNotRegressed(SourceStart start, long eventRecordId)
    {
        if (start.ExpectedEventRecordId is long expected && eventRecordId < expected)
        {
            throw new SourceRecordRegressionException(
                $"EventRecordID regressed from bookmark {expected} to {eventRecordId}.");
        }
    }
}
