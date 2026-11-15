using Sentinel.Core.Sources;

namespace Sentinel.Core.Tests;

public class SourceStartChecksTests
{
    [Fact]
    public void Bookmark_mode_without_xml_is_rejected()
    {
        var start = new SourceStart(SourceStartMode.Bookmark, null, 100);

        Assert.Throws<SourceBookmarkException>(() => SourceStartChecks.Validate(start));
    }

    [Fact]
    public void Oldest_retained_start_needs_no_bookmark()
    {
        SourceStartChecks.Validate(new SourceStart(SourceStartMode.OldestRetained, null, null, 500));
    }

    [Fact]
    public void Record_below_the_bookmark_is_a_regression()
    {
        var start = new SourceStart(SourceStartMode.Bookmark, "<bookmark/>", 100);

        var error = Assert.Throws<SourceRecordRegressionException>(
            () => SourceStartChecks.EnsureNotRegressed(start, 99));
        Assert.Contains("100", error.Message, StringComparison.Ordinal);
        Assert.Contains("99", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_at_or_above_the_bookmark_is_accepted()
    {
        var start = new SourceStart(SourceStartMode.Bookmark, "<bookmark/>", 100);

        SourceStartChecks.EnsureNotRegressed(start, 100);
        SourceStartChecks.EnsureNotRegressed(start, 101);
    }

    [Fact]
    public void Snapshot_boundary_is_inclusive()
    {
        var start = new SourceStart(SourceStartMode.OldestRetained, null, null, 500);

        Assert.False(SourceStartChecks.IsBeyondSnapshot(start, 500));
        Assert.True(SourceStartChecks.IsBeyondSnapshot(start, 501));
    }

    [Fact]
    public void Unbounded_start_has_no_snapshot_limit()
    {
        var start = new SourceStart(SourceStartMode.OldestRetained, null, null);

        Assert.False(SourceStartChecks.IsBeyondSnapshot(start, long.MaxValue));
    }

    [Fact]
    public void Missing_expected_record_id_does_not_regress()
    {
        var start = new SourceStart(SourceStartMode.OldestRetained, null, null, 500);

        SourceStartChecks.EnsureNotRegressed(start, long.MinValue);
    }
}
