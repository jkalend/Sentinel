using System.Text.Json.Nodes;
using Sentinel.Core.Model;
using Sentinel.Core.Ocsf;

namespace Sentinel.Core.Tests;

public class OcsfMapperTests
{
    private static readonly MappingContext Context = new(
        "sensor-test",
        "host-test",
        "incarnation-test",
        DateTimeOffset.Parse("2026-11-02T11:00:00Z"));

    private static JsonObject Map(Fixture.FixtureSet fixture, long recordId, NetworkOutcome? outcome = null)
    {
        var record = fixture.Records.Single(value => value.EventRecordId == recordId);
        var resolved = outcome
            ?? (fixture.GroundTruth.TryGetValue(recordId, out var value) ? value : NetworkOutcome.Unknown);
        var mapped = OcsfMapper.Map(record, Context, resolved);
        Assert.NotNull(mapped.OcsfJson);
        return JsonNode.Parse(mapped.OcsfJson!)!.AsObject();
    }

    [Fact]
    public void Maps_process_launch_fields()
    {
        var fixture = TestSupport.Fixture();
        var record = fixture.Records.Single(value => value.EventRecordId == 1001);
        var mapped = OcsfMapper.Map(record, Context, NetworkOutcome.Unknown);
        var ocsf = JsonNode.Parse(mapped.OcsfJson!)!.AsObject();

        Assert.Equal(RecordKind.Data, mapped.Kind);
        Assert.Equal(1007, ocsf["class_uid"]!.GetValue<int>());
        Assert.Equal(1, ocsf["activity_id"]!.GetValue<int>());
        Assert.Equal(100701, ocsf["type_uid"]!.GetValue<int>());
        Assert.Equal("Process Activity: Launch", ocsf["type_name"]!.GetValue<string>());
        Assert.Equal("1.9.0", ocsf["metadata"]!["version"]!.GetValue<string>());
        Assert.Equal(
            "sensor-test|Microsoft-Windows-Sysmon/Operational|incarnation-test|1001",
            ocsf["metadata"]!["uid"]!.GetValue<string>());
        Assert.Equal("WS1", ocsf["device"]!["hostname"]!.GetValue<string>());
        Assert.Equal("{a1b2c3d4-1111-6111-0000-000000000001}", ocsf["process"]!["uid"]!.GetValue<string>());
        Assert.Equal(4242, ocsf["process"]!["pid"]!.GetValue<int>());
        Assert.Equal("powershell.exe", ocsf["process"]!["name"]!.GetValue<string>());
        Assert.Equal(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            ocsf["process"]!["file"]!["path"]!.GetValue<string>());
        Assert.Equal(4, ocsf["process"]!["file"]!["hashes"]!.AsArray().Count);
        Assert.Equal(
            "{a1b2c3d4-1111-6111-0000-000000000002}",
            ocsf["actor"]!["process"]!["uid"]!.GetValue<string>());
        Assert.Contains(mapped.Unmapped, item => item.SourceField == "RuleName");

        var validator = new OcsfSchemaValidator(TestSupport.SchemaPath);
        Assert.Empty(validator.Validate(ocsf, "events/system/process_activity.json"));
    }

    [Fact]
    public void Maps_established_outbound_tcp_as_open()
    {
        var ocsf = Map(TestSupport.Fixture(), 1002);

        Assert.Equal(1, ocsf["activity_id"]!.GetValue<int>());
        Assert.Equal(1, ocsf["initiator_id"]!.GetValue<int>());
        Assert.Equal(2, ocsf["connection_info"]!["direction_id"]!.GetValue<int>());
        Assert.Equal(8443, ocsf["dst_endpoint"]!["port"]!.GetValue<int>());
        Assert.Equal("127.0.0.1", ocsf["dst_endpoint"]!["ip"]!.GetValue<string>());
        Assert.Equal(
            "{a1b2c3d4-1111-6111-0000-000000000001}",
            ocsf["actor"]!["process"]!["uid"]!.GetValue<string>());

        var validator = new OcsfSchemaValidator(TestSupport.SchemaPath);
        Assert.Empty(validator.Validate(ocsf, "events/network/network_activity.json"));
    }

    [Fact]
    public void Inbound_listener_stays_unknown()
    {
        var fixture = TestSupport.Fixture();
        var mapped = OcsfMapper.Map(
            fixture.Records.Single(value => value.EventRecordId == 1006),
            Context,
            NetworkOutcome.InboundListener);
        var ocsf = JsonNode.Parse(mapped.OcsfJson!)!.AsObject();

        Assert.Equal(0, ocsf["activity_id"]!.GetValue<int>());
        Assert.Equal(400100, ocsf["type_uid"]!.GetValue<int>());
        Assert.Equal(0, ocsf["initiator_id"]!.GetValue<int>());
        Assert.Equal(1, ocsf["connection_info"]!["direction_id"]!.GetValue<int>());
        Assert.False(ocsf["src_endpoint"]!.AsObject().ContainsKey("svc_name"));
        Assert.Contains(mapped.Unmapped, item => item.SourceField == "network.activity");
    }

    [Fact]
    public void Refused_attempt_stays_unknown()
    {
        var ocsf = Map(TestSupport.Fixture(), 1007, NetworkOutcome.Refused);
        Assert.Equal(0, ocsf["activity_id"]!.GetValue<int>());
    }

    [Fact]
    public void Unsupported_event_becomes_source_health()
    {
        var fixture = TestSupport.Fixture();
        var mapped = OcsfMapper.Map(
            fixture.Records.Single(value => value.EventRecordId == 1009),
            Context,
            NetworkOutcome.Unknown);

        Assert.Equal(RecordKind.SourceHealth, mapped.Kind);
        Assert.Null(mapped.OcsfJson);
        Assert.Null(mapped.ClassUid);
    }
}
