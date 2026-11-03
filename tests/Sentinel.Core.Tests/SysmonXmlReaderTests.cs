using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Tests;

public class SysmonXmlReaderTests
{
    [Fact]
    public void Parses_process_create_record()
    {
        var record = TestSupport.Fixture().Records.Single(value => value.EventRecordId == 1001);

        Assert.Equal(1, record.EventId);
        Assert.Equal(1001, record.EventRecordId);
        Assert.Equal(SysmonXmlReader.SysmonChannel, record.Channel);
        Assert.Equal("Microsoft-Windows-Sysmon", record.Provider);
        Assert.Equal("WS1", record.Computer);
        Assert.Equal("2026-11-02 10:00:00.100", record.Get("UtcTime"));
        Assert.Equal("{a1b2c3d4-1111-6111-0000-000000000001}", record.Get("ProcessGuid"));
        Assert.Equal(4242, record.GetInt("ProcessId"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T10:00:00.100Z"), record.TimeCreated);
    }

    [Fact]
    public void Parses_network_record()
    {
        var record = TestSupport.Fixture().Records.Single(value => value.EventRecordId == 1002);

        Assert.Equal(3, record.EventId);
        Assert.Equal("tcp", record.Get("Protocol"));
        Assert.Equal("8443", record.Get("DestinationPort"));
        Assert.Equal(true, record.GetBool("Initiated"));
    }

    [Fact]
    public void Malformed_xml_is_rejected()
    {
        Assert.Throws<SourceRecordException>(() => SysmonXmlReader.Parse("<Event"));
    }

    [Fact]
    public void Missing_channel_is_rejected()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name="Microsoft-Windows-Sysmon"/>
                <EventID>1</EventID>
                <TimeCreated SystemTime="2026-11-02T10:00:00.100Z"/>
                <EventRecordID>1</EventRecordID>
                <Computer>WS1</Computer>
              </System>
            </Event>
            """;

        Assert.Throws<SourceRecordException>(() => SysmonXmlReader.Parse(xml));
    }
}
