using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Tests;

public class LiveCaptureFixtureTests
{
    private static IReadOnlyDictionary<string, SysmonRecord> Records()
    {
        var directory = Path.Combine(TestSupport.Root, "fixtures", "sysmon-v01-live", "events");
        return Directory.EnumerateFiles(directory, "*.xml")
            .ToDictionary(path => Path.GetFileName(path), path => SysmonXmlReader.Parse(File.ReadAllText(path)), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parses_real_capture_records_and_preserves_source_context()
    {
        var records = Records();

        Assert.Equal(6, records.Count);
        Assert.All(records.Values, record =>
        {
            Assert.Equal(SysmonXmlReader.SysmonChannel, record.Channel);
            Assert.Equal("SENTINEL-WIN11", record.Computer);
            Assert.False(string.IsNullOrWhiteSpace(record.RawXml));
            Assert.NotEqual(default, record.TimeCreated);
        });

        var create = records["process-create.xml"];
        Assert.Equal(1, create.EventId);
        Assert.Equal(5124, create.GetInt("ProcessId"));
        Assert.Equal("{77e309eb-34a9-6ae9-de00-000000000400}", create.Get("ProcessGuid"));

        var outbound = records["network-outbound.xml"];
        Assert.Equal(3, outbound.EventId);
        Assert.True(outbound.GetBool("Initiated"));
        Assert.Equal("tcp", outbound.Get("Protocol"));

        var inbound = records["network-inbound.xml"];
        Assert.Equal(3, inbound.EventId);
        Assert.False(inbound.GetBool("Initiated"));

        Assert.Equal(5, records["process-terminate.xml"].EventId);
        Assert.Equal(16, records["config-change.xml"].EventId);
        Assert.Equal(255, records["sysmon-error.xml"].EventId);
    }
}
