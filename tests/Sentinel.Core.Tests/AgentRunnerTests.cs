using Sentinel.Core.Model;
using Sentinel.Core.Rules;
using Sentinel.Core.Sources;
using Sentinel.Core.Store;
using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Tests;

public class AgentRunnerTests
{
    private const string Channel = SysmonXmlReader.SysmonChannel;

    [Fact]
    public void First_enrollment_starts_at_oldest_and_persists_bookmark_xml()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var source = new FakeSource
            {
                Deliveries =
                [
                    new SourceDelivery(ProcessRecord(1001), "bookmark-1001"),
                    new SourceDelivery(ProcessRecord(1002), "bookmark-1002")
                ]
            };
            var runner = new AgentRunner(source, store, new RuleSet([]), Channel);

            var report = runner.Run(new AgentRunOptions(Channel));

            Assert.Equal(2, report.Ingestion.Accepted);
            Assert.Equal(SourceStartMode.OldestRetained, source.LastStart!.Mode);
            Assert.Equal(1002, source.LastStart.MaxEventRecordId);
            var bookmark = store.GetBookmark(Channel);
            Assert.NotNull(bookmark);
            Assert.Equal(1002, bookmark!.EventRecordId);
            Assert.Equal(1001, bookmark.CoverageStartRecordId);
            Assert.Equal("bookmark-1002", bookmark.BookmarkXml);
            Assert.Equal(Channel, store.GetSourceState(Channel)?.SourceId);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Resume_uses_the_saved_bookmark()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var first = new FakeSource
            {
                Deliveries =
                [
                    new SourceDelivery(ProcessRecord(1001), "bookmark-1001"),
                    new SourceDelivery(ProcessRecord(1002), "bookmark-1002")
                ]
            };
            new AgentRunner(first, store, new RuleSet([]), Channel).Run(new AgentRunOptions(Channel));

            var second = new FakeSource
            {
                Deliveries = [new SourceDelivery(ProcessRecord(1003), "bookmark-1003")]
            };
            new AgentRunner(second, store, new RuleSet([]), Channel).Run(new AgentRunOptions(Channel));

            Assert.Equal(SourceStartMode.Bookmark, second.LastStart!.Mode);
            Assert.Equal("bookmark-1002", second.LastStart.BookmarkXml);
            Assert.Equal(1002, second.LastStart.ExpectedEventRecordId);
            Assert.Equal("bookmark-1003", store.GetBookmark(Channel)?.BookmarkXml);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Missing_bookmark_xml_creates_a_gap_and_refuses_to_resume()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var fixture = TestSupport.Fixture();
            var (sensorId, hostId) = store.EnsureIdentity(fixture.SensorId, fixture.HostId);
            var value = TestSupport.LocalEvent(fixture, 1001);
            store.Append(
                value,
                [],
                new BookmarkUpdate(Channel, fixture.IncarnationId, 1, 1001, 1001));

            var source = new FakeSource();
            var runner = new AgentRunner(source, store, new RuleSet([]), Channel);

            Assert.Throws<SourceBookmarkException>(() => runner.Run(new AgentRunOptions(Channel)));
            var gap = Assert.Single(store.ReadGaps());
            Assert.Equal(GapKind.BookmarkMissing, gap.Kind);
            Assert.Equal(CauseConfidence.Unknown, gap.Confidence);
            Assert.Null(source.LastStart);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Stale_bookmark_creates_a_gap()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var first = new FakeSource
            {
                Deliveries = [new SourceDelivery(ProcessRecord(1001), "bookmark-1001")]
            };
            new AgentRunner(first, store, new RuleSet([]), Channel).Run(new AgentRunOptions(Channel));

            var second = new FakeSource
            {
                ThrowOnRead = new SourceBookmarkException("stale bookmark")
            };
            var runner = new AgentRunner(second, store, new RuleSet([]), Channel);

            Assert.Throws<SourceBookmarkException>(() => runner.Run(new AgentRunOptions(Channel)));
            var gap = Assert.Single(store.ReadGaps());
            Assert.Equal(GapKind.BookmarkMissing, gap.Kind);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Record_id_regression_creates_a_gap()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var first = new FakeSource
            {
                Deliveries = [new SourceDelivery(ProcessRecord(1005), "bookmark-1005")]
            };
            new AgentRunner(first, store, new RuleSet([]), Channel).Run(new AgentRunOptions(Channel));

            var second = new FakeSource
            {
                ThrowOnRead = new SourceRecordRegressionException("regressed")
            };
            var runner = new AgentRunner(second, store, new RuleSet([]), Channel);

            Assert.Throws<SourceRecordRegressionException>(() => runner.Run(new AgentRunOptions(Channel)));
            var gap = Assert.Single(store.ReadGaps());
            Assert.Equal(GapKind.RecordIdRegression, gap.Kind);
            Assert.Equal(CauseConfidence.Known, gap.Confidence);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Configuration_change_event_advances_config_generation()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var source = new FakeSource
            {
                Deliveries =
                [
                    new SourceDelivery(ConfigRecord(1001), "bookmark-1001"),
                    new SourceDelivery(ProcessRecord(1002), "bookmark-1002")
                ]
            };
            var runner = new AgentRunner(source, store, new RuleSet([]), Channel);

            var report = runner.Run(new AgentRunOptions(Channel));

            Assert.Equal(1, report.Ingestion.SourceHealth);
            var events = store.ReadEvents();
            var configEvent = events.Single(value => value.Source.EventId == 16);
            var processEvent = events.Single(value => value.Source.EventId == 1);
            Assert.Equal(2, configEvent.Source.ConfigGeneration);
            Assert.Equal(2, processEvent.Source.ConfigGeneration);
            Assert.Equal(2, store.GetBookmark(Channel)?.ConfigGeneration);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Configuration_failure_creates_a_gap()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var source = new FakeSource
            {
                ThrowOnConfiguration = new SourceUnavailableException("access denied")
            };
            var runner = new AgentRunner(source, store, new RuleSet([]), Channel);

            Assert.Throws<SourceUnavailableException>(() => runner.Run(new AgentRunOptions(Channel)));
            var gap = Assert.Single(store.ReadGaps());
            Assert.Equal(GapKind.Unknown, gap.Kind);
            Assert.Equal(CauseConfidence.Unknown, gap.Confidence);
            Assert.Null(source.LastStart);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Source_unavailable_during_read_creates_a_gap()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var source = new FakeSource
            {
                ThrowOnRead = new SourceUnavailableException("channel disappeared")
            };
            var runner = new AgentRunner(source, store, new RuleSet([]), Channel);

            Assert.Throws<SourceUnavailableException>(() => runner.Run(new AgentRunOptions(Channel)));
            var gap = Assert.Single(store.ReadGaps());
            Assert.Equal(GapKind.Unknown, gap.Kind);
            Assert.Equal(CauseConfidence.Unknown, gap.Confidence);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    [Fact]
    public void Unparseable_record_creates_a_gap()
    {
        var path = TestSupport.NewDatabasePath();
        try
        {
            using var store = new SqliteEventStore(path);
            store.Initialize();
            var source = new FakeSource
            {
                ThrowOnRead = new SourceRecordException("malformed XML")
            };
            var runner = new AgentRunner(source, store, new RuleSet([]), Channel);

            Assert.Throws<SourceRecordException>(() => runner.Run(new AgentRunOptions(Channel)));
            var gap = Assert.Single(store.ReadGaps());
            Assert.Equal(GapKind.Unknown, gap.Kind);
            Assert.Equal(CauseConfidence.Unknown, gap.Confidence);
        }
        finally
        {
            TestSupport.DeleteDatabase(path);
        }
    }

    private static SysmonRecord ProcessRecord(long eventRecordId)
    {
        var fixture = TestSupport.Fixture();
        return fixture.Records.Single(value => value.EventRecordId == 1001)
            with { EventRecordId = eventRecordId };
    }

    private static SysmonRecord ConfigRecord(long eventRecordId) => new(
        16,
        eventRecordId,
        Channel,
        "Microsoft-Windows-Sysmon",
        "{5770385F-C22A-43E0-BF4C-06F5698FFBD9}",
        "WS1",
        DateTimeOffset.Parse("2026-11-02T10:00:00Z"),
        "<Event/>",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = "<Sysmon/>",
            ["ConfigurationFileHash"] = "test"
        });

    private sealed class FakeSource : IEventSource
    {
        public ChannelConfig Configuration { get; } = new(
            Channel,
            1024 * 1024 * 1024,
            "Circular",
            true,
            @"C:\Windows\System32\winevt\Logs\Microsoft-Windows-Sysmon%4Operational.evtx",
            DateTimeOffset.Parse("2026-11-02T10:00:00Z"));

        public List<SourceDelivery> Deliveries { get; init; } = [];

        public Exception? ThrowOnRead { get; init; }

        public Exception? ThrowOnConfiguration { get; init; }

        public SourceStart? LastStart { get; private set; }

        public ChannelConfig ReadConfiguration() =>
            ThrowOnConfiguration is null ? Configuration : throw ThrowOnConfiguration;

        public long? GetLastRecordId() =>
            Deliveries.Count == 0 ? null : Deliveries.Max(value => value.Record.EventRecordId);

        public IEnumerable<SourceDelivery> Read(
            SourceStart start,
            int? maxRecords,
            CancellationToken cancellationToken)
        {
            LastStart = start;
            if (ThrowOnRead is not null)
            {
                throw ThrowOnRead;
            }

            foreach (var delivery in Deliveries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return delivery;
            }
        }

        public void Dispose()
        {
        }
    }
}
